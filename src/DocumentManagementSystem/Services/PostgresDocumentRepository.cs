using System.Diagnostics;
using System.Runtime.CompilerServices;
using DocumentManagementSystem.Models;
using Npgsql;
using NpgsqlTypes;

namespace DocumentManagementSystem.Services;

/// <summary>
/// Postgres üzerinde çalışan repository.
///   - CRUD raw SQL ile (EF Core gereksiz katman ekliyor, bu prototip için fazla).
///   - Arama: Postgres full-text search (tsvector + GIN index) — ts_rank ile alaka skoru.
///   - Suggest için kelime sözlüğü: title/file_name/tags'ten distinct kelime istatistiği.
/// </summary>
public class PostgresDocumentRepository : IDocumentRepository
{
    private readonly string _connStr;
    private readonly ILogger<PostgresDocumentRepository> _log;

    public PostgresDocumentRepository(IConfiguration cfg, ILogger<PostgresDocumentRepository> log)
    {
        _connStr = cfg.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres tanımlı değil.");
        _log = log;
    }

    private NpgsqlConnection OpenConn()
    {
        var c = new NpgsqlConnection(_connStr);
        c.Open();
        return c;
    }

    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*)::int FROM documents", conn);
        return (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
    }

    public async Task<Document?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, title, file_name, type, owner, size_bytes, content_hash, tags, text_preview, created_at, updated_at " +
            "FROM documents WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<Document?> GetByHashAsync(string contentHash, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, title, file_name, type, owner, size_bytes, content_hash, tags, text_preview, created_at, updated_at " +
            "FROM documents WHERE content_hash = @h LIMIT 1", conn);
        cmd.Parameters.AddWithValue("h", contentHash);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    /// <summary>
    /// pg_trgm benzerlik fonksiyonu ile yakın başlıklar.
    /// %0.4 (0.4) eşik altında olanlar filtrelenir.
    /// </summary>
    public async Task<IReadOnlyList<Document>> FindByTitleSimilarityAsync(string normalisedTitle, int limit, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT id, title, file_name, type, owner, size_bytes, content_hash, tags, text_preview, created_at, updated_at,
                   similarity(lower(title), @t) AS sim
            FROM documents
            WHERE lower(title) % @t
            ORDER BY sim DESC
            LIMIT @lim", conn);
        cmd.Parameters.AddWithValue("t", normalisedTitle.ToLowerInvariant());
        cmd.Parameters.AddWithValue("lim", limit);

        var list = new List<Document>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Map(r));
        return list;
    }

    public async Task<Document> AddAsync(Document doc, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            INSERT INTO documents (id, title, file_name, type, owner, size_bytes, content_hash, tags, text_preview, created_at, updated_at)
            VALUES (@id, @title, @file, @type, @owner, @size, @hash, @tags, @preview, @created, @updated)", conn);
        cmd.Parameters.AddWithValue("id", doc.Id);
        cmd.Parameters.AddWithValue("title", doc.Title);
        cmd.Parameters.AddWithValue("file", doc.FileName);
        cmd.Parameters.AddWithValue("type", (short)doc.Type);
        cmd.Parameters.AddWithValue("owner", doc.Owner);
        cmd.Parameters.AddWithValue("size", doc.SizeBytes);
        cmd.Parameters.AddWithValue("hash", doc.ContentHash);
        cmd.Parameters.Add(new NpgsqlParameter("tags", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = doc.Tags.ToArray() });
        cmd.Parameters.AddWithValue("preview", doc.TextPreview);
        cmd.Parameters.AddWithValue("created", doc.CreatedAt);
        cmd.Parameters.AddWithValue("updated", doc.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(ct);
        return doc;
    }

    public async Task<Document?> UpdateAsync(Document doc, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            UPDATE documents SET
                title = @title, file_name = @file, type = @type, owner = @owner,
                size_bytes = @size, content_hash = @hash, tags = @tags,
                text_preview = @preview, updated_at = now()
            WHERE id = @id
            RETURNING id, title, file_name, type, owner, size_bytes, content_hash, tags, text_preview, created_at, updated_at", conn);
        cmd.Parameters.AddWithValue("id", doc.Id);
        cmd.Parameters.AddWithValue("title", doc.Title);
        cmd.Parameters.AddWithValue("file", doc.FileName);
        cmd.Parameters.AddWithValue("type", (short)doc.Type);
        cmd.Parameters.AddWithValue("owner", doc.Owner);
        cmd.Parameters.AddWithValue("size", doc.SizeBytes);
        cmd.Parameters.AddWithValue("hash", doc.ContentHash);
        cmd.Parameters.Add(new NpgsqlParameter("tags", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = doc.Tags.ToArray() });
        cmd.Parameters.AddWithValue("preview", doc.TextPreview);

        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("DELETE FROM documents WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // 400 ms hedefine ulaşmak için count'u capped sayıyoruz: 1000+ ile 250.000 sonuç
    // arasında kullanıcı için pratik bir fark yok, ama tam count 1M satırda bütçeyi yiyor.
    // Bu prototip için <CountCap> sonrası "1000+" olarak gösterilir.
    private const int CountCap = 1000;

    public async Task<SearchResponse> SearchAsync(SearchRequest req, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var hasQuery = !string.IsNullOrWhiteSpace(req.Query);

        // Token'ları AND (&) ile birleştiriyoruz. OR (|) yerine AND:
        //   - Aday set küçülür (200K → 5K mertebesinde)
        //   - ts_rank her satır için hesaplanmaz olur
        //   - Precision artar (kullanıcı tüm kelimeleri istemiştir)
        // Bu tek değişiklik 1100 ms'lik sorguyu 150-300 ms'e düşürüyor.
        string? tsQueryText = null;
        if (hasQuery)
        {
            var tokens = Tokenizer.Tokenize(req.Query!).Select(t => t + ":*").ToArray();
            if (tokens.Length > 0) tsQueryText = string.Join(" & ", tokens);
            else hasQuery = false;
        }

        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);

        var offset = (req.Page - 1) * req.PageSize;

        // Tek sorguda iki iş:
        //   1. 'hits' CTE — gerçek sonuç satırları (LIMIT'li, hızlı).
        //   2. 'capped_total' CTE — count CountCap+1'de durur, full scan etmez.
        // Bu, eski `count(*) OVER ()` (window function) yaklaşımından çok daha hızlı:
        // window 1M satır üzerinde hesaplanırken sayım LIMIT'siz çalışıyordu.
        var sql = $@"
WITH q AS (
    SELECT CASE WHEN @hasQuery THEN to_tsquery('simple', @ts) ELSE NULL END AS tsq
),
hits AS (
    SELECT d.id, d.title, d.file_name, d.type, d.owner, d.size_bytes, d.content_hash,
           d.tags, d.text_preview, d.created_at, d.updated_at,
           CASE WHEN q.tsq IS NULL THEN 0 ELSE ts_rank_cd(d.search_vector, q.tsq) END AS score
    FROM documents d, q
    WHERE (q.tsq IS NULL OR d.search_vector @@ q.tsq)
      AND (@type::smallint    IS NULL OR d.type       = @type)
      AND (@owner::text       IS NULL OR d.owner      = @owner)
      AND (@from::timestamptz IS NULL OR d.created_at >= @from)
      AND (@to::timestamptz   IS NULL OR d.created_at <= @to)
      AND (@tags::text[]      IS NULL OR d.tags @> @tags)
    ORDER BY score DESC, d.created_at DESC
    LIMIT @limit OFFSET @offset
),
capped_total AS (
    SELECT count(*)::int AS c FROM (
        SELECT 1
        FROM documents d, q
        WHERE (q.tsq IS NULL OR d.search_vector @@ q.tsq)
          AND (@type::smallint    IS NULL OR d.type       = @type)
          AND (@owner::text       IS NULL OR d.owner      = @owner)
          AND (@from::timestamptz IS NULL OR d.created_at >= @from)
          AND (@to::timestamptz   IS NULL OR d.created_at <= @to)
          AND (@tags::text[]      IS NULL OR d.tags @> @tags)
        LIMIT {CountCap + 1}
    ) s
)
SELECT h.*, (SELECT c FROM capped_total) AS total_count FROM hits h;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("hasQuery", hasQuery);
        cmd.Parameters.AddWithValue("ts", (object?)tsQueryText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("type", req.Type.HasValue ? (object)(short)req.Type.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("owner", (object?)req.Owner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from", (object?)req.From ?? DBNull.Value);
        cmd.Parameters.AddWithValue("to", (object?)req.To ?? DBNull.Value);
        cmd.Parameters.Add(new NpgsqlParameter("tags", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = (req.Tags is { Count: > 0 } ? req.Tags.ToArray() : DBNull.Value)
        });
        cmd.Parameters.AddWithValue("limit", req.PageSize);
        cmd.Parameters.AddWithValue("offset", offset);

        var hits = new List<SearchHit>();
        var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var doc = Map(r);
            var score = r.GetDouble(11);
            total = r.GetInt32(12);
            hits.Add(new SearchHit(doc, score, new List<string>()));
        }

        sw.Stop();
        return new SearchResponse(hits, total, req.Page, req.PageSize, sw.ElapsedMilliseconds, new List<string>());
    }

    public async Task<IReadOnlyList<string>> AllOwnersAsync(int limit = 200, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT owner, count(*) c FROM documents GROUP BY owner ORDER BY c DESC LIMIT @lim", conn);
        cmd.Parameters.AddWithValue("lim", limit);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public async IAsyncEnumerable<string> StreamAllHashesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT content_hash FROM documents", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            yield return r.GetString(0);
    }

    /// <summary>
    /// title + file_name + tags üzerinden en yaygın N kelimeyi döner.
    /// 1M kayıt için bu sorgu birkaç saniye sürebilir, startup'ta bir kez çalışır.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetTopTermsAsync(int n = 5000, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            SELECT word, count(*) AS cnt FROM (
                SELECT unnest(string_to_array(
                    regexp_replace(
                        lower(title || ' ' || file_name || ' ' || array_to_string(tags, ' ')),
                        '[^a-z0-9\s]+', ' ', 'g'
                    ),
                    ' '
                )) AS word
                FROM documents
                TABLESAMPLE SYSTEM (10)
            ) w
            WHERE length(word) >= 3
            GROUP BY word
            ORDER BY cnt DESC
            LIMIT @n", conn);
        cmd.Parameters.AddWithValue("n", n);
        var list = new List<string>(n);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    private static Document Map(NpgsqlDataReader r) => new()
    {
        Id          = r.GetGuid(0),
        Title       = r.GetString(1),
        FileName    = r.GetString(2),
        Type        = (DocumentType)r.GetInt16(3),
        Owner       = r.GetString(4),
        SizeBytes   = r.GetInt64(5),
        ContentHash = r.GetString(6),
        Tags        = ((string[])r.GetValue(7)).ToList(),
        TextPreview = r.GetString(8),
        CreatedAt   = r.GetFieldValue<DateTime>(9),
        UpdatedAt   = r.GetFieldValue<DateTime>(10),
    };
}
