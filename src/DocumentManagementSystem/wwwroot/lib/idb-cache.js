// IndexedDB üzerine ince bir cache wrapper'ı.
// Gmail tarzı "bu hesap alınmış mı?" anlık kontrolü için indirilen bloom filter
// ve term dictionary'yi tarayıcıda saklar. ETag ile fresh tutulur.
//
// Yaklaşım: tek object store, key-value şeklinde. Her kayıt { etag, blob } tutar.

(function (global) {
    const DB_NAME = 'dms-cache';
    const DB_VERSION = 1;
    const STORE = 'kv';

    function open() {
        return new Promise((resolve, reject) => {
            const req = indexedDB.open(DB_NAME, DB_VERSION);
            req.onupgradeneeded = () => {
                req.result.createObjectStore(STORE);
            };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }

    async function get(key) {
        const db = await open();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, 'readonly');
            const r = tx.objectStore(STORE).get(key);
            r.onsuccess = () => resolve(r.result ?? null);
            r.onerror = () => reject(r.error);
        });
    }

    async function put(key, value) {
        const db = await open();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, 'readwrite');
            tx.objectStore(STORE).put(value, key);
            tx.oncomplete = () => resolve();
            tx.onerror = () => reject(tx.error);
        });
    }

    /**
     * Conditional fetch: önce IDB'den varsa etag ile If-None-Match gönder,
     * 304 dönerse cache'i kullan; aksi takdirde indir + cache'i güncelle.
     *
     * @param {string} cacheKey IDB key
     * @param {string} url      endpoint
     * @param {string} mode     'binary' | 'json'
     * @returns parsed payload (ArrayBuffer veya JSON object)
     */
    async function fetchCached(cacheKey, url, mode = 'binary') {
        const cached = await get(cacheKey).catch(() => null);
        const headers = {};
        if (cached?.etag) headers['if-none-match'] = cached.etag;

        const res = await fetch(url, { headers });

        if (res.status === 304 && cached) {
            return cached.payload;
        }

        if (!res.ok) {
            throw new Error(`Cache fetch failed: ${res.status}`);
        }

        const etag = res.headers.get('etag') ?? null;
        const payload = mode === 'json' ? await res.json() : await res.arrayBuffer();

        // ArrayBuffer doğrudan IDB'ye yazılabilir. JSON da öyle.
        await put(cacheKey, { etag, payload, savedAt: Date.now() }).catch(err => {
            // IDB hatası uygulamanın çalışmasını engellemesin
            console.warn('IDB cache put failed', err);
        });

        return payload;
    }

    async function clearAll() {
        const db = await open();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE, 'readwrite');
            tx.objectStore(STORE).clear();
            tx.oncomplete = () => resolve();
            tx.onerror = () => reject(tx.error);
        });
    }

    global.IdbCache = { get, put, fetchCached, clearAll };
})(window);
