using System.Text.Json.Serialization;
using DocumentManagementSystem.Data;
using DocumentManagementSystem.Services;

// Diagnostic mode — `dotnet run -- --print-hashes` ile FNV-1a hash çıktısını
// JS implementasyonuyla doğrulamak için kullanılır. DB'ye gitmez.
if (args.Length > 0 && args[0] == "--print-hashes")
{
    var samples = new[] { "hello", "Acme", "ABCDEF0123456789", "sözleşme", "" };
    foreach (var s in samples)
        for (uint k = 0; k < 3; k++)
            Console.WriteLine($"\"{s}\" seed={k} -> {DocumentManagementSystem.Services.BloomFilter.Fnv1a(s, k):x8}");
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    .AddJsonOptions(opts =>
    {
        opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Repository — Postgres tabanlı tek implementasyon.
builder.Services.AddSingleton<IDocumentRepository, PostgresDocumentRepository>();

// Sunucu tarafı yardımcı servisler.
builder.Services.AddSingleton<IDuplicateDetector, DuplicateDetector>();
builder.Services.AddSingleton<BloomFilterService>();
builder.Services.AddSingleton<TermDictionaryService>();
builder.Services.AddSingleton<SearchCache>();
builder.Services.AddSingleton<DatabaseSeeder>();

var app = builder.Build();

// Başlangıçta:
//   1. (gerekirse) seed verisini yükle (1M satır, ~30-60sn)
//   2. Bloom filter snapshot'ını DB'den inşa et
//   3. Suggest için term sözlüğünü hesapla
using (var scope = app.Services.CreateScope())
{
    var log = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
        await seeder.SeedIfEmptyAsync();

        var bloom = scope.ServiceProvider.GetRequiredService<BloomFilterService>();
        await bloom.RebuildAsync();

        var dict = scope.ServiceProvider.GetRequiredService<TermDictionaryService>();
        await dict.RebuildAsync();

        // Pre-warm: Postgres shared_buffers'ını ısıt — ilk gerçek kullanıcı sorgusu
        // cold cache'e değil sıcak cache'e düşsün. Burada birkaç yaygın sorgu çalıştırarak
        // GIN indeks bloklarını ve tsvector kolonunu RAM'e taşıyoruz.
        var repo = scope.ServiceProvider.GetRequiredService<IDocumentRepository>();
        var warmSw = System.Diagnostics.Stopwatch.StartNew();
        var warmupQueries = new[] { "sozlesme", "teklif", "fatura", "acme", "2025" };
        foreach (var q in warmupQueries)
        {
            await repo.SearchAsync(new DocumentManagementSystem.Models.SearchRequest
            {
                Query = q, Page = 1, PageSize = 20
            });
        }
        // Filtreli sorgu da ısınsın
        await repo.SearchAsync(new DocumentManagementSystem.Models.SearchRequest
        {
            Type = DocumentManagementSystem.Models.DocumentType.Contract, Page = 1, PageSize = 20
        });
        warmSw.Stop();
        var startupLog = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        startupLog.LogInformation("Cache pre-warm tamamlandı: {Ms} ms", warmSw.ElapsedMilliseconds);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Başlangıç hazırlığı başarısız oldu. PostgreSQL erişilebilir mi? " +
                         "Lütfen `docker compose up -d` çalıştırdığınızdan emin olun.");
        throw;
    }
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

app.Run();
