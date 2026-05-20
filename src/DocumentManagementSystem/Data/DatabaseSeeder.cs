using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DocumentManagementSystem.Models;
using Npgsql;
using NpgsqlTypes;

namespace DocumentManagementSystem.Data;

/// <summary>
/// İlk başlangıçta DB boşsa Postgres'e hedef sayıda kayıt yükler.
/// Binary COPY ile yaklaşık 30-60 saniye sürer (1M kayıt, ortalama bir geliştirici makinesinde).
///
/// Üretilen veri "gerçekçi şirket dağınıklığını" yansıtacak şekilde dizayn edildi:
///   - ~2% oranında kasten birebir aynı içerik (farklı title'la) — bloom filter testi için.
///   - Yakın başlıklar ("v2", "kopya", "final" gibi son ekler).
///   - 2 yıllık tarih dağılımı.
///   - 50 farklı kullanıcı, 2000 farklı müşteri ismi.
/// </summary>
public class DatabaseSeeder
{
    private readonly string _connStr;
    private readonly ILogger<DatabaseSeeder> _log;
    private readonly int _targetCount;
    private readonly int _batchSize;
    private readonly double _duplicateRatio;

    public DatabaseSeeder(IConfiguration cfg, ILogger<DatabaseSeeder> log)
    {
        _connStr = cfg.GetConnectionString("Postgres")!;
        _log = log;
        _targetCount    = cfg.GetValue("Seed:TargetCount",    1_000_000);
        _batchSize      = cfg.GetValue("Seed:BatchSize",         50_000);
        _duplicateRatio = cfg.GetValue("Seed:DuplicateRatio",      0.02);
    }

    public async Task SeedIfEmptyAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);

        await using var countCmd = new NpgsqlCommand("SELECT count(*)::int FROM documents", conn);
        var existing = (int)(await countCmd.ExecuteScalarAsync(ct) ?? 0);
        if (existing >= _targetCount)
        {
            _log.LogInformation("Seed atlandı: zaten {Count} kayıt mevcut.", existing);
            return;
        }

        var toInsert = _targetCount - existing;
        _log.LogInformation("Seed başlıyor: {Insert} kayıt yüklenecek (mevcut: {Existing}).", toInsert, existing);

        var sw = Stopwatch.StartNew();
        var inserted = 0;

        // Önceki içerik hash'lerini saklayalım — kasten duplicate yaratmak için bunlardan örnekleyeceğiz.
        var hashPool = new List<(string Hash, string Content)>(capacity: 5000);

        var rng = new Random(42);
        var owners = GenerateOwners();
        var customers = GenerateCustomers();
        var typeLabels = new[] { ("Sözleşme", DocumentType.Contract), ("Teklif", DocumentType.Offer), ("Fatura", DocumentType.Invoice) };
        var suffixes = new[] { "", "", "", " - v2", " - kopya", " - final", " (revize)", " - imza" };
        var tagPool = new[] { "2024", "2025", "2026", "yenileme", "imza", "musteri", "yillik", "aylik", "q1", "q2", "q3", "q4", "revize", "onay", "ekstra" };
        var contentTemplates = new[]
        {
            "{customer} ile {year} yılı için {type} dokümanı. {detail}",
            "{customer} {type}. Tutar: {amount} TL. Detay: {detail}",
            "{type} - {customer} ({year}). {detail}",
            "{customer} için hazırlanan {type}. {detail} Önemli notlar: {amount} TL bedel, {year} dönemi."
        };

        while (inserted < toInsert)
        {
            var batchCount = Math.Min(_batchSize, toInsert - inserted);

            await using var writer = await conn.BeginBinaryImportAsync(
                "COPY documents (id, title, file_name, type, owner, size_bytes, content_hash, tags, text_preview, created_at, updated_at) FROM STDIN (FORMAT BINARY)",
                ct);

            for (int i = 0; i < batchCount; i++)
            {
                var (typeName, type) = typeLabels[rng.Next(typeLabels.Length)];
                var customer = customers[rng.Next(customers.Length)];
                var year = 2024 + rng.Next(3);

                var title = $"{typeName} - {customer} {year}{suffixes[rng.Next(suffixes.Length)]}";
                var fileName = Slugify($"{typeName}-{customer}-{year}") + ".pdf";

                // %duplicateRatio oranında kasten aynı içerikten al — bloom filter testi için.
                string content;
                string contentHash;
                if (hashPool.Count > 100 && rng.NextDouble() < _duplicateRatio)
                {
                    var picked = hashPool[rng.Next(hashPool.Count)];
                    content = picked.Content;
                    contentHash = picked.Hash;
                }
                else
                {
                    var template = contentTemplates[rng.Next(contentTemplates.Length)];
                    content = template
                        .Replace("{customer}", customer)
                        .Replace("{type}", typeName)
                        .Replace("{year}", year.ToString())
                        .Replace("{amount}", (rng.Next(10, 9999) * 1000).ToString())
                        .Replace("{detail}", RandomDetail(rng));
                    contentHash = Sha256Hex(content);
                    if (hashPool.Count < 5000) hashPool.Add((contentHash, content));
                }

                var owner = owners[rng.Next(owners.Length)];
                var tagCount = rng.Next(0, 4);
                var tags = new string[tagCount];
                for (int t = 0; t < tagCount; t++) tags[t] = tagPool[rng.Next(tagPool.Length)];

                // 2 yıllık dağılım
                var createdAt = DateTime.UtcNow.AddDays(-rng.Next(0, 730)).AddHours(-rng.Next(0, 24));
                var updatedAt = createdAt.AddHours(rng.Next(0, 48));

                await writer.StartRowAsync(ct);
                await writer.WriteAsync(Guid.NewGuid(), NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(title, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(fileName, NpgsqlDbType.Text, ct);
                await writer.WriteAsync((short)type, NpgsqlDbType.Smallint, ct);
                await writer.WriteAsync(owner, NpgsqlDbType.Text, ct);
                await writer.WriteAsync((long)Encoding.UTF8.GetByteCount(content), NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(contentHash, NpgsqlDbType.Char, ct);
                await writer.WriteAsync(tags, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                await writer.WriteAsync(content, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(createdAt, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(updatedAt, NpgsqlDbType.TimestampTz, ct);
            }

            await writer.CompleteAsync(ct);
            inserted += batchCount;

            _log.LogInformation("Seed ilerleme: {Inserted}/{Target} ({Pct}%), {Sec:F1} sn",
                inserted, toInsert, inserted * 100 / toInsert, sw.Elapsed.TotalSeconds);
        }

        sw.Stop();
        _log.LogInformation("Seed tamamlandı: {Count} kayıt, {Sec:F1} sn.", inserted, sw.Elapsed.TotalSeconds);
    }

    // ---- helpers ----

    private static string[] GenerateOwners()
    {
        var first = new[] { "ayse", "mehmet", "kaan", "elif", "burak", "zeynep", "can", "deniz", "ece", "fatih",
                            "gizem", "hakan", "irem", "kerem", "leyla", "murat", "nazli", "omer", "pinar", "ramazan",
                            "selin", "tolga", "umut", "vildan", "yusuf" };
        var last = new[] { "yilmaz", "kara", "demir", "sahin", "ozturk", "celik", "aydin", "ozdemir", "arslan", "polat" };
        var combos = new List<string>();
        foreach (var f in first)
            foreach (var l in last) combos.Add($"{f}.{l}");
        return combos.Take(50).ToArray();
    }

    private static string[] GenerateCustomers()
    {
        var prefixes = new[] {
            "Acme", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Theta", "Iota", "Kappa",
            "Lambda", "Mu", "Nu", "Xi", "Omicron", "Pi", "Rho", "Sigma", "Tau", "Upsilon",
            "Phi", "Chi", "Psi", "Omega", "Anadolu", "Marmara", "Ege", "Karadeniz", "Akdeniz", "Toros",
            "Bosphorus", "Istanbul", "Ankara", "Izmir", "Bursa", "Antalya", "Kocaeli", "Adana", "Gaziantep", "Mersin"
        };
        var suffixes = new[] {
            "Tedarik", "Yazılım", "Lojistik", "Danışmanlık", "Bulut", "Donanım", "Eğitim", "Pazarlama",
            "Lisans", "Sigorta", "Analitik", "İnşaat", "Güvenlik", "Temizlik", "Kırtasiye", "Telekom",
            "Enerji", "Ulaşım", "Sağlık", "Finans", "Gıda", "Tekstil", "Otomotiv", "Kimya", "Metal",
            "Plastik", "Cam", "Mobilya", "Mücevher", "Tarım", "Hayvancılık", "Balıkçılık", "Madencilik",
            "Petrol", "Doğalgaz", "Kömür", "Beton", "Çelik", "Alüminyum", "Bakır", "Çinko", "Nikel",
            "Kâğıt", "Karton", "Boya", "Yapıştırıcı", "Vida", "Civata", "Kablo", "Lamba", "Pil"
        };
        var combos = new List<string>(prefixes.Length * suffixes.Length);
        foreach (var p in prefixes)
            foreach (var s in suffixes) combos.Add($"{p} {s}");
        return combos.Take(2000).ToArray();
    }

    private static string Slugify(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        // Türkçe karakterleri kabaca dönüştür
        return slug.Replace('ş', 's').Replace('ı', 'i').Replace('ğ', 'g')
                   .Replace('ü', 'u').Replace('ö', 'o').Replace('ç', 'c');
    }

    private static string RandomDetail(Random rng)
    {
        var phrases = new[] {
            "yıllık bakım dahildir",
            "fesih süresi 60 gündür",
            "ödeme vadeleri 30/60/90 gün",
            "KDV hariç tutardır",
            "imza aşamasında",
            "yenileme opsiyonludur",
            "ek protokol gerekebilir",
            "uzaktan teslim edilebilir",
            "saha ekibi eşliğinde",
            "test ortamı dahildir"
        };
        return phrases[rng.Next(phrases.Length)];
    }

    private static string Sha256Hex(string content)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(content), hash);
        return Convert.ToHexString(hash);
    }
}
