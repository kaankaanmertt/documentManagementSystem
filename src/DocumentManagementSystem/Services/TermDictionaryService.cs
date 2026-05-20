namespace DocumentManagementSystem.Services;

/// <summary>
/// Client tarafına gönderilen "popular terms" listesi. Suggest endpoint'i
/// için kullanılır — client kullanıcı yazarken Levenshtein ile "did you mean"
/// önerisi üretir, server'a gitmeden.
///
/// Startup'ta bir kez hesaplanır, ETag ile cacheable.
/// </summary>
public class TermDictionaryService
{
    private readonly IDocumentRepository _repo;
    private readonly ILogger<TermDictionaryService> _log;
    private List<string> _terms = new();
    private string _etag = "\"empty\"";

    public TermDictionaryService(IDocumentRepository repo, ILogger<TermDictionaryService> log)
    {
        _repo = repo;
        _log = log;
    }

    public IReadOnlyList<string> Terms => _terms;
    public string ETag => _etag;

    public async Task RebuildAsync(int n = 5000, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var terms = await _repo.GetTopTermsAsync(n, ct);
        _terms = terms.ToList();
        _etag = "\"" + BloomFilter.Fnv1a(string.Concat(_terms.Take(100)), 0).ToString("x8") + "\"";
        _log.LogInformation("Term sözlüğü inşa edildi: {Count} kelime, {Ms} ms", _terms.Count, sw.ElapsedMilliseconds);
    }
}
