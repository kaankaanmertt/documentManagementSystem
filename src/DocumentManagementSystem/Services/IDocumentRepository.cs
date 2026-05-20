using DocumentManagementSystem.Models;

namespace DocumentManagementSystem.Services;

public interface IDocumentRepository
{
    Task<int> CountAsync(CancellationToken ct = default);
    Task<Document?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Document?> GetByHashAsync(string contentHash, CancellationToken ct = default);
    Task<IReadOnlyList<Document>> FindByTitleSimilarityAsync(string normalisedTitle, int limit, CancellationToken ct = default);
    Task<Document> AddAsync(Document doc, CancellationToken ct = default);
    Task<Document?> UpdateAsync(Document doc, CancellationToken ct = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken ct = default);

    Task<SearchResponse> SearchAsync(SearchRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<string>> AllOwnersAsync(int limit = 200, CancellationToken ct = default);

    /// <summary>Tüm content_hash'leri streaming olarak döner. Bloom filter inşası için.</summary>
    IAsyncEnumerable<string> StreamAllHashesAsync(CancellationToken ct = default);

    /// <summary>Suggest için en yaygın N kelimeyi döner.</summary>
    Task<IReadOnlyList<string>> GetTopTermsAsync(int n = 5000, CancellationToken ct = default);
}
