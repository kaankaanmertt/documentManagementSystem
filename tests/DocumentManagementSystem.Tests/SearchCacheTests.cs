using DocumentManagementSystem.Models;
using DocumentManagementSystem.Services;
using Xunit;

namespace DocumentManagementSystem.Tests;

public class SearchCacheTests
{
    private static SearchResponse FakeResponse(int total = 1) =>
        new(new List<SearchHit>(), total, 1, 20, 5, new List<string>());

    [Fact]
    public void Get_returns_false_for_unknown_key()
    {
        var cache = new SearchCache();
        var req = new SearchRequest { Query = "acme" };

        Assert.False(cache.TryGet(req, out _));
    }

    [Fact]
    public void Set_then_get_returns_same_response()
    {
        var cache = new SearchCache();
        var req = new SearchRequest { Query = "acme", Page = 1, PageSize = 10 };
        var response = FakeResponse(total: 42);

        cache.Set(req, response);

        Assert.True(cache.TryGet(req, out var got));
        Assert.Equal(42, got.Total);
    }

    [Fact]
    public void Different_query_is_a_different_cache_entry()
    {
        var cache = new SearchCache();
        var req1 = new SearchRequest { Query = "acme" };
        var req2 = new SearchRequest { Query = "beta" };

        cache.Set(req1, FakeResponse(total: 1));

        Assert.True(cache.TryGet(req1, out _));
        Assert.False(cache.TryGet(req2, out _));
    }

    [Fact]
    public void Different_filters_produce_different_cache_keys()
    {
        var cache = new SearchCache();
        var noFilter   = new SearchRequest { Query = "acme" };
        var withFilter = new SearchRequest { Query = "acme", Type = DocumentType.Contract };

        cache.Set(noFilter, FakeResponse(total: 100));
        cache.Set(withFilter, FakeResponse(total: 30));

        Assert.True(cache.TryGet(noFilter,   out var a));
        Assert.True(cache.TryGet(withFilter, out var b));
        Assert.Equal(100, a.Total);
        Assert.Equal(30,  b.Total);
    }

    [Fact]
    public void Invalidate_all_clears_existing_entries()
    {
        var cache = new SearchCache();
        var req = new SearchRequest { Query = "acme" };
        cache.Set(req, FakeResponse(total: 5));

        cache.InvalidateAll();

        Assert.False(cache.TryGet(req, out _));
    }

    [Fact]
    public void Same_query_with_different_page_is_separate_entry()
    {
        var cache = new SearchCache();
        var page1 = new SearchRequest { Query = "fatura", Page = 1, PageSize = 20 };
        var page2 = new SearchRequest { Query = "fatura", Page = 2, PageSize = 20 };

        cache.Set(page1, FakeResponse(total: 10));
        cache.Set(page2, FakeResponse(total: 20));

        Assert.True(cache.TryGet(page1, out var p1));
        Assert.True(cache.TryGet(page2, out var p2));
        Assert.Equal(10, p1.Total);
        Assert.Equal(20, p2.Total);
    }
}
