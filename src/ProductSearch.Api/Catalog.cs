namespace ProductSearch.Api;

/// <summary>
/// The product as it exists in SQL Server, which is the source of truth.
/// </summary>
public sealed class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public bool InStock { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// The denormalised read model stored in Elasticsearch. Kept separate from the
/// SQL entity on purpose: a search document has its own shape and lifecycle.
/// </summary>
public sealed class ProductDocument
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Category { get; set; } = "";
    public double Price { get; set; }
    public bool InStock { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A single result row, identical whichever engine produced it.</summary>
public sealed record ProductHit(int Id, string Sku, string Name, string Brand, decimal Price);

/// <summary>
/// A search response. <paramref name="TookMs"/> is what the engine reported;
/// <paramref name="ElapsedMs"/> is what the API actually measured end to end.
/// </summary>
public sealed record SearchResults(
    string Engine,
    string Query,
    int Count,
    long ElapsedMs,
    long? TookMs,
    IReadOnlyList<ProductHit> Items,
    IReadOnlyDictionary<string, long>? Facets = null);
