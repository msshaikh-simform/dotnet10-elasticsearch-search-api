# Benchmark results

**Dataset:** 200,000 products, identical in both engines (SQL Server is the source of truth).
**Query mix:** `wireless`, `bluetooth headphone`, `gaming laptop`, `sony speaker`, `portable studio`.
**Method:** 40 requests per concurrency level after warmup, 20 results returned, latency measured per request.
**Hardware:** Intel Core i5-1135G7 (4 cores / 8 threads), 16 GB RAM, Windows 10. Both engines, the load
generator and Docker Desktop (8 CPUs, 8 GB) all ran on this one laptop. Absolute numbers are therefore
indicative rather than production figures — but both engines faced identical data and identical conditions.

**The SQL baseline is deliberately steelmanned.** It is raw ADO.NET, not an ORM, and it splits the query into
terms and requires all of them (`(Name LIKE @p0 OR Description LIKE @p0) AND (...)`) rather than searching for
one contiguous phrase. This is what a competent developer writes before reaching for a search engine.

## Full-text search

| Concurrency | Engine | p50 | p95 | p99 | Throughput |
| --- | --- | --- | --- | --- | --- |
| 1 | SQL Server | 1,709 ms | 2,081 ms | 2,357 ms | 0.6 req/s |
| 1 | Elasticsearch | **18 ms** | 34 ms | 146 ms | **41.2 req/s** |
| 5 | SQL Server | 9,194 ms | 9,756 ms | 9,937 ms | 0.6 req/s |
| 5 | Elasticsearch | **29 ms** | 45 ms | 67 ms | **153.2 req/s** |
| 20 | SQL Server | 41,572 ms | 43,995 ms | 44,753 ms | 0.5 req/s |
| 20 | Elasticsearch | **76 ms** | 289 ms | 297 ms | **125.0 req/s** |

That is roughly **95x at a single user and 550x at twenty**, but the latency column is not the important one.

**Look at throughput.** SQL Server sits at 0.5-0.6 requests per second no matter how many users arrive — every
extra user simply queues. Elasticsearch goes from 41 to 153 requests per second as concurrency rises. One
engine scales with load; the other has a fixed ceiling and hits it immediately.

The reason is visible in `SET STATISTICS IO/TIME`: a single one of these queries reads **19,116 pages, about
149 MB - the whole table** - and burns **8,499 ms of CPU** (parallelised across 9 threads) to return 20 rows, because the work is proportional to
rows *stored*, not rows *matched*. At twenty concurrent users, p99 is **45 seconds** — every one of those
requests is a timeout in any real application.

**On the zero failures column:** these runs used a 300-second command timeout so real latency could be
measured. With ADO.NET's default 30-second timeout, *every* SQL Server request at concurrency 20 fails. That
is not hypothetical — it is what the first run of this harness actually did.

## Exact SKU lookup — the case SQL Server wins

| Engine | p50 | p95 | Throughput |
| --- | --- | --- | --- |
| SQL Server | **1 ms** | 2 ms | **499.6 req/s** |
| Elasticsearch | 11 ms | 13 ms | 90.0 req/s |

Given an indexed identifier and an exact value, the relational database is the right tool and is about eleven
times faster. Elasticsearch is not a replacement for your database — it is a specialised read model for the
queries a database is bad at.

## What latency alone does not show

Speed is only half the gap. On identical data:

| Query | SQL Server | Elasticsearch |
| --- | --- | --- |
| `wireless headphone` | 20 results, ~2,800 ms | 20 results, ~19 ms |
| `wireless headphones` | **0 results**, ~1,300 ms | 20 results, ~30 ms |
| `wirless headphone` (typo) | **0 results** | 20 results |
| `notebook` (synonym) | **0 results** | 20 results |

The second row is the one to sit with. Product names contain "Headphone"; a user typing the plural gets
**nothing at all** from `LIKE`, after more than a second of scanning. Elasticsearch stems both sides to `headphon` and
matches. No amount of tuning fixes that — `LIKE` has no concept of word forms.

The same applies to typo tolerance: `wirless headphone` returns **0 rows** in SQL and 20 in Elasticsearch. (A
note on choosing that example - `wireles` does *not* work as a test, because it is a substring of "Wireless"
and `LIKE` matches it by coincidence.) Synonyms behave the same way: `notebook` appears nowhere in the data,
so SQL returns nothing while Elasticsearch expands it to `laptop`. And facet counts come back in the same
request, where SQL Server would need a separate `GROUP BY` scan per filter.

## Reproducing

Stop the API first (Ctrl+C) so it does not compete for CPU, then:

```powershell
docker compose up -d
dotnet run --project src/ProductSearch.Seeder    -c Release -- 200000
dotnet run --project src/ProductSearch.Benchmark -c Release -- --requests 40
```
