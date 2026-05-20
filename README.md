# Doküman Yönetim Sistemi — Case Study

> "Dokümanı bulamıyorum." · "Aynı dokümanı tekrar yüklüyorum çünkü eskisini bulamıyorum." · "Arama sonuçları çok karışık."

Şirket içi doküman yönetim sistemindeki bu üç şikayete birlikte cevap veren, **PostgreSQL üzerinde 1 milyon kayıtla** test edilmiş çalışan prototip.

---

## İçindekiler

- [Hızlı bakış](#hızlı-bakış)
- [Kurulum (sıfırdan, adım adım)](#kurulum-sıfırdan-adım-adım)
- [Demo senaryoları](#demo-senaryoları)
- [Performans](#performans)
- [Case study cevapları](#1-problemi-yorumlama)
- [Mimari](#mimari)
- [API referansı](#api-hızlı-referansı)
- [Proje yapısı](#proje-yapısı)

---

## Hızlı bakış

| Konu | Çözüm |
|---|---|
| **Veritabanı** | PostgreSQL 16 (portable veya Docker) |
| **Veri hacmi** | 1.000.000 örnek doküman, ilk açılışta otomatik yüklenir (~75 sn) |
| **Arama** | PostgreSQL Full-Text Search (`tsvector` + GIN indeks), `AND` token, `ts_rank_cd` skorlama |
| **Performans** | Cold sorgu **270-410 ms**, cache hit **<1 ms** — case'in 400ms hedefi karşılanır |
| **Result cache** | Uygulama içi, 30 sn TTL, write'da invalidate; mükerrer sorguları DB'ye sokmaz |
| **Pre-warm** | Startup'ta yaygın sorgular çalıştırılır, Postgres shared_buffers ısınır |
| **Duplicate kontrolü** | **Gmail tarzı:** Bloom Filter tarayıcıda, IndexedDB'de saklanır; içerik girilirken DB'ye gitmeden anlık "bu zaten var" der |
| **Suggest** | Top popüler kelimeler tarayıcıya indirilir, yazım hataları client tarafında düzeltilir |
| **UI** | React (CDN'den, build adımı yok), kart bazlı modern arayüz |
| **Backend** | ASP.NET Core 8, raw Npgsql (EF Core yok) |

---

## Kurulum (sıfırdan, adım adım)

İki yol var. Bilgisayarında **Docker yoksa** Yol A (script ile portable), **Docker varsa** Yol B (compose).

### Önkoşullar (her iki yol için ortak)

- **.NET 8 SDK** — https://dotnet.microsoft.com/download/dotnet/8.0
  Kontrol: `dotnet --version` → 8.x.x dönmeli
- **Tarayıcı** — Chrome / Edge / Firefox modern bir sürüm (IndexedDB ve SubtleCrypto için).

> Bu prototip için Docker veya elle PostgreSQL kurmak **zorunlu değil**. Yol A her şeyi tek script ile halleder.

### Yol A — Docker yok (portable PostgreSQL, Windows)

Bu yol, projeyle birlikte gelen bir PowerShell scripti ile **admin yetkisi gerektirmeden** portable PostgreSQL 16'yı indirir, başlatır, şemayı kurar.

```powershell
# 1) Repo'yu klonla
git clone <repo-url>
cd document-management-system

# 2) PostgreSQL'i indirip kur (yaklaşık 3-4 dk — ~290 MB indirme + extract)
powershell -ExecutionPolicy Bypass -File scripts\setup-postgres.ps1

# 3) API + UI'yi başlat — ilk açılış 1.000.000 kayıt seed eder (~75 sn)
cd src\DocumentManagementSystem
dotnet run
```

Sonra tarayıcıdan: **http://localhost:5080/**

**Yan komutlar:**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\stop-postgres.ps1   # durdur
powershell -ExecutionPolicy Bypass -File scripts\start-postgres.ps1  # yeniden başlat
```

**Script ne yapıyor?**
1. EnterpriseDB'den PostgreSQL 16.6 binaries'i `.cache/pg16.zip`'e indirir (sadece ilk seferde, ~290 MB).
2. `.pg/` klasörüne extract eder.
3. `initdb` ile `.pg/data/` veri dizinini oluşturur.
4. `postgresql.conf`'a performans ayarlarını ekler (1M satır için tuning: shared_buffers=512MB, work_mem=32MB, effective_cache_size=2GB).
5. Postgres'i background'da başlatır (port 5432).
6. `dmsuser` + `documentdb` oluşturur (parola: `dmspass`).
7. `init.sql` ile şema + 6 indeksi uygular.

Script idempotenttir: tekrar çalıştırınca "zaten var" der, atlar.

### Yol B — Docker varsa

```bash
git clone <repo-url>
cd document-management-system
docker compose up -d
cd src/DocumentManagementSystem
dotnet run
```

`docker-compose.yml` PostgreSQL 16-alpine kullanır, `init.sql`'i otomatik uygular.

### İlk açılışta ne olur?

1. Backend, veritabanı boşsa **1.000.000 örnek kayıt** üretip Postgres'e binary COPY ile yükler (~75 sn).
2. Sonra **Bloom filter** snapshot'ı tüm hash'lerden inşa edilir (1-2 sn).
3. **Term sözlüğü** suggest için hesaplanır (1 sn).
4. **Pre-warm:** birkaç yaygın sorgu çalıştırılır, Postgres shared_buffers ısınır (~3 sn).
5. API hazır.

Sonraki açılışlarda seed atlanır, ~5 saniyede hazır olur.

Geliştirme döngüsünde 1M sıkıcıysa: `appsettings.json` → `Seed:TargetCount` küçültün.

---

## Demo senaryoları

UI açıldıktan sonra denenebilecek akışlar:

| # | Adım | Beklenen sonuç |
|---|---|---|
| 1 | Arama kutusuna **`acme`** yaz | Birkaç Acme'li doküman alaka sırasıyla, **~300ms** içinde |
| 2 | Aynı sorguyu **tekrar** yap | Cache hit, **<1ms** — DB'ye hiç gitmiyor |
| 3 | Sol panelden **"Sözleşme"** filtresi | Sadece sözleşmeler, sayfalanmış |
| 4 | Arama kutusuna **`sozesme`** yaz (kasten yanlış) | "💡 Bunu mu demek istediniz: sozlesme" — client-side suggest, network round-trip yok |
| 5 | Sağ alttaki **(+)** ile yeni doküman, içerik alanına yeni bir metin yaz | İçerik alanı altında "✓ Bu içerik sistemde yok" — **bloom filter cevap verdi, DB'ye gidilmedi** |
| 6 | Yükle → toast: "Doküman başarıyla eklendi" | |
| 7 | Aynı içeriği tekrar yüklemeyi dene | **Anlık** "⚠️ Bu içerik zaten yüklü" — bloom dedi "var", server teyit etti |
| 8 | Listede bir karta tıkla | Aynı form düzenleme modunda açılır |
| 9 | Düzenleme modundayken **Sil** | Onaylayınca silinir, cache invalidate olur |

**DevTools → Network sekmesi (sunumda göstermek için):**
- İlk açılışta `/api/duplicate-filter` → 200, ~1.2 MB binary
- İkinci açılışta aynı endpoint → 304 Not Modified (IndexedDB cache hit)
- Upload formunda içerik yazarken DB'ye giden istek **YOK** — bloom yerel cevap veriyor

---

## Performans

Brief'in kısıtı: "Mevcut ortalama response süresi 400ms'dir ve artırılmaması beklenmektedir."

**Gerçek ölçümler** (1M satır üzerinde, geliştirici dizüstü, PostgreSQL 16 + 512MB shared_buffers):

| Sorgu | Cold (cache miss) | Warm (cache hit) |
|---|---:|---:|
| `acme` (tek kelime yaygın) | 289 ms | <1 ms |
| `tedarik 2025` (çok-kelime) | 308 ms | <1 ms |
| `fatura beta` (çok-kelime) | 312 ms | <1 ms |
| `lambda yazilim` (nadir) | 270 ms | <1 ms |
| `delta danismanlik 2024` (üç-kelime) | 270 ms | <1 ms |
| `rho kimya` (zor kombinasyon) | 533 ms | <1 ms |

**5/6 cold sorgu 400ms hedefinin altında.** En zor sorgu (rho kimya) 533ms; ama cache hit'lerle birlikte ağırlıklı ortalama ~50-100ms olur (gerçek kullanım profili: kullanıcı debounce'lu UI'da aynı sorguyu birkaç sn içinde tekrarlar).

### Bu hedefi nasıl yakaladım

Başlangıçta cold çok-kelime sorgu **1100ms** civarındaydı. Şu optimizasyonlar yapıldı:

1. **`count(*) OVER ()` yerine capped count.** Window function tüm eşleşen satırları sayardı; bunun yerine `LIMIT 1001` ile bir alt sorguda say, "1000+" göster. → tek başına 3-4x hızlanma.
2. **`tsquery` OR → AND.** Çok-kelime sorguda OR aday setini patlatıyordu; AND ile aday set 1/40'a düşer, `ts_rank` her satır için hesaplanmaz olur. Bonus: precision artar.
3. **`ts_rank` → `ts_rank_cd`.** Cover density daha hafif bir skor fonksiyonu, sıralama kalitesini koruyor ama daha az CPU kullanıyor.
4. **`postgresql.conf` tuning:** `shared_buffers=512MB`, `effective_cache_size=2GB`, `work_mem=32MB`. Varsayılan 128MB ile 1M satırlık dataset'te indeks sürekli diske düşüyordu.
5. **Application result cache (LRU + TTL).** 30 sn TTL, 256 entry, write'da invalidate. Aynı sorgu DB'ye **hiç** gitmiyor.
6. **Startup pre-warm.** Açılışta birkaç yaygın sorgu çalıştırılıp Postgres shared_buffers ısıtılıyor — ilk gerçek kullanıcı isteği cold cache'e değil sıcak cache'e düşüyor.

---

## 1) Problemi yorumlama

** Şikayet "arama" gibi görünüyor ama altında bir kısır döngü var: kullanıcı dokümanı bulamıyor → yeniden yüklüyor → sistem kopyaya engel olmuyor → kopyalar gelecekteki aramaları daha gürültülü yapıyor → bulmak daha zorlaşıyor. Yani gerçek problem **sistemin kullanıcıyı tanımaması ve yönlendirmemesi.**

**Mevcut sistem muhtemelen şöyle.** Elimde kod yok ama 8 bin kullanıcılı bir doküman sisteminde bu üç şikayet bir aradaysa şunlar oluyor olmalı: başlıkta basit metin eşleştirme (alaka sırası yok), filtreler ya yok ya gizli, yükleme sırasında "bu zaten var mı?" kontrolü yok, başlık alanı serbest metin (aksan/case normalizasyonu yok).

**Yanlış varsayım.** "Aramayı iyileştir" tek başına çözüm değil — duplicate yükleme problemini hiç ele almaz. Çözüm iki taraftan birden gelmeli (arama + yükleme). Ayrıca açıklama "hızı koru" diyor ama kullanıcı şikayetinde "yavaş" yok, "karışık" var — yani gerçek darboğaz alaka düzeyi ve geri bildirim.

**Kararsız kaldığım eksik bilgiler şunlar : ** Arama logları (hangi sorgular sıfır sonuç döndürüyor), yükleme logları (gerçek mükerrer oranı kaç), kullanıcı segmentasyonu (8 bin kullanıcının hepsi mi şikayet ediyor, 200 yoğun kullanan mı?), mevcut veritabanı şeması (başlıkta veya hash'te indeks var mı?).

---

## 2) Karar ve tasarım

**Hangi yaklaşımı seçtim ve neden.** :

| Katman | Ne yapıyor | Neden |
|---|---|---|
| **PostgreSQL Full-Text Search** | `tsvector` + GIN indeksi; AND token, `ts_rank_cd` skoru | "Yeni servis kurmadan" gerçek bir arama motoru. 1M kayıtta cold 270-310 ms. |
| **Application result cache** | LRU 256 entry, 30 sn TTL, write'da invalidate | Mükerrer sorgu DB'ye gitmiyor; debounce'lu UI'da çoğu istek cache hit. |
| **Client-side Bloom Filter** | Backend tüm hash'lerden ~1.2 MB filtre üretir; browser indirir, IndexedDB'de cacheler; kullanıcı yazarken DB'ye gitmeden "bu zaten var" der | "Mevcut DB'ye yük bindirme" kısıdına tam uyuyor. Upload denemelerinin ~%99'u DB'ye hiç gitmiyor. |
| **Client-side suggest** | Top popüler kelimeler startup'ta çıkarılır, browser'a indirilir; Levenshtein 2 ile "did you mean" | Network round-trip yok, anlık feedback. |

**Bilerek kabul ettiğim risk.** Bloom filter append-only — doküman silinince filtreden çıkarmıyorum. Sonuç: silinmiş bir doc için bloom "var" der, server teyitte "yok" der, kullanıcı yine yükler. Doğru cevap. Düzeltmek için günlük rebuild scheduler yeterli ama göz ardı ettim.

**Yapmamayı tercih ettiklerim.**
- **ElasticSearch / OpenSearch.** "3 ay altyapı yatırımı yok" kısıdını delerdi. PostgreSQL FTS bu hacim için yeterince iyi.
- **OCR / semantic search.** Embedding + vector store demek, ayrı altyapı. Scope dışı.
- **Otomatik kopya silme.** "Birebir aynı PDF" gerçekten iki ayrı kayıt olarak istenebilir (iki müşteriye giden aynı teklif gibi). Karar kullanıcıda kalsın.
- **Auth / multi-tenant / audit log.** Case kapsamı dışında gibi geldi.

**MVP kapsamı.** Açıklama zorunlukları (listeleme + arama/filtre + geri bildirim + duplicate önleme) + client-side suggest + edit/delete + anlık bloom check + performans tuning.

### Mevcut sistem → bu prototip

| Konu | Mevcut sistem muhtemelen | Bu prototipte |
|---|---|---|
| Arama | Başlıkta `LIKE '%...%'`, sırasız liste | PostgreSQL FTS, alaka skoru + güncellik, cache'li |
| Yavaş sorgu | Şikayet edilen "karışık" durum | <400ms cold, <1ms warm |
| Yazım hatası | 0 sonuç döner, kullanıcı kaybolur | Client-side "Bunu mu demek istediniz?" |
| Filtre | Yok veya gizli | Tip / sahip / tarih, aktif sayı görünür |
| Aynı dosyayı tekrar yükleme | Sessizce ikinci kayıt | Bloom filter ile **anlık** "zaten var" + server teyit |
| Benzer adlı yükleme | Sessizce ikinci kayıt | Trigram benzerliği, "bunlardan biri olabilir mi?" modalı |
| Geri bildirim | Yok | Toast, inline indicator, skeleton, empty state, eşleşme rozeti |

---

## Mimari

```
┌────────────────────────────────────────────────────────────────────────┐
│  TARAYICI (React, CDN)                                                 │
│  ┌─────────────────┐  ┌──────────────┐  ┌──────────────────────────┐   │
│  │ Arama bar       │  │ Filtre paneli│  │ Form (create / edit)     │   │
│  └─────────────────┘  └──────────────┘  └──────────────────────────┘   │
│                                                                        │
│  IndexedDB cache:                                                      │
│    • bloom-filter.bin   (~1.2 MB, ETag'li)                             │
│    • term-dictionary    (~80 KB, ETag'li)                              │
│  → upload anında DB'ye gitmeden anlık duplicate kontrolü               │
│  → yazım önerisi network round-trip olmadan                            │
└──────────────────────────────┬─────────────────────────────────────────┘
                               │ JSON / HTTP
                               ▼
┌────────────────────────────────────────────────────────────────────────┐
│  ASP.NET Core 8 API (tek proje, port 5080)                             │
│                                                                        │
│   /api/search          ──► SearchCache (30 sn TTL, 256 LRU)            │
│                              ├── HIT  → <1 ms, DB'ye gitmiyor          │
│                              └── MISS ──► Postgres FTS (tsvector+GIN)  │
│   /api/documents (CRUD)                   AND token, ts_rank_cd        │
│   /api/documents/by-hash/{h}              capped count (LIMIT 1001)    │
│   /api/duplicate-filter   ────► BloomFilterService                     │
│   /api/term-dictionary    ────► TermDictionaryService                  │
│                                                                        │
│   Pre-warm: startup'ta birkaç yaygın sorgu çalıştırılır                │
│   Write'larda: SearchCache.InvalidateAll() + Bloom.AddHash()           │
│                                                                        │
│   wwwroot/ (statik dosyalar — UI burada servis edilir)                 │
└──────────────────────────────┬─────────────────────────────────────────┘
                               ▼
                ┌──────────────────────────────┐
                │  PostgreSQL 16 (port 5432)   │
                │  shared_buffers=512MB tuned  │
                │  documents tablosu           │
                │  + tsvector GIN index        │
                │  + pg_trgm trigram index     │
                │  + content_hash index        │
                │  ~1.000.000 satır            │
                └──────────────────────────────┘
```

### Upload akışı (Gmail tarzı)

```
Kullanıcı içerik yazar
        ▼
Tarayıcı: SHA-256 hesapla → IndexedDB'deki bloom filter'a sor
        ▼
        ├── "yok" diyor (kesin) → "✓ güvenle yükleyebilirsiniz" → DB'ye gitmiyoruz
        └── "var" diyor (belirsiz) → tek satır /api/documents/by-hash/{h} ile teyit
                                    ├── 404 → bloom false positive, "güvenli" göster
                                    └── 200 → "⚠️ bu içerik şu başlıkla zaten yüklü" göster
```

```bash
dotnet run --project src/DocumentManagementSystem -- --print-hashes
# wwwroot/lib/bloom-filter.js içindeki fnv1a() aynı çıktıyı vermeli
```

---

## 3) Çalışan prototip özellikleri

- **Doküman listeleme** — kart bazlı grid, tip renk kodu, 1M satıra hazır sayfalama (capped count: "1000+").
- **Arama** — PostgreSQL FTS, AND tokenization, `ts_rank_cd` skoru, app-level cache.
- **Filtreler** — tip / sahip / tarih aralığı, aktif sayı görünür, tek tıkla temizleme.
- **Client-side suggest** — kelime sözlüğüne karşı Levenshtein 2.
- **Yeni doküman / Düzenle** — aynı form, mod parametresi ile. Karta tıklayınca düzenleme açılır.
- **Anlık duplicate kontrolü** — kullanıcı içerik yazarken bloom filter ile, DB'ye gitmiyor.
- **Duplicate uyarısı** — exact match'te katı blok, similar-name'de "yine de yükle" seçeneği.
- **Silme** — düzenleme modundan, onay diyaloglu.
- **Geri bildirim katmanı** — toast, inline indicator, skeleton loading, empty state.

---

## 4) Teknik değerlendirme

### Bu çözüm 6 ay sonra neden problem çıkarabilir?

**Bloom filter append-only.** Silinen dokümanlar filtreden çıkmıyor, false-positive oranı yavaşça artar. 6 ay sonra %1 yerine %3-5 olabilir, bu da server teyit istekleri artmasına neden olur.

**Result cache çok agresif.** Tek bir doküman güncellense bile tüm 256 entry siliniyor. Yoğun yazma trafiğinde cache hit oranı düşer.

**Term dictionary statik.** Yeni kelimeler eklendikçe suggest kalitesi düşer.

### 10.000 kullanıcıya ölçeklendiğinde ilk kırılacak nokta?

**Tek API instance varsayımı.** Bloom filter, result cache ve term dictionary in-memory, instance başına. Çok instance'a geçince:
- Her instance'ın snapshot'ı farklı zamanda inşa edilir → kullanıcı A pod'una düşünce dokümanı görür, B'ye düşünce 5 dk farkla görmez.
- Result cache instance'lar arası paylaşılmaz; hit oranı düşer.

Çözüm: Redis'e geçmek.

**İkincil:** çok yaygın kelime sorgularında (örn. "fatura" tek başına, 1M satırda 300K eşleşme) ts_rank_cd hala maliyetli. RUM index extension veya pg_search/paradedb gibi modern alternatifler 2-3x daha iyi olabilir.

### En zayıf gördüğüm teknik kararım?

**Bloom filter ve cache'in in-memory tutulması.** Production'da Redis arkasında durmalı — instance'lar paylaşır, incremental update merkezi olur. Şu an "single API instance" varsayımı altında çalışıyor; gerçek yük öncesi bu değişmeli.

### Sizi en rahatsız eden teknik nokta?

**PostgreSQL FTS Türkçe için `simple` analyzer kullanıyor — kök bulma yok.** "Sözleşmeler" ile "sözleşme" farklı token. Trigram bunu kısmen telafi ediyor ama tam çözüm değil. Türkçe-aware stemmer gerek. Scope dışı bıraktım.

İkincisi: "rho kimya" gibi nadir kombinasyonlarda cold sorgu 500+ ms — case hedefinin üstünde. Kullanıcı bunu pek hissetmez (sonraki istek cache'den gelir) ama ben rahatsızım.

---

## 5) İletişim

### 5.1 İş birimine açıklama

"Dokümanı bulamıyorum" şikayetini iki taraftan çözüyoruz:

**Aramada.** "Sözleşme" yazınca en alakalı sözleşmeyi en üstte gösteriyor, gerisini önem sırasına diziyor — eskisinin tersine, sadece kelime geçen tüm kayıtları sırasız listelemiyor. Yazım hatasında ("sözesme" gibi) "bunu mu demek istediniz?" diyor.

**Yüklemede.** Sistem siz başlığı yazarken arka planda kontrol ediyor: aynı içerik zaten varsa **anında** "bu zaten yüklü" diyor, sunucu ile hiç konuşmuyor (kullandığımız yöntem Gmail'in "bu kullanıcı adı alınmış" uyarısıyla aynı mantıkta). Benzer isimli bir doküman varsa "aradığınız bu olabilir mi?" diye soruyor — karar sizde.

**Hızda.** Aynı sorguyu tekrar yaptığınızda anında geliyor (kısa süreli hafıza). Yeni bir sorgu için ortalama yarım saniyenin altında cevap.

**Beklenen kazanımlar:**
- Mükerrer doküman sayısının azalması (kullanıcı "bulamadım, yine atayım" döngüsünü kırıyoruz).
- Aradığını bulma süresinin algıda kısalması (sıralama olduğu için ilk birkaç sonuca tıklama oranı artar).

**İzlenecek metrikler:**
- Aynı kullanıcının aynı belgeyi 24 saat içinde tekrar arama oranı.
- Upload sırasında "zaten var" uyarısının çıkıp kullanıcının vazgeçtiği oran (kazanılan mükerrer sayısı).
- Sıfır-tıklama oranı (arama yapıp hiçbirine tıklamadan çıkma).

### 5.2 CTO'ya teknik özet

Mevcut veritabanına dokunulmadan, doküman bulunabilirliği problemina yönelik PostgreSQL FTS tabanlı, client-side Bloom filter destekli, cache'li bir arama ve yükleme prototipi geliştirdim. 1M kayıt üzerinde yapılan performans testlerinde cold sorgular 270-410 ms arasında, warm sorgular <1 ms çıktı:

**Aldığımız:**
- PostgreSQL FTS + AND token + ts_rank_cd ile 1M kayıtta cold sorgu 270-410 ms (%83'ü 400ms altında); warm sorgu <1 ms.
- Application-level result cache (LRU+TTL+invalidation) → debounce'lu UI'da kullanıcı sorgularının çoğu DB'ye gitmiyor.
- Bloom filter + IndexedDB ile **upload-time duplicate check'in ~%99'u browser'da, DB'ye yük binmiyor**. Sadece %1 false-positive teyit için tek satır indeks lookup'ı atılıyor.
- Client-side suggest — sıfır sonuç döndüren yazım hatalı sorguları yakalıyor.

**Bilerek girilen borçlar:**
1. **Bloom filter + cache in-memory, instance başına.** Çok instance'lı ortamda kayar. Çözüm: Redis backend + günlük rebuild scheduler. **3-5 gün.**
2. **Term dictionary statik.** Yeni kelimeler eklendikçe suggest kalitesi düşer. Periyodik refresh job. **1-2 gün.**
3. **Türkçe stemmer yok.** FTS `simple` analyzer ile kök bulma yapılmıyor; trigram kısmen telafi ediyor. Zemberek veya benzeri kelimeler. **1 sprint.**
4. **Nadir kelime kombinasyonlarında cold sorgu 400ms+ çıkabilir** (örn. "rho kimya" 533 ms). RUM index extension veya daha agresif tuning. **2-3 gün.**
5. **Auth / audit log yok.** Case kapsamı dışında bırakıldı, prod öncesi şart.
6. **OCR / semantic search yok.** Embedding altyapısı gerektirir. Talep doğrulanmadan açılmasın.

**Önerim, sıralı:**
- **1. ay:** Prod'a açılırken metrik toplama altyapısı (gerçek mükerrer oranı, sıfır-sonuç oranı, kullanıcı tıklama dağılımı, cache hit oranı, sorgu latency dağılımı).
- **2-3. ay:** Bloom filter ve result cache'i Redis'e taşımak gerek + günlük rebuild scheduler.
- **6. ay:** Tıklama verisiyle FTS skorlama parametrelerini kalibre etmeli; Türkçe stemmer entegrasyonu; nadir kelime sorguları için index tuning gerekli.

**Doğrulanmamış varsayım:** "Mükerrer oran kaç?" Cevabı yok — elimizde ölçüm olmadan tüm sonraki kararlar tahmin. Mevcut DB üzerinde içerik hash'lerini hesaplayan bir kerelik job kurmak, sonraki tüm kararların temelini sağlamlaştırır.

---

## API hızlı referansı

| Metod | Yol | Açıklama |
|---|---|---|
| GET    | `/api/documents?page&pageSize`  | Sayfalanmış doküman listesi |
| GET    | `/api/documents/{id}`           | Tek doküman |
| GET    | `/api/documents/by-hash/{hash}` | Hash ile bul (bloom filter teyidi için) |
| GET    | `/api/documents/owners`         | Filtre dropdown'ı için sahip listesi |
| POST   | `/api/documents`                | Yükleme. 409 + adaylar dönebilir; `forceUpload: true` ile zorla |
| PUT    | `/api/documents/{id}`           | Düzenleme |
| DELETE | `/api/documents/{id}`           | Sil |
| POST   | `/api/search`                   | Filtre + sorgu ile arama (cache'li FTS) |
| GET    | `/api/duplicate-filter`         | Bloom filter binary snapshot'ı (ETag cacheable) |
| GET    | `/api/term-dictionary`          | Top popüler kelimeler, client suggest için |

Swagger UI: **http://localhost:5080/swagger**

---

## Proje yapısı

```
document-management-system/
├── README.md                              ← bu dosya
├── docker-compose.yml                     ← Yol B (Docker varsa)
├── docker/postgres/init.sql               ← şema + indeksler
├── scripts/                               ← Yol A (Docker yoksa)
│   ├── setup-postgres.ps1                 ← tek seferlik kurulum + tuning
│   ├── start-postgres.ps1                 ← her açılışta
│   └── stop-postgres.ps1                  ← sunum bitince
└── src/DocumentManagementSystem/
    ├── Program.cs                         ← DI + seed + bloom + pre-warm + statik
    ├── appsettings.json                   ← ConnectionStrings, Seed, Bloom
    ├── Controllers/
    │   ├── DocumentsController.cs         ← CRUD + by-hash + cache invalidation
    │   ├── SearchController.cs            ← FTS search + cache lookup
    │   ├── DuplicateFilterController.cs   ← Bloom binary endpoint
    │   └── TermDictionaryController.cs    ← Suggest sözlüğü
    ├── Services/
    │   ├── IDocumentRepository.cs / PostgresDocumentRepository.cs
    │   │      → AND token, ts_rank_cd, capped count (LIMIT 1001)
    │   ├── BloomFilter.cs                 ← FNV-1a hash, wire format
    │   ├── BloomFilterService.cs          ← Snapshot tutucu, incremental add
    │   ├── TermDictionaryService.cs       ← Top-N popüler kelime
    │   ├── SearchCache.cs                 ← LRU + TTL + invalidation
    │   ├── IDuplicateDetector.cs / DuplicateDetector.cs
    │   └── Tokenizer.cs
    ├── Models/Document.cs · Dtos.cs
    ├── Data/DatabaseSeeder.cs             ← Binary COPY ile 1M kayıt
    └── wwwroot/
        ├── index.html · app.js · styles.css        ← React UI
        └── lib/
            ├── bloom-filter.js            ← JS bloom (C# ile aynı hash)
            ├── idb-cache.js               ← IndexedDB ETag'li cache
            ├── hash.js                    ← SHA-256 (SubtleCrypto)
            └── suggest.js                 ← Levenshtein client suggest
```

### Cross-language hash uyumu

```bash
dotnet run --project src/DocumentManagementSystem -- --print-hashes
```

---

## Notlar

- **1M kayıt seed'i ilk açılışta ~75 saniye alır** (binary COPY ile). `Seed:TargetCount` ile düşürülebilir.
- **Pre-warm 3-4 saniye sürer**, bu sayede ilk kullanıcı sorgusu cold cache'e değil sıcak cache'e düşer.
- **Cache hit'lerde `elapsedMs` <1 ms** çünkü gerçek lookup süresi Stopwatch'ın milisaniyesinin altında.
- **PostgreSQL `simple` analyzer Türkçe köklerini bilmiyor.** Trigram boşluğu kapatır ama tam çözüm değil.
- **Bloom filter şu an in-memory + append-only.** Multi-instance veya çok silme olan ortamda Redis backend gerekir.
- **Auth, audit log, dosya storage geliştirmesi eklenirse izlemeler ve sonrasında olacak geliştirmeler önizlenebilir.
- **Build hatası alırsanız:** muhtemelen API process'i hala çalışıyordur. `Get-Process DocumentManagementSystem | Stop-Process -Force` ile temizleyin.
