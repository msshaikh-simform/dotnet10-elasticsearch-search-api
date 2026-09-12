using Elastic.Clients.Elasticsearch;

namespace ProductSearch.Api;

/// <summary>
/// Creates the products index: analyzers, mappings and the read alias.
/// This is the highest-leverage code in the project - the mapping decides
/// far more about search quality and speed than any query does.
/// </summary>
public static class ProductIndex
{
    public const string Alias = "products";
    public const string CurrentIndex = "products-v1";

    public static async Task EnsureCreatedAsync(ElasticsearchClient client, CancellationToken ct = default)
    {
        var exists = await client.Indices.ExistsAsync(CurrentIndex, ct);
        if (exists.Exists) return;

        var response = await client.Indices.CreateAsync(CurrentIndex, i => i
            // The application always queries the alias, never the index name,
            // so the index underneath can be swapped without a redeploy.
            .Aliases(a => a.Add(Alias, _ => { }))
            .Settings(s => s
                .NumberOfShards(1)      // local development only
                .NumberOfReplicas(0)    // no replicas on a single node
                .Analysis(a => a
                    .TokenFilters(tf => tf
                        .Stemmer("english_stemmer", st => st.Language("english"))
                        .Synonym("product_synonyms", sy => sy
                            .Synonyms(["tv, television", "laptop, notebook"])))
                    .Analyzers(an => an
                        // Index and search analyzers must agree on stemming,
                        // otherwise the terms never meet and nothing matches.
                        .Custom("product_index_analyzer", c => c
                            .Tokenizer("standard")
                            .Filter(["lowercase", "asciifolding", "english_stemmer"]))
                        // Synonyms are applied only at search time, so changing
                        // the list does not require reindexing the documents.
                        .Custom("product_search_analyzer", c => c
                            .Tokenizer("standard")
                            .Filter(["lowercase", "asciifolding", "product_synonyms", "english_stemmer"])))
                    .Normalizers(n => n
                        .Custom("lowercase_normalizer", c => c
                            .Filter(["lowercase", "asciifolding"])))))
            .Mappings(m => m
                .Properties<ProductDocument>(p => p
                    .Text(t => t.Name, t => t
                        .Analyzer("product_index_analyzer")
                        .SearchAnalyzer("product_search_analyzer")
                        .Fields(f => f
                            .Keyword("keyword", k => k.Normalizer("lowercase_normalizer"))
                            .SearchAsYouType("suggest")))
                    .Text(t => t.Description, t => t
                        .Analyzer("product_index_analyzer")
                        .SearchAnalyzer("product_search_analyzer"))
                    // Sku is an identifier: never analyzed, never fuzzy-matched.
                    .Keyword(k => k.Sku)
                    .Keyword(k => k.Brand)
                    .Keyword(k => k.Category)
                    .DoubleNumber(d => d.Price)
                    .Boolean(b => b.InStock)
                    .Date(d => d.CreatedAt))),
            ct);

        if (!response.IsValidResponse)
            throw new InvalidOperationException(
                $"Failed to create index: {response.ElasticsearchServerError?.Error?.Reason}");
    }
}
