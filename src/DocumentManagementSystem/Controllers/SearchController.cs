using DocumentManagementSystem.Models;
using DocumentManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocumentManagementSystem.Controllers;

[ApiController]
[Route("api/search")]
public class SearchController : ControllerBase
{
    private readonly IDocumentRepository _repo;
    private readonly SearchCache _cache;

    public SearchController(IDocumentRepository repo, SearchCache cache)
    {
        _repo = repo;
        _cache = cache;
    }

    [HttpPost]
    public async Task<ActionResult<SearchResponse>> Search([FromBody] SearchRequest req, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        req.Page = req.Page < 1 ? 1 : req.Page;
        req.PageSize = Math.Clamp(req.PageSize <= 0 ? 20 : req.PageSize, 1, 100);

        // Cache hit: DB'ye hiç gitmeyiz (debounce'lu UI'da aynı sorgu birkaç saniyede tekrarlanır).
        if (_cache.TryGet(req, out var cached))
        {
            sw.Stop();
            // Gerçek cache lookup süresini döndür; 0ms yanıltıcı olur. Tipik: <2ms.
            return new SearchResponse(cached.Hits, cached.Total, cached.Page, cached.PageSize,
                sw.ElapsedMilliseconds, cached.Suggestions);
        }

        var response = await _repo.SearchAsync(req, ct);
        _cache.Set(req, response);
        return response;
    }
}
