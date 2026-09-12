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
| 1 | SQL Server | 2,527 ms | 2,798 ms | 2,946 ms | 0.4 req/s |
| 1 | Elasticsearch | **17 ms** | 24 ms | 31 ms | **54.2 req/s** |
| 5 | SQL Server | 16,526 ms | 25,148 ms | 25,679 ms | 0.3 req/s |
| 5 | Elasticsearch | **30 ms** | 52 ms | 72 ms | **143.4 req/s** |
| 20 | SQL Server | 75,747 ms | 91,613 ms | 92,212 ms | 0.3 req/s |
| 20 | Elasticsearch | **91 ms** | 292 ms | 304 ms | **128.2 req/s** |

That is roughly **150× at a single user and 830× at twenty**, but the latency column is not the important one.

**Look at throughput.** SQL Server sits at 0.3–0.4 requests per second no matter how many users arrive — every
extra user simply queues. Elasticsearch goes from 54 to 143 requests per second as concurrency rises. One
engine scales with load; the other has a fixed ceiling and hits it immediately.

The reason is visible in `SET STATISTICS TIME`: a single one of these queries burns **12–13 seconds of CPU**
(SQL Server parallelises the scan across every core) to return 20 rows, because the work is proportional to
rows *stored*, not rows *matched*. At twenty concurrent users, p99 is **92 seconds** — every one of those
requests is a timeout in any real application.

**On the zero failures column:** these runs used a 300-second command timeout so real latency could be
measured. With ADO.NET's default 30-second timeout, *every* SQL Server request at concurrency 20 fails. That
is not hypothetical — it is what the first run of this harness actually did.

## Exact SKU lookup — the case SQL Server wins

| Engine | p50 | p95 | Throughput |
| --- | --- | --- | --- |
| SQL Server | **2 ms** | 3 ms | **189.4 req/s** |
| Elasticsearch | 11 ms | 13 ms | 86.4 req/s |

Given an indexed identifier and an exact value, the relational database is the right tool and is about five
times faster. Elasticsearch is not a replacement for your database — it is a specialised read model for the
queries a database is bad at.

## What latency alone does not show

Speed is only half the gap. On identical data:

| Query | SQL Server | Elasticsearch |
| --- | --- | --- |
| `wireless` | 3 results, 1,453 ms | 3 results, 17 ms |
| `wireless headphone` | 3 results, 1,920 ms | 3 results, 14 ms |
| `wireless headphones` | **0 results**, 2,036 ms | 3 results, 13 ms |
| `gaming laptop` | 3 results, 1,962 ms | 3 results, 63 ms |

The third row is the one to sit with. Product names contain "Headphone"; a user typing the plural gets
**nothing at all** from `LIKE`, after a two-second wait. Elasticsearch stems both sides to `headphon` and
matches. No amount of tuning fixes that — `LIKE` has no concept of word forms.

The same applies to typo tolerance (`wireles` → nothing in SQL), and to facet counts, which Elasticsearch
returns in the same request while SQL Server needs a separate `GROUP BY` scan per filter.

## Reproducing

```bash
docker compose up -d
dotnet run --project src/ProductSearch.Seeder    -c Release -- 200000
dotnet run --project src/ProductSearch.Benchmark -c Release -- --requests 40
```
