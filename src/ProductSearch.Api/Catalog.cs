using System.ComponentModel.DataAnnotations;

namespace ProductSearch.Api;

/// <summary>The product as it exists in SQL Server, which is the source of truth.</summary>
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

/// <summary>
/// Search request. The validation attributes are enforced automatically by
/// ASP.NET Core 10's minimal API validation - an invalid request never reaches
/// the handler and comes back as a 400 with the offending fields listed.
/// </summary>
public sealed record ProductSearchRequest
{
    // Every optional value is nullable on purpose. With [AsParameters], a
    // non-nullable value type becomes a *required* query parameter, so `bool Fuzzy`
    // would force callers to pass ?fuzzy= on every request. Defaults are applied
    // through the Effective* properties below instead.

    /// <summary>Text to search for. Required.</summary>
    [Required(AllowEmptyStrings = false, ErrorMessage = "Provide a search term, for example ?q=wireless headphones")]
    [StringLength(100, MinimumLength = 1)]
    public string? Q { get; init; }

    /// <summary>Restrict to a single brand, for example "Sony".</summary>
    [StringLength(80)]
    public string? Brand { get; init; }

    /// <summary>Only products at or below this price.</summary>
    [Range(0, 1_000_000, ErrorMessage = "maxPrice must be between 0 and 1,000,000")]
    public decimal? MaxPrice { get; init; }

    /// <summary>Allow small spelling mistakes. Elasticsearch only; costs more, so it is opt-in.</summary>
    public bool? Fuzzy { get; init; }

    /// <summary>Results per page (1-100). Defaults to 20.</summary>
    [Range(1, 100, ErrorMessage = "size must be between 1 and 100")]
    public int? Size { get; init; }

    /// <summary>Page number, starting at 1.</summary>
    [Range(1, 10_000, ErrorMessage = "page must be 1 or greater")]
    public int? Page { get; init; }

    internal string Term => Q ?? "";
    internal bool UseFuzzy => Fuzzy ?? false;
    internal int PageSize => Size ?? 20;
    internal int PageNumber => Page ?? 1;
}

/// <summary>A single result row, identical whichever engine produced it.</summary>
public sealed record ProductHit(int Id, string Sku, string Name, string Brand, decimal Price);

/// <summary>
/// A search response, including both timings so they can be compared:
/// <c>ElapsedMs</c> is measured by the API and includes network and
/// deserialization, while <c>TookMs</c> is what Elasticsearch itself reported
/// (null for SQL Server). <c>HasMore</c> indicates whether another page exists.
/// </summary>
public sealed record SearchResults(
    string Engine,
    string Query,
    int Count,
    int Page,
    int Size,
    bool HasMore,
    long ElapsedMs,
    long? TookMs,
    IReadOnlyList<ProductHit> Items,
    IReadOnlyDictionary<string, long>? Facets = null);

/// <summary>Status of one dependency, with a hint when it is not healthy.</summary>
public sealed record DependencyStatus(string Name, bool Ok, string Detail, string? Hint = null);

/// <summary>
/// Health and readiness. <c>Seeded</c> is the one people miss: the stack can be
/// perfectly healthy and still return nothing because no data was ever indexed.
/// </summary>
public sealed record HealthReport(
    bool Ok,
    bool Seeded,
    long IndexedProducts,
    IReadOnlyList<DependencyStatus> Dependencies);
