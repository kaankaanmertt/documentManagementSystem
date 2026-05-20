using System.Security.Cryptography;
using DocumentManagementSystem.Models;

namespace DocumentManagementSystem.Services;

/// <summary>
/// Server-side duplicate detection — iki katmanlı:
///   1. content_hash üzerinden tek satır lookup (B-tree index, sub-millisecond).
///   2. pg_trgm benzerlik fonksiyonu ile yakın başlık taraması (GIN trigram index).
/// Client tarafında bloom filter zaten ön filtrelemeyi yaptı; bu method
/// "kesin teyit" + "benzer isim" için çağrılır.
/// </summary>
public class DuplicateDetector : IDuplicateDetector
{
    private readonly IDocumentRepository _repo;

    public DuplicateDetector(IDocumentRepository repo) => _repo = repo;

    public string ComputeHash(byte[] content)
    {
        Span<byte> sha = stackalloc byte[32];
        SHA256.HashData(content, sha);
        return Convert.ToHexString(sha);
    }

    public async Task<List<DuplicateCandidate>> FindCandidatesAsync(string title, string fileName, string contentHash, CancellationToken ct = default)
    {
        var result = new List<DuplicateCandidate>();

        // 1. Exact content hash match (en güçlü sinyal)
        var exact = await _repo.GetByHashAsync(contentHash, ct);
        if (exact is not null)
        {
            result.Add(new DuplicateCandidate(
                exact.Id, exact.Title, exact.FileName, exact.Owner, exact.CreatedAt,
                "exact", 1.0));
            return result;
        }

        // 2. Yakın başlık taraması (Postgres pg_trgm)
        if (!string.IsNullOrWhiteSpace(title))
        {
            var similar = await _repo.FindByTitleSimilarityAsync(title, 5, ct);
            foreach (var d in similar)
            {
                // Trigram similarity'yi tekrar hesaplayıp normalize edelim (basit, gösterim için)
                var sim = JaccardOnTrigrams(title, d.Title);
                if (sim >= 0.6)
                {
                    result.Add(new DuplicateCandidate(
                        d.Id, d.Title, d.FileName, d.Owner, d.CreatedAt,
                        "similar-name", Math.Round(sim, 2)));
                }
            }
        }

        return result.OrderByDescending(c => c.Similarity).Take(5).ToList();
    }

    /// <summary>Trigram bazlı Jaccard benzerliği — pg_trgm'in CPU üzerindeki eşi.</summary>
    private static double JaccardOnTrigrams(string a, string b)
    {
        var ta = Trigrams(a);
        var tb = Trigrams(b);
        if (ta.Count == 0 || tb.Count == 0) return 0;
        var intersection = ta.Intersect(tb).Count();
        var union = ta.Union(tb).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> Trigrams(string input)
    {
        var s = "  " + string.Concat(Tokenizer.Tokenize(input)) + "  ";
        var set = new HashSet<string>();
        for (int i = 0; i + 3 <= s.Length; i++) set.Add(s.Substring(i, 3));
        return set;
    }
}
