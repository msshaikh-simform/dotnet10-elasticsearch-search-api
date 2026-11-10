using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ProductSearch.Tests;

/// <summary>
/// These tests exist to protect specific claims the article makes. Each one
/// corresponds to a documented promise, and each would have caught a real bug
/// found during review:
///
///   - validation returns 400          (the article shows this exact response)
///   - the last allowed page returns   (it used to return a raw 500)
///   - the brand filter ignores case   (?brand=sony used to find nothing)
///
/// They need Elasticsearch running and the catalog seeded, which is why each
/// one skips rather than fails when the stack is not up - a test that fails on
/// a laptop with no Docker teaches the reader nothing.
/// </summary>
public sealed class SearchEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client => factory.CreateClient();

    [Fact]
    public async Task Size_above_the_maximum_is_rejected_with_400()
    {
        var response = await Client.GetAsync("/api/search?q=wireless&size=999");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("size must be between 1 and 100", body);
    }

    [Fact]
    public async Task Missing_search_term_is_rejected_with_400()
    {
        var response = await Client.GetAsync("/api/search?q=");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Provide a search term", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Page_beyond_the_result_window_is_rejected_with_400_not_500()
    {
        // from = 10,000 is past the window, so this is the explained failure.
        var response = await Client.GetAsync("/api/search?q=wireless&page=501&size=20");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("search_after", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_last_allowed_page_succeeds()
    {
        // from + size is exactly the 10,000 window. The look-ahead hit used to
        // push the request one past it, and Elasticsearch rejected the whole
        // thing - the API returned 500 where it had promised 400.
        var response = await Client.GetAsync("/api/search?q=wireless&page=500&size=20");

        if (await SkipWhenNotSeeded(response)) return;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("sony")]
    [InlineData("Sony")]
    [InlineData("SONY")]
    public async Task Brand_filter_ignores_case(string brand)
    {
        var response = await Client.GetAsync($"/api/search?q=wireless&brand={brand}");

        if (await SkipWhenNotSeeded(response)) return;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"count\":0", body);
    }

    /// <summary>
    /// A 503 means Elasticsearch is not running or nothing has been seeded.
    /// That is an environment problem, not a failing assertion.
    /// </summary>
    private static async Task<bool> SkipWhenNotSeeded(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.ServiceUnavailable) return false;

        Assert.Skip("Elasticsearch is not running or the catalog has not been seeded. "
                  + "Run: docker compose up -d && dotnet run --project src/ProductSearch.Seeder -c Release -- 200000");
        await Task.CompletedTask;
        return true;
    }
}
