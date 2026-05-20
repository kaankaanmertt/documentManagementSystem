using DocumentManagementSystem.Services;
using Microsoft.AspNetCore.Mvc;

namespace DocumentManagementSystem.Controllers;

/// <summary>
/// Client tarafına en yaygın kelime sözlüğünü gönderir.
/// Suggest'ı client-side yapmak için kullanılır — kullanıcı yazarken
/// network round-trip olmadan "Bunu mu demek istediniz?" üretir.
/// </summary>
[ApiController]
[Route("api/term-dictionary")]
public class TermDictionaryController : ControllerBase
{
    private readonly TermDictionaryService _service;

    public TermDictionaryController(TermDictionaryService service) => _service = service;

    [HttpGet]
    public IActionResult Get()
    {
        var etag = _service.ETag;
        if (Request.Headers.TryGetValue("If-None-Match", out var inm) && inm.ToString() == etag)
            return StatusCode(304);

        Response.Headers["ETag"] = etag;
        Response.Headers["Cache-Control"] = "public, max-age=600";
        return Ok(_service.Terms);
    }
}
