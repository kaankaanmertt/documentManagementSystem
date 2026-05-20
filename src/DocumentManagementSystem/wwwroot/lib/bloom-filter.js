// Bloom filter — C# tarafıyla birebir aynı algoritma.
// Wire format: [int32 m | int32 k | bit array], little-endian.
// Hash: FNV-1a 32-bit, seed XOR ile k farklı varyant.

(function (global) {
    class BloomFilter {
        constructor(m, k, bits) {
            this.m = m;
            this.k = k;
            this.bits = bits; // Uint8Array
        }

        static fromArrayBuffer(buf) {
            const view = new DataView(buf);
            const m = view.getInt32(0, /* littleEndian */ true);
            const k = view.getInt32(4, true);
            const bits = new Uint8Array(buf, 8);
            return new BloomFilter(m, k, bits);
        }

        mightContain(item) {
            const s = String(item);
            for (let i = 0; i < this.k; i++) {
                // C#'taki "(uint)(Fnv1a(item, (uint)i) % (uint)M)" karşılığı.
                // (h >>> 0) ile uint sayıyı al, sonra modulo
                const h = (BloomFilter.fnv1a(s, i) >>> 0) % (this.m >>> 0);
                const byte = h >>> 3;
                const bit = h & 7;
                if (!(this.bits[byte] & (1 << bit))) return false;
            }
            return true;
        }

        // FNV-1a 32-bit. C# tarafı ile birebir uyumlu (UTF-16 code units).
        // Math.imul ile 32-bit signed çarpma, sonra >>> 0 ile unsigned'a çevir.
        static fnv1a(str, seed) {
            let h = ((2166136261 >>> 0) ^ (seed >>> 0)) >>> 0;
            for (let i = 0; i < str.length; i++) {
                h = (h ^ str.charCodeAt(i)) >>> 0;
                h = Math.imul(h, 16777619) >>> 0;
            }
            return h >>> 0;
        }
    }

    global.BloomFilter = BloomFilter;
})(window);
