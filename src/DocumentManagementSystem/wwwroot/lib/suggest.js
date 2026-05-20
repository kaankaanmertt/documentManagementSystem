// Client-side "did you mean" — server'a gitmeden çalışır.
// TermDictionary servisinin gönderdiği popular term listesi üzerinden
// Levenshtein 2 mesafesindeki en yakın kelimeleri bulur.

(function (global) {
    /** Bounded Levenshtein (early exit). */
    function lev(a, b, maxDistance) {
        if (Math.abs(a.length - b.length) > maxDistance) return maxDistance + 1;
        const m = a.length, n = b.length;
        if (m === 0) return n;
        if (n === 0) return m;
        let prev = new Array(n + 1);
        let curr = new Array(n + 1);
        for (let j = 0; j <= n; j++) prev[j] = j;
        for (let i = 1; i <= m; i++) {
            curr[0] = i;
            let rowMin = i;
            for (let j = 1; j <= n; j++) {
                const cost = a[i - 1] === b[j - 1] ? 0 : 1;
                curr[j] = Math.min(curr[j - 1] + 1, prev[j] + 1, prev[j - 1] + cost);
                if (curr[j] < rowMin) rowMin = curr[j];
            }
            if (rowMin > maxDistance) return maxDistance + 1;
            [prev, curr] = [curr, prev];
        }
        return prev[n];
    }

    class Suggester {
        constructor(terms) {
            this.terms = terms || [];
        }

        suggest(query, max = 5) {
            const q = (query || '').toLowerCase().trim();
            if (q.length < 2) return [];

            // Önce prefix eşleşmeleri (daha güçlü sinyal)
            const prefix = [];
            for (const t of this.terms) {
                if (t.startsWith(q) && t !== q) prefix.push(t);
                if (prefix.length >= max) break;
            }
            if (prefix.length >= max) return prefix.slice(0, max);

            // Sonra edit-distance ≤ 2 olanlar (yazım hatası)
            const fuzzy = [];
            const maxLen = q.length + 2;
            for (const t of this.terms) {
                if (Math.abs(t.length - q.length) > 2) continue;
                if (t === q || prefix.includes(t)) continue;
                const d = lev(q, t, 2);
                if (d <= 2) fuzzy.push({ t, d });
                if (prefix.length + fuzzy.length >= max * 3) break; // limit scan
            }
            fuzzy.sort((a, b) => a.d - b.d || a.t.length - b.t.length);
            return [...prefix, ...fuzzy.map(x => x.t)].slice(0, max);
        }
    }

    global.Suggester = Suggester;
})(window);
