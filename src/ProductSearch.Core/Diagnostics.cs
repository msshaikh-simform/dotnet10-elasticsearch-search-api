using Microsoft.Extensions.Logging;
using Elastic.Clients.Elasticsearch;
using Microsoft.Data.SqlClient;

namespace ProductSearch.Core;

/// <summary>
/// Checks every dependency and reports what is wrong in terms a developer can act on.
/// The goal is that nobody ever stares at an empty result list wondering why.
/// </summary>
public sealed class Diagnostics(ElasticsearchClient client, string sqlConnectionString)
{
    public async Task<HealthReport> CheckAsync(CancellationToken ct = default)
    {
        var checks = new List<DependencyStatus>();
        long indexed = 0;

        // ---- Elasticsearch reachable? -----------------------------------------
        var esOk = false;
        try
        {
            var ping = await client.PingAsync(ct);
            esOk = ping.IsValidResponse;
            checks.Add(esOk
                ? new DependencyStatus("Elasticsearch", true, $"reachable at {Env.ElasticsearchUrl}")
                : new DependencyStatus("Elasticsearch", false,
                    ping.ElasticsearchServerError?.Error?.Reason ?? "ping failed",
                    "Is the container running and finished starting? Elasticsearch needs roughly 60 seconds. Try: docker compose up -d"));
        }
        catch (Exception ex)
        {
            checks.Add(new DependencyStatus("Elasticsearch", false, Describe(ex),
                $"Cannot reach {Env.ElasticsearchUrl}. Start it with: docker compose up -d"));
        }

        // ---- Index exists, and does it have anything in it? --------------------
        if (esOk)
        {
            try
            {
                var exists = await client.Indices.ExistsAsync(ProductIndex.Alias, ct);
                if (!exists.Exists)
                {
                    checks.Add(new DependencyStatus("Product index", false,
                        $"index/alias '{ProductIndex.Alias}' does not exist",
                        "Run the seeder: dotnet run --project src/ProductSearch.Seeder -c Release -- 200000"));
                }
                else
                {
                    var count = await client.CountAsync<ProductDocument>(c => c.Indices(ProductIndex.Alias), ct);
                    indexed = count.Count;
                    checks.Add(indexed > 0
                        ? new DependencyStatus("Product index", true, $"{indexed:N0} documents indexed")
                        : new DependencyStatus("Product index", false, "index exists but is empty",
                            "Run the seeder: dotnet run --project src/ProductSearch.Seeder -c Release -- 200000"));
                }
            }
            catch (Exception ex)
            {
                checks.Add(new DependencyStatus("Product index", false, Describe(ex),
                    "Run the seeder: dotnet run --project src/ProductSearch.Seeder -c Release -- 200000"));
            }
        }

        // ---- SQL Server reachable, and is the table there? ---------------------
        try
        {
            await using var conn = new SqlConnection(sqlConnectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(
                "SELECT COUNT(*) FROM sys.tables WHERE name = 'Products';", conn) { CommandTimeout = 5 };
            var tables = (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);

            checks.Add(tables > 0
                ? new DependencyStatus("SQL Server", true, "reachable, Products table present")
                : new DependencyStatus("SQL Server", false, "connected, but the Products table is missing",
                    "Run the seeder: dotnet run --project src/ProductSearch.Seeder -c Release -- 200000"));
        }
        catch (Exception ex)
        {
            checks.Add(new DependencyStatus("SQL Server", false, Describe(ex),
                "SQL Server starts more slowly than Elasticsearch - give it a moment, then retry. Start it with: docker compose up -d"));
        }

        var ok = checks.TrueForAll(c => c.Ok);
        return new HealthReport(ok, indexed > 0, indexed, checks);
    }

    /// <summary>Prints the same information to the console at startup.</summary>
    public async Task ReportToConsoleAsync(ILogger logger, CancellationToken ct = default)
    {
        var report = await CheckAsync(ct);

        Console.WriteLine();
        Console.WriteLine("  Startup check");
        Console.WriteLine("  ─────────────────────────────────────────────────────────────");
        foreach (var c in report.Dependencies)
        {
            Console.WriteLine($"   {(c.Ok ? "[ OK ]" : "[FAIL]")}  {c.Name,-16} {c.Detail}");
            if (!c.Ok && c.Hint is not null)
                Console.WriteLine($"           -> {c.Hint}");
        }
        Console.WriteLine("  ─────────────────────────────────────────────────────────────");

        if (report.Ok)
            Console.WriteLine("  Ready. Open http://localhost:5080");
        else if (!report.Seeded)
            Console.WriteLine("  The API will start, but searches return nothing until you seed.");
        else
            Console.WriteLine("  The API will start, but some dependencies are unavailable.");
        Console.WriteLine();
    }

    /// <summary>Unwraps the message people actually need, not the whole chain.</summary>
    private static string Describe(Exception ex) =>
        ex is SqlException sql
            ? sql.Number switch
            {
                18456 => "login failed - SQL Server may still be starting up",
                -2 => "connection timed out",
                _ => sql.Message.Split('\n')[0]
            }
            : (ex.InnerException ?? ex).Message.Split('\n')[0];
}
