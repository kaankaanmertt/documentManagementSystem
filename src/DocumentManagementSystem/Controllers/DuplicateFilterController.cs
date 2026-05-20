using DocumentManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocumentManagementSystem.Controllers;

/// <summary>
/// Bloom filter snapshot'ını client'a binary olarak verir.
///
/// Wire format (little-endian):
///   bytes 0..3   = m (filtrenin bit uzunluğu)
///   bytes 4..7   = k (hash fonksiyonu sayısı)
///   bytes 8..    = bit array
///
/// JS tarafı wwwroot/lib/bloom-filter.js içinde aynı format ile parse ediyor.
/// ETag + Cache-Control ile client cache'leyebilir, yeni snapshot değişince yeniler.
/// </summary>
[ApiController]
[Route("api/duplicate-filter")]
public class DuplicateFilterController : ControllerBase
{
    private readonly BloomFilterService _bloom;
    public DuplicateFilterController(BloomFilterService bloom) => _bloom = bloom;

    [HttpGet]
    public IActionResult Get()
    {
        var etag = _bloom.ETag;
        if (Request.Headers.TryGetValue("If-None-Match", out var inm) && inm.ToString() == etag)
            return StatusCode(304);

        Response.Headers["ETag"] = etag;
        Response.Headers["Cache-Control"] = "public, max-age=300";
        Response.Headers["X-Bloom-BuiltAt"] = _bloom.BuiltAt.ToString("O");

        var bytes = _bloom.Filter.Serialize();
        return File(bytes, "application/octet-stream");
    }
}
