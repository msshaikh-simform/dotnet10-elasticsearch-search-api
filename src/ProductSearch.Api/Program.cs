using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using ProductSearch.Api;

var builder = WebApplication.CreateBuilder(args);

// Configuration wins when present; otherwise fall back to the development
// defaults in Env. Nothing secret lives in source.
var esUrl   = builder.Configuration["Elasticsearch:Url"]      ?? Env.ElasticsearchUrl;
var esUser  = builder.Configuration["Elasticsearch:Username"] ?? Env.ElasticsearchUsername;
var esPass  = builder.Configuration["Elasticsearch:Password"] ?? Env.ElasticsearchPassword;
var sqlConn = builder.Configuration.GetConnectionString("SqlServer") ?? Env.SqlConnectionString;

// The client is thread-safe and owns a connection pool, so it is registered once.
builder.Services.AddSingleton(_ =>
{
    var settings = new ElasticsearchClientSettings(new Uri(esUrl))
        .Authentication(new BasicAuthentication(esUser, esPass))
        .DefaultIndex(ProductIndex.Alias)
        .RequestTimeout(TimeSpan.FromSeconds(10));

    return new ElasticsearchClient(settings);
});

builder.Services.AddSingleton(new SqlSearchService(sqlConn));
builder.Services.AddSingleton<ElasticSearchService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ---- The comparison: the same feature, two engines -------------------------

app.MapGet("/api/search/sql", async (
    string q, int? size, SqlSearchService sql, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(q)
            ? Results.BadRequest("Query 'q' is required.")
            : Results.Ok(await sql.SearchAsync(q, size ?? 20, ct)));

app.MapGet("/api/search/elastic", async (
    string q, string? brand, decimal? maxPrice, bool? fuzzy, int? size,
    ElasticSearchService es, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(q)
            ? Results.BadRequest("Query 'q' is required.")
            : Results.Ok(await es.SearchAsync(q, brand, maxPrice, fuzzy ?? false, size ?? 20, ct)));

// Autocomplete has no SQL counterpart worth shipping.
app.MapGet("/api/suggest", async (
    string q, ElasticSearchService es, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(q)
            ? Results.Ok(new { items = Array.Empty<ProductHit>() })
            : Results.Ok(await es.SuggestAsync(q, ct)));

// The honest case: an exact indexed lookup, where SQL Server wins.
app.MapGet("/api/lookup/sql", async (
    string sku, SqlSearchService sql, CancellationToken ct) =>
        Results.Ok(await sql.GetBySkuAsync(sku, ct)));

app.MapGet("/api/health", async (ElasticsearchClient client, CancellationToken ct) =>
{
    var ping = await client.PingAsync(ct);
    return Results.Ok(new { elasticsearch = ping.IsValidResponse });
});

app.Run();
