using DocumentManagementSystem.Models;

namespace DocumentManagementSystem.Services;

public interface IDuplicateDetector
{
    string ComputeHash(byte[] content);
    Task<List<DuplicateCandidate>> FindCandidatesAsync(string title, string fileName, string contentHash, CancellationToken ct = default);
}
