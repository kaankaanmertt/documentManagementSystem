using System.Text;
using DocumentManagementSystem.Models;
using DocumentManagementSystem.Services;
using Xunit;

namespace DocumentManagementSystem.Tests;

/// <summary>
/// DuplicateDetector için unit testler. Postgres'e gitmesin diye
/// IDocumentRepository'yi in-memory bir fake ile değiştiriyoruz.
/// </summary>
public class DuplicateDetectorTests
{
    [Fact]
    public void ComputeHash_is_deterministic_and_uppercase_hex()
    {
        var repo = new FakeRepository();
        var detector = new DuplicateDetector(repo);

        var hash1 = detector.ComputeHash(Encoding.UTF8.GetBytes("hello"));
        var hash2 = detector.ComputeHash(Encoding.UTF8.GetBytes("hello"));

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length); // SHA-256 = 32 byte = 64 hex char
        Assert.Equal(hash1, hash1.ToUpperInvariant());
    }

    [Fact]
    public void ComputeHash_differs_for_different_content()
    {
        var detector = new DuplicateDetector(new FakeRepository());

        var a = detector.ComputeHash(Encoding.UTF8.GetBytes("hello"));
        var b = detector.ComputeHash(Encoding.UTF8.GetBytes("hello!"));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task FindCandidates_returns_exact_match_when_hash_exists()
    {
        var existing = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Sözleşme - Acme 2025",
            FileName = "sozlesme-acme.pdf",
            Owner = "ayse.kara",
            ContentHash = "ABC123"
        };
        var repo = new FakeRepository(byHash: new Dictionary<string, Document> { ["ABC123"] = existing });
        var detector = new DuplicateDetector(repo);

        var candidates = await detector.FindCandidatesAsync("Yeni başlık", "yeni.pdf", "ABC123");

        Assert.Single(candidates);
        Assert.Equal("exact", candidates[0].Reason);
        Assert.Equal(1.0, candidates[0].Similarity);
    }

    [Fact]
    public async Task FindCandidates_returns_empty_when_no_similar_titles()
    {
        var repo = new FakeRepository(); // boş repo
        var detector = new DuplicateDetector(repo);

        var candidates = await detector.FindCandidatesAsync("Tamamen Yeni Bir Doküman", "yeni.pdf", "UNIQUE_HASH");

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task FindCandidates_marks_similar_titles_with_reason()
    {
        var existing = new Document
        {
            Id = Guid.NewGuid(),
            Title = "Sözleşme Acme Tedarik 2025",
            FileName = "sozlesme-acme.pdf",
            Owner = "ayse.kara",
            ContentHash = "ORIG_HASH"
        };
        var repo = new FakeRepository(
            similarByTitle: new[] { existing });
        var detector = new DuplicateDetector(repo);

        // Çok yakın başlık ama farklı hash
        var candidates = await detector.FindCandidatesAsync(
            "Sözleşme Acme Tedarik 2025 v2",
            "sozlesme-acme-v2.pdf",
            "DIFFERENT_HASH");

        Assert.NotEmpty(candidates);
        Assert.Equal("similar-name", candidates[0].Reason);
        Assert.True(candidates[0].Similarity >= 0.6, $"Similarity should be >= 0.6, got {candidates[0].Similarity}");
    }

    // --- in-memory fake repository ---

    private sealed class FakeRepository : IDocumentRepository
    {
        private readonly Dictionary<string, Document> _byHash;
        private readonly IReadOnlyList<Document> _similarByTitle;

        public FakeRepository(
            Dictionary<string, Document>? byHash = null,
            IReadOnlyList<Document>? similarByTitle = null)
        {
            _byHash = byHash ?? new();
            _similarByTitle = similarByTitle ?? Array.Empty<Document>();
        }

        public Task<Document?> GetByHashAsync(string contentHash, CancellationToken ct = default)
            => Task.FromResult(_byHash.TryGetValue(contentHash, out var d) ? d : null);

        public Task<IReadOnlyList<Document>> FindByTitleSimilarityAsync(string normalisedTitle, int limit, CancellationToken ct = default)
            => Task.FromResult(_similarByTitle);

        // Bu test kapsamında kullanılmayan üyeler:
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task<Document?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Document?>(null);
        public Task<Document> AddAsync(Document doc, CancellationToken ct = default) => Task.FromResult(doc);
        public Task<Document?> UpdateAsync(Document doc, CancellationToken ct = default) => Task.FromResult<Document?>(doc);
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => Task.FromResult(false);
        public Task<SearchResponse> SearchAsync(SearchRequest req, CancellationToken ct = default) =>
            Task.FromResult(new SearchResponse(new List<SearchHit>(), 0, 1, 20, 0, new List<string>()));
        public Task<IReadOnlyList<string>> AllOwnersAsync(int limit = 200, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public async IAsyncEnumerable<string> StreamAllHashesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<IReadOnlyList<string>> GetTopTermsAsync(int n = 5000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
