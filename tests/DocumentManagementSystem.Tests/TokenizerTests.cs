using DocumentManagementSystem.Services;
using Xunit;

namespace DocumentManagementSystem.Tests;

public class TokenizerTests
{
    [Fact]
    public void Tokenize_splits_on_punctuation_and_lowercases()
    {
        var tokens = Tokenizer.Tokenize("Hello, World! Foo-Bar").ToList();
        Assert.Equal(new[] { "hello", "world", "foo", "bar" }, tokens);
    }

    [Fact]
    public void Tokenize_strips_turkish_diacritics()
    {
        // 'Sözleşme' → 'sozlesme' (kullanıcı 'sozesme' yazarsa suggest çalışsın diye)
        var tokens = Tokenizer.Tokenize("Sözleşme").ToList();
        Assert.Contains("sozlesme", tokens);
    }

    [Fact]
    public void Tokenize_drops_short_and_stopwords()
    {
        // "ve", "ile" stop-word; tek karakter atlanır
        var tokens = Tokenizer.Tokenize("a ve b ile sözleşme").ToList();
        Assert.DoesNotContain("ve", tokens);
        Assert.DoesNotContain("ile", tokens);
        Assert.DoesNotContain("a", tokens);
        Assert.Contains("sozlesme", tokens);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Tokenize_returns_empty_for_blank_input(string? input)
    {
        Assert.Empty(Tokenizer.Tokenize(input));
    }

    [Fact]
    public void Tokenize_keeps_digits()
    {
        var tokens = Tokenizer.Tokenize("Fatura 2025 Q1").ToList();
        Assert.Contains("fatura", tokens);
        Assert.Contains("2025", tokens);
        Assert.Contains("q1", tokens);
    }
}
