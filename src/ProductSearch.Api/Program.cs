using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ProductSearch.Core;

// Numbers format identically regardless of the machine locale.
System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

// Configuration wins when present; otherwise fall back to the development
// defaults in Env. Nothing secret lives in source.
var esUrl   = builder.Configuration["Elasticsearch:Url"]      ?? Env.ElasticsearchUrl;
var esUser  = builder.Configuration["Elasticsearch:Username"] ?? Env.ElasticsearchUsername;
var esPass  = builder.Configuration["Elasticsearch:Password"] ?? Env.ElasticsearchPassword;
var sqlConn = builder.Configuration.GetConnectionString("SqlServer") ?? Env.SqlConnectionString;

var esApiKey = builder.Configuration["Elasticsearch:ApiKey"];

// Outside Development, refuse to start rather than quietly connecting as the
// built-in superuser with a default password. A misconfigured deployment should
// fail at startup, not run on 'elastic'/'changeme'.
if (!builder.Environment.IsDevelopment() && string.IsNullOrEmpty(esApiKey))
    throw new InvalidOperationException(
        "Elasticsearch:ApiKey must be configured outside Development. "
        + "Create a key scoped to read on the 'products' alias - the superuser fallback is for the local container only.");

// The client is thread-safe and owns a connection pool, so it is registered once.
builder.Services.AddSingleton(_ =>
{
    var settings = new ElasticsearchClientSettings(new Uri(esUrl))
        .Authentication(string.IsNullOrEmpty(esApiKey)
            ? new BasicAuthentication(esUser, esPass)   // local container only
            : new ApiKey(esApiKey))                     // scoped key everywhere else
        .DefaultIndex(ProductIndex.Alias)
        .RequestTimeout(TimeSpan.FromSeconds(10));

    if (builder.Environment.IsDevelopment())
        settings.DisableDirectStreaming();

    return new ElasticsearchClient(settings);
});

builder.Services.AddSingleton(new SqlSearchService(sqlConn));
builder.Services.AddSingleton(new SqlFullTextSearchService(sqlConn));
builder.Services.AddSingleton<ElasticSearchService>();
builder.Services.AddSingleton(sp => new Diagnostics(
    sp.GetRequiredService<ElasticsearchClient>(), sqlConn, esUrl));

// .NET 10: validation attributes on the request record are enforced automatically,
// so an invalid request never reaches the handler.
builder.Services.AddValidation();

// .NET 10: OpenAPI 3.1 documents by default, with XML comments folded in.
builder.Services.AddOpenApi();

// Structured errors instead of stack traces.
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;

    // Every failure leaves a server-side trace. Without this, a 500 told the
    // caller something and told the operator nothing.
    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
    logger.LogError(error, "Request {Method} {Path} failed", context.Request.Method, context.Request.Path);

    var (status, title, detail) = error switch
    {
        SearchWindowExceededException w => (
            StatusCodes.Status400BadRequest,
            "Result window exceeded",
            $"{w.Message} Deep paging needs search_after with a point-in-time rather than a larger window."),

        // Matched on type, not on the wording of a message.
        SearchIndexMissingException e => (
            StatusCodes.Status503ServiceUnavailable,
            "Search index missing",
            e.Message + " Run: dotnet run --project src/ProductSearch.Seeder -c Release -- 200000"),

        Microsoft.Data.SqlClient.SqlException { Number: -2 } => (
            StatusCodes.Status504GatewayTimeout,
            "SQL Server query timed out",
            "Expected above roughly ten concurrent users - a LIKE '%term%' scan cannot keep up. "
            + "This is the finding the benchmark demonstrates, not a defect."),

        Microsoft.Data.SqlClient.SqlException => (
            StatusCodes.Status503ServiceUnavailable,
            "SQL Server unavailable",
            "Is the container running and finished starting? Try: docker compose up -d"),

        _ when error?.GetType().Namespace?.StartsWith("Elastic") == true => (
            StatusCodes.Status503ServiceUnavailable,
            "Elasticsearch unavailable",
            // The URL actually in use, not the environment default - configuration
            // may have overridden it, and a hint naming the wrong host wastes time.
            $"Cannot reach {esUrl}. Elasticsearch needs roughly 60 seconds to start. "
            + "Try: docker compose up -d"),

        BadHttpRequestException bad => (
            StatusCodes.Status400BadRequest,
            "Invalid request",
            bad.Message),

        // Nothing internal goes to the caller. The traceId is the link between
        // what they can quote and what the log holds.
        _ => (StatusCodes.Status500InternalServerError, "Unexpected error",
            "An unexpected error occurred. Quote the traceId when reporting it.")
    };

    await Results.Problem(title: title, detail: detail, statusCode: status)
        .ExecuteAsync(context);
}));

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenApi();

// ---- The comparison: the same feature, two engines -------------------------

// Search the catalog with Elasticsearch. The primary endpoint.
app.MapGet("/api/search", async (
    [AsParameters] ProductSearchRequest request, ElasticSearchService es, CancellationToken ct) =>
        TypedResults.Ok(await es.SearchAsync(request, ct)))
    .WithName("Search")
    .WithSummary("Search products using Elasticsearch")
    .WithDescription("Full-text search with filters, facet counts, optional typo tolerance and paging.")
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// The SQL Server baseline, kept only so the two can be compared.
app.MapGet("/api/search/sql", async (
    [AsParameters] ProductSearchRequest request, SqlSearchService sql, CancellationToken ct) =>
        TypedResults.Ok(await sql.SearchAsync(request.Term, request.PageSize, request.PageNumber, ct)))
    .WithName("SearchSql")
    .WithSummary("Search products using SQL Server LIKE (baseline)")
    .WithDescription("Comparison baseline only. Scans every row; no ranking, facets or typo tolerance.")
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status504GatewayTimeout);

// The stronger SQL Server baseline: Full-Text Search rather than LIKE.
app.MapGet("/api/search/sqlfts", async (
    [AsParameters] ProductSearchRequest request, SqlFullTextSearchService fts, CancellationToken ct) =>
{
    if (!await fts.IsAvailableAsync(ct))
        return Results.Problem(
            title: "Full-text search unavailable",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: "SQL Server Full-Text Search is not installed, or the index has not been built. "
                  + "Rebuild the container image with: docker compose up -d --build, then re-run the seeder.");

    return Results.Ok(await fts.SearchAsync(request.Term, request.PageSize, request.PageNumber, ct));
})
    .WithName("SearchSqlFullText")
    .WithSummary("Search products using SQL Server Full-Text Search")
    .WithDescription("The stronger SQL baseline: FREETEXT matches inflectional forms and orders by RANK. "
                   + "No typo tolerance and no facet counts in the same request.")
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Type-ahead suggestions.
app.MapGet("/api/suggest", async (
    [AsParameters] SuggestRequest request, ElasticSearchService es, CancellationToken ct) =>
        TypedResults.Ok(await es.SuggestAsync(request.Term, ct)))
    .WithName("Suggest")
    .WithSummary("Autocomplete suggestions")
    .WithDescription("Prefix matching over a search_as_you_type field, fast enough to run on every keystroke.")
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

// Fetch one product - the detail view behind a search result.
app.MapGet("/api/products/{id:int}", async (
    int id, ElasticSearchService es, CancellationToken ct) =>
        await es.GetByIdAsync(id, ct) is { } hit
            ? Results.Ok(hit)
            : Results.Problem(title: "Product not found", statusCode: StatusCodes.Status404NotFound,
                detail: $"No product with id {id}. Ids run from 1 to the number of seeded products."))
    .WithName("GetProduct")
    .WithSummary("Get a single product by id");

// Exact indexed lookup - the case SQL Server wins.
app.MapGet("/api/lookup/sql", async (
    [FromQuery] string sku, SqlSearchService sql, CancellationToken ct) =>
        TypedResults.Ok(await sql.GetBySkuAsync(sku, ct)))
    .WithName("LookupBySku")
    .WithSummary("Exact SKU lookup against SQL Server")
    .WithDescription("Included to show where a relational database is the better tool.");

// Dependency health, including whether any data has been indexed.
app.MapGet("/api/health", async (Diagnostics diagnostics, CancellationToken ct) =>
{
    var report = await diagnostics.CheckAsync(ct);
    return report.Ok ? Results.Ok(report) : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
})
    .WithName("Health")
    .WithSummary("Dependency health and seed status");

// Report what is and is not working before the first request arrives.
using (var scope = app.Services.CreateScope())
{
    var diagnostics = scope.ServiceProvider.GetRequiredService<Diagnostics>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        await diagnostics.ReportToConsoleAsync(logger);
    }
    catch (Exception ex)
    {
        // Diagnostics must never stop the app from starting.
        logger.LogWarning(ex, "Startup check could not complete");
    }
}

app.Run();

/// <summary>
/// Top-level statements compile into an internal Program class. Making it
/// public and partial is what lets the test project drive the real application
/// through WebApplicationFactory instead of testing a copy of it.
/// </summary>
public partial class Program;

/// <summary>Autocomplete request.</summary>
public sealed record SuggestRequest
{
    /// <summary>Prefix to complete, at least two characters.</summary>
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.StringLength(100, MinimumLength = 2)]
    public string? Q { get; init; }

    internal string Term => Q ?? "";
}
