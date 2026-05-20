using DocumentManagementSystem.Services;
using Xunit;

namespace DocumentManagementSystem.Tests;

public class BloomFilterTests
{
    // ---- FNV-1a hash parity ----
    // Bu değerler wwwroot/lib/bloom-filter.js içindeki implementasyon ile
    // birebir uyuşmak zorundadır. Cross-language uyum bloom filter'in temelidir.
    // Referans değerler `dotnet run -- --print-hashes` ile üretildi.

    [Theory]
    [InlineData("hello", 0u, 0x4f9f2cabu)]
    [InlineData("hello", 1u, 0xb28dc714u)]
    [InlineData("hello", 2u, 0x6cfc027du)]
    [InlineData("Acme",  0u, 0xc629bdcfu)]
    [InlineData("ABCDEF0123456789", 0u, 0x5d87fc55u)]
    [InlineData("sözleşme", 0u, 0x4c43ccecu)]
    [InlineData("", 0u, 0x811c9dc5u)]
    public void Fnv1a_produces_expected_hash(string input, uint seed, uint expected)
    {
        Assert.Equal(expected, BloomFilter.Fnv1a(input, seed));
    }

    [Fact]
    public void Create_picks_optimal_m_and_k_for_expected_items()
    {
        // 1M items, %1 false positive rate → m ≈ 9.6M bits, k = 7
        var bf = BloomFilter.Create(expectedItems: 1_000_000, falsePositiveRate: 0.01);

        Assert.InRange(bf.M, 9_500_000, 9_700_000);
        Assert.Equal(7, bf.K);
    }

    [Fact]
    public void Added_items_are_always_reported_as_present()
    {
        var bf = BloomFilter.Create(1000, 0.01);
        var items = new[] { "alpha", "beta", "gamma", "delta", "üğşıöç" };

        foreach (var item in items) bf.Add(item);
        foreach (var item in items)
        {
            Assert.True(bf.MightContain(item), $"Added item '{item}' must be reported as present");
        }
    }

    [Fact]
    public void Unadded_items_are_mostly_reported_as_absent()
    {
        var bf = BloomFilter.Create(10_000, 0.01);
        for (var i = 0; i < 10_000; i++) bf.Add($"present-{i}");

        var falsePositives = 0;
        for (var i = 0; i < 10_000; i++)
        {
            if (bf.MightContain($"absent-{i}")) falsePositives++;
        }

        // %1 hedef false positive oranı için 10K denemede 200'den az olmalı (geniş tolerans).
        Assert.InRange(falsePositives, 0, 200);
    }

    [Fact]
    public void Serialize_then_deserialize_roundtrip_preserves_state()
    {
        var original = BloomFilter.Create(100, 0.01);
        original.Add("hello");
        original.Add("world");

        var serialized = original.Serialize();

        // Wire format: 4 byte m + 4 byte k + bits
        var m = BitConverter.ToInt32(serialized, 0);
        var k = BitConverter.ToInt32(serialized, 4);
        var bits = serialized[8..];

        var rebuilt = new BloomFilter(m, k, bits);

        Assert.True(rebuilt.MightContain("hello"));
        Assert.True(rebuilt.MightContain("world"));
        Assert.Equal(original.M, rebuilt.M);
        Assert.Equal(original.K, rebuilt.K);
    }
}
