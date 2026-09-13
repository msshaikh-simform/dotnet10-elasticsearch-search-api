using System.Data;
using System.Diagnostics;
using Bogus;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Data.SqlClient;

// Seeds SQL Server (the source of truth) and then bulk-indexes into Elasticsearch.
// Both engines end up with byte-identical data, which is what makes the comparison fair.

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

var count = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 200_000;

var masterConn  = ProductSearch.Core.Env.SqlMasterConnectionString;
var catalogConn = ProductSearch.Core.Env.SqlConnectionString;
var esUrl       = ProductSearch.Core.Env.ElasticsearchUrl;

var elastic = new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(esUrl))
    .Authentication(new BasicAuthentication(
        ProductSearch.Core.Env.ElasticsearchUsername, ProductSearch.Core.Env.ElasticsearchPassword))
    .RequestTimeout(TimeSpan.FromMinutes(5)));

// Check both engines before generating anything. Without this, a stopped
// Elasticsearch container is only discovered after a minute of work, and the
// failure arrives as a raw stack trace.
if (!await PreflightAsync(masterConn, elastic, esUrl))
    return 1;

Console.WriteLine($"Seeding {count:N0} products...");
var total = Stopwatch.StartNew();

// ---- 1. Schema --------------------------------------------------------------
await using (var master = new SqlConnection(masterConn))
{
    await master.OpenAsync();
    await new SqlCommand(
        "IF DB_ID('ProductCatalog') IS NULL CREATE DATABASE ProductCatalog;", master).ExecuteNonQueryAsync();
}

await using (var db = new SqlConnection(catalogConn))
{
    await db.OpenAsync();
    await new SqlCommand("""
        IF OBJECT_ID('dbo.Products') IS NOT NULL DROP TABLE dbo.Products;
        CREATE TABLE dbo.Products (
            Id          INT IDENTITY(1,1) PRIMARY KEY,
            Sku         NVARCHAR(32)   NOT NULL,
            Name        NVARCHAR(200)  NOT NULL,
            Description NVARCHAR(MAX)  NOT NULL,
            Brand       NVARCHAR(80)   NOT NULL,
            Category    NVARCHAR(80)   NOT NULL,
            Price       DECIMAL(10,2)  NOT NULL,
            InStock     BIT            NOT NULL,
            CreatedAt   DATETIME2      NOT NULL
        );
        """, db).ExecuteNonQueryAsync();

    // A realistic catalog indexes its identifier. This is the case SQL wins.
    await new SqlCommand(
        "CREATE UNIQUE INDEX IX_Products_Sku ON dbo.Products(Sku);", db).ExecuteNonQueryAsync();
}

// ---- 2. Generate ------------------------------------------------------------
string[] brands = ["Sony", "Bose", "Samsung", "Logitech", "Anker", "JBL", "Philips", "Dell", "Lenovo", "Xiaomi"];
string[] categories = ["Headphones", "Speakers", "Laptops", "Monitors", "Keyboards", "Cameras", "Televisions", "Wearables"];
string[] qualifiers = ["Wireless", "Bluetooth", "Noise-Cancelling", "Gaming", "Portable", "Studio", "Ultra", "Compact"];

var id = 0;
var faker = new Faker<ProductRow>()
    .UseSeed(20260912) // deterministic: every run produces the same catalog
    .RuleFor(p => p.Sku, f => $"SKU-{++id:D7}")
    .RuleFor(p => p.Brand, f => f.PickRandom(brands))
    .RuleFor(p => p.Category, f => f.PickRandom(categories))
    .RuleFor(p => p.Name, (f, p) =>
    {
        // Two *distinct* qualifiers, otherwise names like "Wireless Wireless" appear.
        var picked = f.Random.Shuffle(qualifiers).Take(2).ToArray();
        return $"{p.Brand} {picked[0]} {picked[1]} {p.Category[..^1]} {f.Random.Int(100, 999)}";
    })
    // Descriptions are long on purpose: a real catalog has them, and they are
    // exactly what makes LIKE '%term%' expensive.
    .RuleFor(p => p.Description, f => string.Join(' ', f.Lorem.Sentences(6)))
    .RuleFor(p => p.Price, f => Math.Round(f.Random.Decimal(9, 2499), 2))
    .RuleFor(p => p.InStock, f => f.Random.Bool(0.8f))
    .RuleFor(p => p.CreatedAt, f => f.Date.Past(3));

var products = faker.Generate(count);
Console.WriteLine($"  generated in {total.ElapsedMilliseconds:N0} ms");

// ---- 3. Bulk load into SQL Server -------------------------------------------
var sw = Stopwatch.StartNew();
var table = new DataTable();
table.Columns.Add("Sku", typeof(string));
table.Columns.Add("Name", typeof(string));
table.Columns.Add("Description", typeof(string));
table.Columns.Add("Brand", typeof(string));
table.Columns.Add("Category", typeof(string));
table.Columns.Add("Price", typeof(decimal));
table.Columns.Add("InStock", typeof(bool));
table.Columns.Add("CreatedAt", typeof(DateTime));

foreach (var p in products)
    table.Rows.Add(p.Sku, p.Name, p.Description, p.Brand, p.Category, p.Price, p.InStock, p.CreatedAt);

await using (var db = new SqlConnection(catalogConn))
{
    await db.OpenAsync();
    using var bulk = new SqlBulkCopy(db) { DestinationTableName = "dbo.Products", BatchSize = 10_000, BulkCopyTimeout = 300 };
    foreach (DataColumn c in table.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
    await bulk.WriteToServerAsync(table);
}
Console.WriteLine($"  SQL Server loaded in {sw.ElapsedMilliseconds:N0} ms");

// ---- 4. Bulk index into Elasticsearch ---------------------------------------
var client = elastic;

await ProductSearch.Core.ProductIndex.EnsureCreatedAsync(client);

sw.Restart();
var indexed = 0;
var failures = 0;

// Batch by document count; a real pipeline would batch by payload size.
foreach (var batch in products.Select((p, i) => (p, i)).GroupBy(x => x.i / 5_000).Select(g => g.Select(x => x.p).ToList()))
{
    var docs = batch.Select((p, i) => new ProductSearch.Core.ProductDocument
    {
        Id = indexed + i + 1,
        Sku = p.Sku,
        Name = p.Name,
        Description = p.Description,
        Brand = p.Brand,
        Category = p.Category,
        Price = (double)p.Price,
        InStock = p.InStock,
        CreatedAt = p.CreatedAt
    }).ToList();

    var response = await client.BulkAsync(b => b
        .Index(ProductSearch.Core.ProductIndex.CurrentIndex)
        .IndexMany(docs, (d, doc) => d.Id(doc.Id.ToString())));

    // A bulk request can report overall success while individual items failed,
    // so the per-item results are what actually matter.
    if (response.Errors)
        foreach (var item in response.ItemsWithErrors)
        {
            failures++;
            if (failures <= 3) Console.WriteLine($"  ! {item.Id}: {item.Error?.Reason}");
        }

    indexed += docs.Count;
    Console.Write($"\r  indexed {indexed:N0}/{count:N0}");
}

await client.Indices.RefreshAsync(ProductSearch.Core.ProductIndex.CurrentIndex);
Console.WriteLine($"\r  Elasticsearch indexed in {sw.ElapsedMilliseconds:N0} ms ({failures} failures)   ");
Console.WriteLine($"Done in {total.Elapsed.TotalSeconds:N1}s");

if (failures > 0)
{
    Console.Error.WriteLine($"\n  {failures:N0} documents failed to index. Search results will be incomplete.");
    return 2;
}

Console.WriteLine("\n  Next: dotnet run --project src/ProductSearch.Api -c Release");
return 0;

// ---- Preflight ---------------------------------------------------------------

static async Task<bool> PreflightAsync(string sqlConnection, ElasticsearchClient elastic, string esUrl)
{
    var problems = new List<string>();

    try
    {
        await using var conn = new SqlConnection(sqlConnection);
        await conn.OpenAsync();
    }
    catch (SqlException ex)
    {
        problems.Add(ex.Number switch
        {
            18456 => "SQL Server rejected the login. If the container only just started, wait a few seconds "
                   + "and try again - it accepts connections before authentication is fully ready.",
            _ => $"Cannot reach SQL Server: {ex.Message.Split('\n')[0]}\n"
               + "    Start it with: docker compose up -d\n"
               + "    SQL Server takes longer to start than Elasticsearch, so give it a moment."
        });
    }
    catch (Exception ex)
    {
        problems.Add($"Cannot reach SQL Server: {ex.Message.Split('\n')[0]}\n    Try: docker compose up -d");
    }

    try
    {
        var ping = await elastic.PingAsync();
        if (!ping.IsValidResponse)
            problems.Add($"Elasticsearch at {esUrl} did not respond to a ping.\n"
                       + "    It needs roughly 60 seconds to start. Try: docker compose up -d");
    }
    catch (Exception ex)
    {
        problems.Add($"Cannot reach Elasticsearch at {esUrl}: {ex.Message.Split('\n')[0]}\n"
                   + "    Start it with: docker compose up -d\n"
                   + "    Check readiness with: curl.exe -u elastic:changeme http://localhost:9200/_cluster/health");
    }

    if (problems.Count == 0) return true;

    Console.Error.WriteLine("\n  Cannot seed - the required services are not available:\n");
    foreach (var p in problems) Console.Error.WriteLine($"  - {p}\n");
    return false;
}

internal sealed class ProductRow
{
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public bool InStock { get; set; }
    public DateTime CreatedAt { get; set; }
}
