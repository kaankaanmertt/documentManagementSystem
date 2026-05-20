using System.Security.Cryptography;
using System.Text;

namespace DocumentManagementSystem.Services;

/// <summary>
/// Application'da tek tane bloom filter snapshot'ı tutar.
///   - Açılışta DB'den tüm hash'leri çekip inşa eder.
///   - Yeni upload'da incremental olarak ekler (yeni hash içeri katılır).
///   - Snapshot'ın ETag'i değişir, client cache'i invalid olur.
///
/// Note: Filtre append-only. Doküman silindiğinde filtreden çıkarmıyoruz; bu kasıtlı —
/// silme zaten bloom filter ile yapılamaz, periyodik rebuild gerektirir. Şu prototip
/// için kabul ediyoruz: silinmiş doc için false-positive olur, server-side probe
/// "yok" der ve kullanıcı yine yükler. Doğru yanıt.
/// </summary>
public class BloomFilterService
{
    private readonly IDocumentRepository _repo;
    private readonly ILogger<BloomFilterService> _log;
    private readonly double _fpRate;
    private readonly object _gate = new();

    private BloomFilter? _filter;
    private string _etag = "\"empty\"";
    private DateTimeOffset _builtAt;

    public BloomFilterService(IDocumentRepository repo, IConfiguration cfg, ILogger<BloomFilterService> log)
    {
        _repo = repo;
        _log = log;
        _fpRate = cfg.GetValue("Bloom:FalsePositiveRate", 0.01);
    }

    public BloomFilter Filter => _filter ?? throw new InvalidOperationException("Bloom filter henüz inşa edilmedi.");
    public string ETag => _etag;
    public DateTimeOffset BuiltAt => _builtAt;

    public async Task RebuildAsync(CancellationToken ct = default)
    {
        var count = Math.Max(1024, await _repo.CountAsync(ct));
        var filter = BloomFilter.Create(count, _fpRate);

        var added = 0;
        await foreach (var hash in _repo.StreamAllHashesAsync(ct))
        {
            filter.Add(hash);
            added++;
        }

        lock (_gate)
        {
            _filter = filter;
            _builtAt = DateTimeOffset.UtcNow;
            _etag = ComputeETag(_filter);
        }

        _log.LogInformation("Bloom filter inşa edildi: {Count} hash, m={M}, k={K}, ~{KB} KB",
            added, filter.M, filter.K, filter.ToByteArray().Length / 1024);
    }

    public void AddHash(string hash)
    {
        lock (_gate)
        {
            _filter?.Add(hash);
            // ETag'i hash'i de katarak güncelle — incremental fingerprint
            _etag = QuickETag(_etag, hash);
        }
    }

    private static string ComputeETag(BloomFilter f)
    {
        Span<byte> hashInput = stackalloc byte[8 + 32];
        BitConverter.TryWriteBytes(hashInput[..4], f.M);
        BitConverter.TryWriteBytes(hashInput[4..8], f.K);

        // SHA1 yeterli — sadece versiyonlama için, security amaçlı değil.
        Span<byte> sha = stackalloc byte[20];
        SHA1.HashData(f.ToByteArray(), sha);
        sha[..12].CopyTo(hashInput[8..20]);
        return "\"" + Convert.ToHexString(hashInput[..20]).ToLowerInvariant() + "\"";
    }

    private static string QuickETag(string previous, string addedHash)
    {
        // Hızlı incremental ETag: önceki etag + yeni hash'in fnv1a türevi
        var h = BloomFilter.Fnv1a(previous + addedHash, 0);
        return "\"" + h.ToString("x8") + DateTime.UtcNow.Ticks.ToString("x") + "\"";
    }
}
