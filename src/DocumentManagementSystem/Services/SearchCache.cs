using System.Collections.Concurrent;
using DocumentManagementSystem.Models;

namespace DocumentManagementSystem.Services;

/// <summary>
/// In-memory LRU-ish cache for search results.
///
/// Mantik:
///   - Anahtar: search request'in normalize edilmiş hash'i
///   - TTL: 30 saniye (kullanıcı yazarken aynı sorgu birkaç saniye içinde tekrarlanır)
///   - Kapasite: 256 entry (LRU eviction)
/// 8 bin DAU senaryosunda popüler sorgular bu cache'te kalır, DB'ye gitmez.
/// Veri değiştiğinde (upload/update/delete) tüm cache invalidate edilir — basit ama doğru.
/// </summary>
public class SearchCache
{
    private const int Capacity = 256;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, CacheEntry> _store = new();
    private long _version; // increment edildiğinde tüm entry'ler invalid

    private record CacheEntry(SearchResponse Response, DateTime ExpiresAt, long Version, long LastAccessTicks);

    public bool TryGet(SearchRequest req, out SearchResponse response)
    {
        var key = Key(req);
        if (_store.TryGetValue(key, out var entry))
        {
            if (entry.Version == Volatile.Read(ref _version) && entry.ExpiresAt > DateTime.UtcNow)
            {
                // Erişim zamanını güncelle (LRU için)
                _store[key] = entry with { LastAccessTicks = DateTime.UtcNow.Ticks };
                response = entry.Response;
                return true;
            }
            _store.TryRemove(key, out _);
        }
        response = null!;
        return false;
    }

    public void Set(SearchRequest req, SearchResponse response)
    {
        var key = Key(req);
        var entry = new CacheEntry(response, DateTime.UtcNow + Ttl,
            Volatile.Read(ref _version), DateTime.UtcNow.Ticks);
        _store[key] = entry;

        if (_store.Count > Capacity) EvictOldest();
    }

    /// <summary>Tüm cache'i invalidate eder. Upload/update/delete sonrası çağrılır.</summary>
    public void InvalidateAll() => Interlocked.Increment(ref _version);

    private void EvictOldest()
    {
        // Capacity'i %20 düşür — sık eviction maliyetini azaltır.
        var target = (int)(Capacity * 0.8);
        var toRemove = _store.Count - target;
        if (toRemove <= 0) return;

        var oldest = _store.OrderBy(kv => kv.Value.LastAccessTicks).Take(toRemove).Select(kv => kv.Key).ToList();
        foreach (var k in oldest) _store.TryRemove(k, out _);
    }

    private static string Key(SearchRequest r) =>
        $"{r.Query?.Trim().ToLowerInvariant()}|{r.Type}|{r.Owner}|{r.From:o}|{r.To:o}|" +
        $"{(r.Tags is null ? "" : string.Join(",", r.Tags.OrderBy(t => t)))}|{r.Page}|{r.PageSize}";
}
