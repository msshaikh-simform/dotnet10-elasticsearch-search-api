using System.Diagnostics;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using ProductSearch.Core;

// SQL Server LIKE '%term%' vs Elasticsearch, on identical data.
// Reports p50/p95/p99 and throughput at several concurrency levels, because
// single-query latency hides the failure mode that actually bites in production.

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

var requests = ArgOr("--requests", 50);
int[] concurrency = [1, 5, 20];
string[] queries = ["wireless", "bluetooth headphone", "gaming laptop", "sony speaker", "portable studio"];

var sqlConn = Env.SqlConnectionString;

var sqlSvc = new SqlSearchService(sqlConn, commandTimeoutSeconds: 300);
var esSvc = new ElasticSearchService(new ElasticsearchClient(
    new ElasticsearchClientSettings(new Uri(Env.ElasticsearchUrl))
        .Authentication(new BasicAuthentication(Env.ElasticsearchUsername, Env.ElasticsearchPassword))
        .RequestTimeout(TimeSpan.FromMinutes(2))));

Console.WriteLine($"Dataset: 200,000 products | {requests} requests per level | query mix of {queries.Length}");
Console.WriteLine("Warming up...");
for (var i = 0; i < 5; i++)
{
    await sqlSvc.SearchAsync(queries[i % queries.Length], 20, 1, default);
    await esSvc.SearchAsync(new ProductSearchRequest { Q = queries[i % queries.Length], Size = 20 }, default);
}

var rows = new List<Row>();

foreach (var c in concurrency)
{
    Console.WriteLine($"\nConcurrency {c}...");
    rows.Add(await RunAsync("SQL Server", c, requests, queries,
        (q, ct) => sqlSvc.SearchAsync(q, 20, 1, ct)));
    Console.WriteLine($"  SQL done");
    rows.Add(await RunAsync("Elasticsearch", c, requests, queries,
        (q, ct) => esSvc.SearchAsync(new ProductSearchRequest { Q = q, Size = 20 }, ct)));
    Console.WriteLine($"  Elasticsearch done");
}

// The honest counter-example: an exact indexed lookup, where SQL Server wins.
Console.WriteLine("\nExact SKU lookup (concurrency 1)...");
var skuSql = await RunAsync("SQL Server", 1, requests, ["SKU-0100000"],
    (q, ct) => sqlSvc.GetBySkuAsync(q, ct));
var skuEs = await RunAsync("Elasticsearch", 1, requests, ["SKU-0100000"],
    (q, ct) => esSvc.SearchAsync(new ProductSearchRequest { Q = q, Size = 20 }, ct));

// ---- Report ----------------------------------------------------------------
Console.WriteLine("\n\n## Full-text search: `LIKE '%term%'` vs Elasticsearch\n");
Console.WriteLine("| Concurrency | Engine | p50 | p95 | p99 | Throughput | Failed |");
Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- |");
foreach (var r in rows)
    Console.WriteLine($"| {r.Concurrency} | {r.Engine} | {r.P50:N0} ms | {r.P95:N0} ms | {r.P99:N0} ms | {r.Throughput:N1} req/s | {r.Failures} |");

Console.WriteLine("\n## Exact SKU lookup (the case SQL Server wins)\n");
Console.WriteLine("| Engine | p50 | p95 | Throughput |");
Console.WriteLine("| --- | --- | --- | --- |");
foreach (var r in new[] { skuSql, skuEs })
    Console.WriteLine($"| {r.Engine} | {r.P50:N0} ms | {r.P95:N0} ms | {r.Throughput:N1} req/s |");

static async Task<Row> RunAsync(
    string engine, int concurrency, int requests, string[] queries,
    Func<string, CancellationToken, Task<SearchResults>> call)
{
    var latencies = new long[requests];
    var next = -1;
    var failures = 0;
    var sw = Stopwatch.StartNew();

    var workers = Enumerable.Range(0, concurrency).Select(async _ =>
    {
        while (true)
        {
            var i = Interlocked.Increment(ref next);
            if (i >= requests) return;

            var one = Stopwatch.StartNew();
            try
            {
                await call(queries[i % queries.Length], default);
            }
            catch
            {
                // A request that never completes is still a data point - arguably
                // the most important one.
                Interlocked.Increment(ref failures);
            }
            one.Stop();
            latencies[i] = one.ElapsedMilliseconds;
        }
    });

    await Task.WhenAll(workers);
    sw.Stop();

    Array.Sort(latencies);
    return new Row(engine, concurrency,
        Percentile(latencies, 50), Percentile(latencies, 95), Percentile(latencies, 99),
        requests / Math.Max(sw.Elapsed.TotalSeconds, 0.001), failures);
}

static long Percentile(long[] sorted, int p)
{
    if (sorted.Length == 0) return 0;
    var index = (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}

static int ArgOr(string name, int fallback)
{
    var args = Environment.GetCommandLineArgs();
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

internal readonly record struct Row(
    string Engine, int Concurrency, long P50, long P95, long P99, double Throughput, int Failures);
