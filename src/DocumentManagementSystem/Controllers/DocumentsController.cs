using System.Text;
using DocumentManagementSystem.Models;
using DocumentManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocumentManagementSystem.Controllers;

[ApiController]
[Route("api/documents")]
public class DocumentsController : ControllerBase
{
    private readonly IDocumentRepository _repo;
    private readonly IDuplicateDetector _detector;
    private readonly BloomFilterService _bloom;
    private readonly SearchCache _searchCache;

    public DocumentsController(IDocumentRepository repo, IDuplicateDetector detector, BloomFilterService bloom, SearchCache searchCache)
    {
        _repo = repo;
        _detector = detector;
        _bloom = bloom;
        _searchCache = searchCache;
    }

    [HttpGet]
    public async Task<ActionResult<SearchResponse>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var req = new SearchRequest { Page = page, PageSize = Math.Clamp(pageSize, 1, 100) };
        return await _repo.SearchAsync(req, ct);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<Document>> GetById(Guid id, CancellationToken ct)
    {
        var doc = await _repo.GetByIdAsync(id, ct);
        return doc is null ? NotFound() : doc;
    }

    [HttpGet("owners")]
    public async Task<ActionResult<IReadOnlyCollection<string>>> Owners(CancellationToken ct)
        => Ok(await _repo.AllOwnersAsync(200, ct));

    /// <summary>
    /// Client'ın bloom filter "var" dediği durumda exact teyit için kullandığı endpoint.
    /// Tek satır, indeks lookup; sub-millisecond.
    /// </summary>
    [HttpGet("by-hash/{hash}")]
    public async Task<ActionResult<Document>> GetByHash(string hash, CancellationToken ct)
    {
        var doc = await _repo.GetByHashAsync(hash, ct);
        return doc is null ? NotFound() : doc;
    }

    [HttpPost]
    public async Task<ActionResult<UploadResponse>> Upload([FromBody] UploadRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.FileName))
            return BadRequest(new UploadResponse(false, null, new(), "Başlık ve dosya adı zorunlu."));

        var bytes = Encoding.UTF8.GetBytes(req.Content ?? string.Empty);
        var hash = _detector.ComputeHash(bytes);
        var candidates = await _detector.FindCandidatesAsync(req.Title, req.FileName, hash, ct);

        var exact = candidates.FirstOrDefault(c => c.Reason == "exact");
        if (exact is not null)
        {
            return Conflict(new UploadResponse(
                false, null, candidates,
                "Bu dosya birebir aynı içerikle zaten yüklenmiş. Tekrar yüklemenize gerek yok."));
        }

        if (candidates.Count > 0 && !req.ForceUpload)
        {
            return Conflict(new UploadResponse(
                false, null, candidates,
                "Benzer isimli dokümanlar bulundu. Aşağıdaki kayıtlardan biri aradığınız olabilir."));
        }

        var doc = new Document
        {
            Title = req.Title,
            FileName = req.FileName,
            Type = req.Type,
            Owner = string.IsNullOrWhiteSpace(req.Owner) ? "unknown" : req.Owner,
            SizeBytes = bytes.LongLength,
            ContentHash = hash,
            Tags = req.Tags ?? new(),
            TextPreview = req.Content ?? string.Empty
        };

        await _repo.AddAsync(doc, ct);
        _bloom.AddHash(hash); // canlı bloom filter'a ekle (sonraki upload'larda anında yakalanır)
        _searchCache.InvalidateAll(); // veri değişti, cache eskidi

        return CreatedAtAction(nameof(GetById), new { id = doc.Id },
            new UploadResponse(true, doc, candidates, "Doküman yüklendi."));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<Document>> Update(Guid id, [FromBody] UploadRequest req, CancellationToken ct)
    {
        var existing = await _repo.GetByIdAsync(id, ct);
        if (existing is null) return NotFound();

        if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.FileName))
            return BadRequest(new { message = "Başlık ve dosya adı zorunlu." });

        var bytes = Encoding.UTF8.GetBytes(req.Content ?? string.Empty);
        var newHash = _detector.ComputeHash(bytes);

        existing.Title = req.Title;
        existing.FileName = req.FileName;
        existing.Type = req.Type;
        existing.Owner = string.IsNullOrWhiteSpace(req.Owner) ? existing.Owner : req.Owner;
        existing.SizeBytes = bytes.LongLength;
        existing.ContentHash = newHash;
        existing.Tags = req.Tags ?? new();
        existing.TextPreview = req.Content ?? string.Empty;

        var saved = await _repo.UpdateAsync(existing, ct);
        if (saved is null) return NotFound();

        _bloom.AddHash(newHash); // hash değiştiyse yeni hash de filtreye girer
        _searchCache.InvalidateAll();
        return Ok(saved);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (!await _repo.DeleteAsync(id, ct)) return NotFound();
        _searchCache.InvalidateAll();
        return NoContent();
    }
}
