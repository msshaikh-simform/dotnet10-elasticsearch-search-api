using System.Diagnostics;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Data.SqlClient;
using SourceFilter = Elastic.Clients.Elasticsearch.Core.Search.SourceFilter;

namespace ProductSearch.Api;

/// <summary>Raised when a request asks for a page beyond Elasticsearch's result window.</summary>
public sealed class SearchWindowExceededException(int requested, int limit)
    : Exception($"Requested result offset {requested} exceeds the index result window of {limit}.")
{
    public int Requested { get; } = requested;
    public int Limit { get; } = limit;
}

/// <summary>
/// The baseline every team starts with: LIKE '%term%' against SQL Server.
/// Deliberately written with raw ADO.NET rather than an ORM so the comparison
/// is fair - this is as fast as this approach can be made.
/// </summary>
public sealed class SqlSearchService(string connectionString, int commandTimeoutSeconds = 30)
{
    public async Task<SearchResults> SearchAsync(string term, int size, int page, CancellationToken ct)
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
            SELECT Id, Sku, Name, Brand, Price
            FROM Products
            WHERE {predicates}
            ORDER BY Name
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            """;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = commandTimeoutSeconds };
        cmd.Parameters.AddWithValue("@skip", (page - 1) * size);
        cmd.Parameters.AddWithValue("@take", size + 1); // one extra row reveals whether another page exists
        for (var i = 0; i < terms.Length; i++)
            cmd.Parameters.AddWithValue($"@p{i}", $"%{terms[i]}%");

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(new ProductHit(
                reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetDecimal(4)));

        sw.Stop();

        var hasMore = items.Count > size;
        if (hasMore) items.RemoveAt(items.Count - 1);

        return new SearchResults("sql", term, items.Count, page, size, hasMore,
            sw.ElapsedMilliseconds, null, items);
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
        return new SearchResults("sql", sku, items.Count, 1, items.Count, false,
            sw.ElapsedMilliseconds, null, items);
    }
}

/// <summary>The same feature built on Elasticsearch.</summary>
public sealed class ElasticSearchService(ElasticsearchClient client)
{
    /// <summary>Elasticsearch refuses from+size beyond this by default (index.max_result_window).</summary>
    public const int MaxResultWindow = 10_000;

    private static SourceConfig CardFields => new(new SourceFilter
    {
        Includes = Fields.FromExpressions<ProductDocument>(
            [f => f.Id, f => f.Sku, f => f.Name, f => f.Brand, f => f.Price])
    });

    public async Task<SearchResults> SearchAsync(ProductSearchRequest request, CancellationToken ct)
    {
        var from = (request.PageNumber - 1) * request.PageSize;

        // Fail with an explanation rather than letting Elasticsearch return a
        // confusing error. Deep paging needs search_after, not bigger windows.
        if (from + request.PageSize > MaxResultWindow)
            throw new SearchWindowExceededException(from + request.PageSize, MaxResultWindow);

        var sw = Stopwatch.StartNew();

        // Yes/no conditions go in filter context: no scoring, and eligible for caching.
        var filters = new List<Query>();
        if (!string.IsNullOrWhiteSpace(request.Brand))
            filters.Add(new TermQuery { Field = Infer.Field<ProductDocument>(f => f.Brand), Value = request.Brand });
        if (request.MaxPrice is not null)
            filters.Add(new NumberRangeQuery
            {
                Field = Infer.Field<ProductDocument>(f => f.Price),
                Lte = (double)request.MaxPrice
            });

        var response = await client.SearchAsync<ProductDocument>(s => s
            .Indices(ProductIndex.Alias)
            .From(from)
            .Size(request.PageSize + 1) // one extra hit reveals whether another page exists
            // The UI shows "20 results", not "20 of 41,382" - so don't pay for an exact count.
            .TrackTotalHits(new TrackHits(false))
            .Source(CardFields)
            .Query(q => q.Bool(b => b
                .Must(mu => mu.MultiMatch(mm =>
                {
                    mm.Query(request.Term).Fields(new[] { "name^3", "brand^2", "description" });
                    // Fuzziness is opt-in: it costs more and must never touch identifiers like Sku.
                    if (request.UseFuzzy) mm.Fuzziness(new Fuzziness("AUTO"));
                }))
                .Filter(filters)))
            .Aggregations(a => a
                .Add("by_brand", agg => agg.Terms(t => t.Field(f => f.Brand!).Size(10)))
                .Add("by_category", agg => agg.Terms(t => t.Field(f => f.Category!).Size(10)))),
            ct);

        sw.Stop();
        EnsureValid(response);

        var items = response.Documents.Select(ToHit).ToList();
        var hasMore = items.Count > request.PageSize;
        if (hasMore) items.RemoveAt(items.Count - 1);

        return new SearchResults("elasticsearch", request.Term, items.Count, request.PageNumber, request.PageSize,
            hasMore, sw.ElapsedMilliseconds, response.Took, items, ReadFacets(response, "by_brand"));
    }

    /// <summary>Autocomplete. SQL Server has no comparable answer at this speed.</summary>
    public async Task<SearchResults> SuggestAsync(string prefix, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        var response = await client.SearchAsync<ProductDocument>(s => s
            .Indices(ProductIndex.Alias)
            .Size(8)
            .TrackTotalHits(new TrackHits(false))
            .Source(CardFields)
            .Query(q => q.MultiMatch(mm => mm
                .Query(prefix)
                .Type(TextQueryType.BoolPrefix)
                .Fields(new[] { "name.suggest", "name.suggest._2gram", "name.suggest._3gram" }))),
            ct);

        sw.Stop();
        EnsureValid(response);

        var items = response.Documents.Select(ToHit).ToList();
        return new SearchResults("elasticsearch", prefix, items.Count, 1, 8, false,
            sw.ElapsedMilliseconds, response.Took, items);
    }

    /// <summary>Fetch a single product by id - the detail view behind every search result.</summary>
    public async Task<ProductHit?> GetByIdAsync(int id, CancellationToken ct)
    {
        var response = await client.GetAsync<ProductDocument>(id.ToString(),
            g => g.Index(ProductIndex.Alias).SourceIncludes(
                Fields.FromExpressions<ProductDocument>(
                    [f => f.Id, f => f.Sku, f => f.Name, f => f.Brand, f => f.Price])), ct);

        return response is { IsValidResponse: true, Found: true, Source: not null }
            ? ToHit(response.Source)
            : null;
    }

    private static ProductHit ToHit(ProductDocument d) =>
        new(d.Id, d.Sku, d.Name, d.Brand, (decimal)d.Price);

    private static void EnsureValid<T>(SearchResponse<T> response)
    {
        if (response.IsValidResponse) return;

        var reason = response.ElasticsearchServerError?.Error?.Reason ?? "Elasticsearch query failed";
        if (reason.Contains("index_not_found", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("no such index", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The '{ProductIndex.Alias}' index does not exist. Run the seeder first.");

        throw new InvalidOperationException(reason);
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
