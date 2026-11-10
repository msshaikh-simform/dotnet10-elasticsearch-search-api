# Benchmark results

**Dataset:** 200,000 products, identical in all three engines (SQL Server is the source of truth).
**Query mix:** `wireless`, `bluetooth headphone`, `gaming laptop`, `sony speaker`, `portable studio`.
**Method:** 40 requests per concurrency level after a warm-up pass, 20 results returned, latency measured per
request. **Each engine is measured separately** — the demo page fires all three at once, which is useful to
watch but useless to measure.
**Hardware:** Intel Core i5-1135G7 (4 cores / 8 threads), 16 GB RAM, Windows 10. All three engines, the load
generator and Docker Desktop (8 CPUs, 8 GB) on one laptop. Absolute numbers are therefore indicative rather
than production figures — but every engine faced identical data under identical conditions.

Reproduce with:

```powershell
dotnet run --project src/ProductSearch.Benchmark -c Release -- --requests 40
```

## The three engines

| Engine | What it is | Why it is here |
| --- | --- | --- |
| **SQL Server `LIKE`** | `WHERE (Name LIKE '%a%' OR Description LIKE '%a%') AND (...)` | The baseline almost every catalog starts with |
| **SQL Server Full-Text** | `FREETEXTTABLE(Products, (Name, Description), @term)` ordered by `RANK` | The strongest option *inside* the database — the one a reader who knows SQL Server will ask about |
| **Elasticsearch** | `multi_match` over `name^3`, `brand.text^2`, `description` | The purpose-built search engine |

**The `LIKE` baseline is deliberately steelmanned.** Raw ADO.NET, not an ORM, and it splits the query into
terms and requires all of them rather than searching for one contiguous phrase. It is as fast as that approach
can be made.

**Full-text is not a strawman either.** It uses a real index, matches inflectional forms, and returns a
relevance rank. Where it loses, it loses on capability rather than on effort.

## Full-text search

| Concurrency | Engine | p50 | p95 | p99 | Throughput | Failed |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | SQL `LIKE` | 5,621 ms | 8,778 ms | 16,112 ms | 0.2 req/s | 0 |
| 1 | SQL Full-Text | 381 ms | 727 ms | 817 ms | 2.4 req/s | 0 |
| 1 | **Elasticsearch** | **39 ms** | 63 ms | 76 ms | **25.3 req/s** | 0 |
| 5 | SQL `LIKE` | 24,074 ms | 50,849 ms | 51,192 ms | 0.2 req/s | 0 |
| 5 | SQL Full-Text | 1,225 ms | 3,664 ms | 3,882 ms | 3.0 req/s | 0 |
| 5 | **Elasticsearch** | **57 ms** | 111 ms | 119 ms | **77.2 req/s** | 0 |
| 20 | SQL `LIKE` | 126,779 ms | 147,511 ms | 149,618 ms | 0.1 req/s | 0 |
| 20 | SQL Full-Text | 10,945 ms | 25,027 ms | 25,753 ms | 1.5 req/s | 0 |
| 20 | **Elasticsearch** | **195 ms** | 372 ms | 428 ms | **85.5 req/s** | 0 |

**Throughput is the column that matters.** `LIKE` sits at 0.1–0.2 requests per second no matter how many users
arrive — every extra user simply queues. Full-text search is roughly **15× faster** than `LIKE` and uses an
index instead of scanning, but its throughput is flat too, between 1.5 and 3.0 req/s. Only Elasticsearch
answers more requests as more arrive, climbing from 25 to 85 req/s.

That is the real distinction, and it is not "SQL Server is slow". It is that one of these three scales with
load and the other two have a fixed ceiling they reach immediately.

### Repeatability

The table above is one run. A second run on an idle machine agreed closely at low concurrency and varied at
high concurrency:

| Measurement | Run A | Run B | Spread |
| --- | --- | --- | --- |
| Elasticsearch p50 @ 1 | 37 ms | 39 ms | 5% |
| Full-Text p50 @ 1 | 343 ms | 381 ms | 10% |
| Elasticsearch p50 @ 5 | 70 ms | 57 ms | 20% |
| `LIKE` p50 @ 20 | 102,322 ms | 126,779 ms | **24%** |

Multi-second scans saturate every core, so the `LIKE` figures are the noisiest; treat them as "around two
minutes at twenty users", not as 126,779 ms. None of the conclusions depend on that spread.

### One honest confound

The full-text index lives on the same `Products` table that `LIKE` scans, and both compete for the same buffer
pool. Adding it may have made the `LIKE` numbers slightly worse than they were when it did not exist. The
effect is small next to a 300× gap, but it is real and worth naming rather than quietly absorbing.

### On the zero-failures column

These runs used a 300-second command timeout so that real latency could be measured. With ADO.NET's default
30-second timeout, *every* `LIKE` request at concurrency 20 fails. That is not hypothetical — it is what the
first run of this harness actually did.

## Exact SKU lookup — the case SQL Server wins

| Engine | p50 | p95 | Throughput |
| --- | --- | --- | --- |
| **SQL Server** (unique index on `Sku`) | **4 ms** | 5 ms | **221.5 req/s** |
| Elasticsearch (`term` query on `sku`) | 12 ms | 29 ms | 62.6 req/s |

Given an indexed identifier and an exact value, the relational database is the right tool — about **three times
faster** on latency and three and a half times the throughput.

> **This number used to be wrong, in SQL Server's favour.** An earlier version of this harness sent the SKU
> through the full-text search path — a scored `multi_match` over `name`, `brand` and `description` plus two
> aggregations. `sku` is not one of those fields, and the product descriptions contain no such token, so the
> query matched *nothing*. The published figure was the cost of a search that found no product, and it made
> SQL Server look eleven times faster instead of three. Measuring the right query cut the gap by 4×.

Elasticsearch is not a replacement for your database. It is a specialised read model for the queries a
database is bad at.

## What latency alone does not show

Speed is only half the gap. On identical data:

| Query | `LIKE` | SQL Full-Text | Elasticsearch |
| --- | --- | --- | --- |
| `wireless headphone` | 20 results | 20 results | 20 results |
| `wireless headphones` (plural) | **0 results** | 20 results | 20 results |
| `wirless headphone` (typo) | **0 results** | 20 results * | 20 results |
| `notebook` (synonym) | **0 results** | **0 results** | 20 results |

**Full-text search closes the plural gap, and only that one.** `FREETEXT` matches inflectional forms, so
"headphones" finds "headphone" — exactly what `LIKE` cannot do at any amount of tuning.

**\* The typo row needs care.** Full-text has no edit distance. `FREETEXT` treats its terms as OR, so
`wirless headphone` returns results purely because "headphone" matches; `wirless` on its own returns **0**.
That is graceful degradation, not typo tolerance.

**The synonym row is where it stops.** SQL Server supports a thesaurus, but it ships empty — it needs an XML
file per language and a reload before `FORMSOF(THESAURUS, …)` does anything. Elasticsearch takes a synonym
list inline in the analyzer, applied at search time, so changing it needs no reindex.

And facet counts come back in the same request as the results, where SQL Server would need a separate
aggregate pass per filter over the same data.

### Why these examples and not the obvious ones

- **`wireles` fails as a typo test** — it is a *substring* of "Wireless", so `LIKE '%wireles%'` matches it by
  coincidence and proves nothing. `wirless` is a genuine misspelling that is a substring of nothing.
- **`television` fails as a synonym test** — products are literally named "Television", so every engine
  matches directly and the `tv, television` synonym is never exercised. `notebook` appears nowhere in the
  data, so only a synonym can find it.

## Reproducing

Stop the API first (Ctrl+C) so it does not compete for CPU, then:

```powershell
docker compose up -d --build
dotnet run --project src/ProductSearch.Seeder    -c Release -- 200000
dotnet run --project src/ProductSearch.Benchmark -c Release -- --requests 40
```

The seeder is deterministic — fixed Bogus seed `20260912` — so the same catalog is generated every time.
