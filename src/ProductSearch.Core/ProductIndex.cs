using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.IndexManagement;

namespace ProductSearch.Core;

/// <summary>
/// Creates the products index: analyzers, mappings and the read alias.
/// This is the highest-leverage code in the project - the mapping decides
/// far more about search quality and speed than any query does.
/// </summary>
public static class ProductIndex
{
    public const string Alias = "products";

    /// <summary>
    /// The concrete index behind the alias. v2 adds <c>brand.text</c>: a keyword
    /// field alone can only match when the whole query equals the stored value,
    /// so <c>brand^2</c> never fired against a query like "sony wireless headphones".
    /// </summary>
    public const string CurrentIndex = "products-v2";

    public static Task EnsureCreatedAsync(ElasticsearchClient client, CancellationToken ct = default) =>
        EnsureCreatedAsync(client, CurrentIndex, attachAlias: true, ct);

    public static async Task EnsureCreatedAsync(
        ElasticsearchClient client, string indexName, bool attachAlias, CancellationToken ct = default)
    {
        var exists = await client.Indices.ExistsAsync(indexName, ct);
        if (exists.Exists) return;

        var response = await client.Indices.CreateAsync(indexName, i => i
            // The application always queries the alias, never the index name,
            // so the index underneath can be swapped without a redeploy.
            .Aliases(a => { if (attachAlias) a.Add(Alias, _ => { }); })
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
                    // Brand needs both behaviours, so it is a multi-field. The bare
                    // keyword is what by_brand aggregates and what the brand filter
                    // matches; brand.text is analyzed, which is what lets a brand
                    // name inside a longer query contribute to the score.
                    .Keyword(k => k.Brand, k => k
                        .Fields(f => f
                            .Text("text", t => t
                                .Analyzer("product_index_analyzer")
                                .SearchAnalyzer("product_search_analyzer"))))
                    .Keyword(k => k.Category)
                    .DoubleNumber(d => d.Price)
                    .Boolean(b => b.InStock)
                    .Date(d => d.CreatedAt))),
            ct);

        if (!response.IsValidResponse)
            throw new InvalidOperationException(
                $"Failed to create index: {response.ElasticsearchServerError?.Error?.Reason}");
    }

    /// <summary>
    /// Drops whatever is behind the alias and builds the current index from
    /// scratch. This is the seeder's path: re-running it rebuilds both engines,
    /// so leaving an older index attached to the alias would mean searches hit a
    /// mixture of old and new documents.
    /// </summary>
    public static async Task RecreateAsync(ElasticsearchClient client, CancellationToken ct = default)
    {
        var existing = await client.Indices.GetAliasAsync(a => a.Name(Alias), ct);
        if (existing.IsValidResponse && existing.Aliases is { Count: > 0 })
            foreach (var index in existing.Aliases.Keys)
                await client.Indices.DeleteAsync(index, ct);

        await client.Indices.DeleteAsync(CurrentIndex, ct);   // harmless when absent
        await EnsureCreatedAsync(client, CurrentIndex, attachAlias: true, ct);
    }

    /// <summary>
    /// Zero-downtime reindex: build <paramref name="newIndex"/> with the current
    /// mapping, copy every document into it, then move the alias across in one
    /// atomic step. Searches never see a gap, and the old index stays in place
    /// for an immediate rollback.
    /// </summary>
    public static async Task ReindexAsync(
        ElasticsearchClient client, string newIndex, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(client, newIndex, attachAlias: false, ct);

        var copy = await client.ReindexAsync(r => r
            .Source(s => s.Indices(Alias))
            .Dest(d => d.Index(newIndex))
            .Refresh(true)
            .WaitForCompletion(true), ct);

        if (!copy.IsValidResponse)
            throw new InvalidOperationException(
                $"Reindex into '{newIndex}' failed: {copy.ElasticsearchServerError?.Error?.Reason}");

        // Which concrete indices the alias points at today.
        var current = await client.Indices.GetAliasAsync(a => a.Name(Alias), ct);
        var previous = current.IsValidResponse && current.Aliases is not null
            ? current.Aliases.Keys.ToArray()
            : [];

        // One request, so there is never a moment with no index behind the alias.
        //
        // Build the action list explicitly rather than chaining calls on a
        // descriptor lambda: the fluent form keeps only the last action, which
        // silently leaves the previous index attached and makes the alias point
        // at two indices at once. Searches then return each document twice over.
        // Add and Remove are properties on the action, not factory methods, so
        // these are object initialisers rather than calls.
        var actions = new List<IndexUpdateAliasesAction>();
        foreach (var old in previous.Where(p => p != newIndex))
            actions.Add(new IndexUpdateAliasesAction
            {
                Remove = new RemoveAction { Index = old, Alias = Alias }
            });
        actions.Add(new IndexUpdateAliasesAction
        {
            Add = new AddAction { Index = newIndex, Alias = Alias }
        });

        var swap = await client.Indices.UpdateAliasesAsync(u => u.Actions(actions), ct);

        if (!swap.IsValidResponse)
            throw new InvalidOperationException(
                $"Alias swap to '{newIndex}' failed: {swap.ElasticsearchServerError?.Error?.Reason}");
    }
}
