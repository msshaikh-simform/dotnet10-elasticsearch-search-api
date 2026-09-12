using System.Diagnostics;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Data.SqlClient;

namespace ProductSearch.Api;

/// <summary>
/// The baseline every team starts with: LIKE '%term%' against SQL Server.
/// Deliberately written with raw ADO.NET rather than an ORM so the comparison
/// is fair - this is as fast as this approach can be made.
/// </summary>
public sealed class SqlSearchService(string connectionString, int commandTimeoutSeconds = 30)
{
    public async Task<SearchResults> SearchAsync(string term, int size, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var items = new List<ProductHit>();

        // Split the query into terms and require all of them. A single
        // LIKE '%wireless headphones%' would demand that exact contiguous phrase
        // and match almost nothing, so this is the fairer baseline - it is what a
        // competent developer writes before reaching for a search engine.
        var terms = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var predicates = string.Join(" AND ", terms.Select((_, i) =>
            $"(Name LIKE @p{i} OR Description LIKE @p{i})"));

        var sql = $"""
            SELECT TOP (@size) Id, Sku, Name, Brand, Price
            FROM Products
            WHERE {predicates}
            ORDER BY Name;
            """;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = commandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@size", size);
        for (var i = 0; i < terms.Length; i++)
            cmd.Parameters.AddWithValue($"@p{i}", $"%{terms[i]}%");

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new ProductHit(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetDecimal(4)));

        sw.Stop();
        return new SearchResults("sql", term, items.Count, sw.ElapsedMilliseconds, null, items);
    }

    /// <summary>The case SQL Server wins: an exact indexed lookup.</summary>
    public async Task<SearchResults> GetBySkuAsync(string sku, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var items = new List<ProductHit>();

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT Id, Sku, Name, Brand, Price FROM Products WHERE Sku = @sku;", conn);
        cmd.Parameters.AddWithValue("@sku", sku);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new ProductHit(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetDecimal(4)));

        sw.Stop();
        return new SearchResults("sql", sku, items.Count, sw.ElapsedMilliseconds, null, items);
    }
}

/// <summary>The same feature built on Elasticsearch.</summary>
public sealed class ElasticSearchService(ElasticsearchClient client)
{
    public async Task<SearchResults> SearchAsync(
        string term, string? brand, decimal? maxPrice, bool fuzzy, int size, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Yes/no conditions go in filter context: no scoring, and eligible for caching.
        var filters = new List<Query>();
        if (!string.IsNullOrWhiteSpace(brand))
            filters.Add(new TermQuery { Field = Infer.Field<ProductDocument>(f => f.Brand), Value = brand });
        if (maxPrice is not null)
            filters.Add(new NumberRangeQuery
            {
                Field = Infer.Field<ProductDocument>(f => f.Price),
                Lte = (double)maxPrice
            });

        var response = await client.SearchAsync<ProductDocument>(s => s
            .Indices(ProductIndex.Alias)
            .Size(size)
            // The UI shows "20 results", not "20 of 41,382" - so don't pay for an exact count.
            .TrackTotalHits(new TrackHits(false))
            // Return only the fields the result card renders.
            .Source(new SourceConfig(new Elastic.Clients.Elasticsearch.Core.Search.SourceFilter
            {
                Includes = Fields.FromExpressions<ProductDocument>(
                    [f => f.Id, f => f.Sku, f => f.Name, f => f.Brand, f => f.Price])
            }))
            .Query(q => q.Bool(b => b
                .Must(mu => mu.MultiMatch(mm =>
                {
                    mm.Query(term).Fields(new[] { "name^3", "brand^2", "description" });
                    // Fuzziness is opt-in: it costs more and it must never be
                    // applied to identifiers like Sku.
                    if (fuzzy) mm.Fuzziness(new Fuzziness("AUTO"));
                }))
                .Filter(filters)))
            .Aggregations(a => a
                .Add("by_brand", agg => agg.Terms(t => t.Field(f => f.Brand!).Size(10)))
                .Add("by_category", agg => agg.Terms(t => t.Field(f => f.Category!).Size(10)))),
            ct);

        sw.Stop();

        if (!response.IsValidResponse)
            throw new InvalidOperationException(
                response.ElasticsearchServerError?.Error?.Reason ?? "Elasticsearch query failed");

        var items = response.Documents
            .Select(d => new ProductHit(d.Id, d.Sku, d.Name, d.Brand, (decimal)d.Price))
            .ToList();

        return new SearchResults("elasticsearch", term, items.Count,
            sw.ElapsedMilliseconds, response.Took, items, ReadFacets(response, "by_brand"));
    }

    /// <summary>Autocomplete. SQL Server has no comparable answer at this speed.</summary>
    public async Task<SearchResults> SuggestAsync(string prefix, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var response = await client.SearchAsync<ProductDocument>(s => s
            .Indices(ProductIndex.Alias)
            .Size(8)
            .TrackTotalHits(new TrackHits(false))
            .Source(new SourceConfig(new Elastic.Clients.Elasticsearch.Core.Search.SourceFilter
            {
                Includes = Fields.FromExpressions<ProductDocument>(
                    [f => f.Id, f => f.Sku, f => f.Name, f => f.Brand, f => f.Price])
            }))
            .Query(q => q.MultiMatch(mm => mm
                .Query(prefix)
                .Type(TextQueryType.BoolPrefix)
                .Fields(new[] { "name.suggest", "name.suggest._2gram", "name.suggest._3gram" }))),
            ct);

        sw.Stop();

        var items = response.Documents
            .Select(d => new ProductHit(d.Id, d.Sku, d.Name, d.Brand, (decimal)d.Price))
            .ToList();

        return new SearchResults("elasticsearch", prefix, items.Count,
            sw.ElapsedMilliseconds, response.Took, items);
    }

    private static Dictionary<string, long> ReadFacets(SearchResponse<ProductDocument> response, string name)
    {
        var facets = new Dictionary<string, long>();
        if (response.Aggregations?.GetStringTerms(name) is { } terms)
            foreach (var bucket in terms.Buckets)
                facets[bucket.Key.ToString()] = bucket.DocCount;
        return facets;
    }
}
