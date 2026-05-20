namespace DocumentManagementSystem.Services;

/// <summary>
/// Klasik bit-array bloom filter.
///   - "Var" cevabı belirsizdir (false positive olabilir).
///   - "Yok" cevabı kesindir.
/// Bu özellik bizim için tam istediğimiz: client'ta "yok" derse server'a bile gitmeyiz;
/// "var" derse server'a teyit gönderirken sadece bu nadir durumda DB'ye gitmiş oluruz.
///
/// Hash fonksiyonu (FNV-1a) JS tarafıyla birebir aynıdır — wwwroot/lib/bloom-filter.js
/// içinde aynı algoritma satır satır var.
/// </summary>
public class BloomFilter
{
    public int M { get; }
    public int K { get; }
    private readonly byte[] _bits;

    public BloomFilter(int m, int k)
    {
        if (m < 8) m = 8;
        M = m;
        K = k;
        _bits = new byte[(M + 7) / 8];
    }

    public BloomFilter(int m, int k, byte[] bits)
    {
        M = m;
        K = k;
        _bits = bits;
    }

    /// <summary>
    /// Verilen item sayısı ve hedef false-positive oranı için optimum m ve k'yı seçer.
    /// </summary>
    public static BloomFilter Create(int expectedItems, double falsePositiveRate)
    {
        if (expectedItems < 1) expectedItems = 1;
        var m = (int)Math.Ceiling(-(expectedItems * Math.Log(falsePositiveRate)) / (Math.Log(2) * Math.Log(2)));
        var k = Math.Max(1, (int)Math.Round((double)m / expectedItems * Math.Log(2)));
        return new BloomFilter(m, k);
    }

    public void Add(string item)
    {
        for (int i = 0; i < K; i++) SetBit((int)(Fnv1a(item, (uint)i) % (uint)M));
    }

    public bool MightContain(string item)
    {
        for (int i = 0; i < K; i++)
            if (!GetBit((int)(Fnv1a(item, (uint)i) % (uint)M))) return false;
        return true;
    }

    public byte[] ToByteArray() => _bits;

    /// <summary>
    /// Wire format: 4 byte int32 LE: M, 4 byte int32 LE: K, ardından bit array.
    /// </summary>
    public byte[] Serialize()
    {
        var output = new byte[8 + _bits.Length];
        BitConverter.GetBytes(M).CopyTo(output, 0);
        BitConverter.GetBytes(K).CopyTo(output, 4);
        _bits.CopyTo(output, 8);
        return output;
    }

    // FNV-1a 32-bit, seed XOR ile k farklı varyant.
    // JS tarafıyla birebir aynı: Math.imul, charCodeAt(i), >>> 0 ile maskeleme.
    public static uint Fnv1a(string s, uint seed)
    {
        uint hash = 2166136261u ^ seed;
        for (int i = 0; i < s.Length; i++)
        {
            hash ^= s[i];               // UTF-16 code unit (JS charCodeAt ile aynı)
            hash *= 16777619u;          // overflow doğal, uint
        }
        return hash;
    }

    private void SetBit(int idx)    => _bits[idx >> 3] |= (byte)(1 << (idx & 7));
    private bool GetBit(int idx)    => (_bits[idx >> 3] & (1 << (idx & 7))) != 0;
}
