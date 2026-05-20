using System.Globalization;
using System.Text;

namespace DocumentManagementSystem.Services;

public static class Tokenizer
{
    // Stop-words that add noise to short titles. Kept intentionally small —
    // a bigger stop list trades recall for precision and we'd want metrics first.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ve", "ile", "için", "bir", "the", "a", "an", "of", "and", "or", "to", "in", "on"
    };

    public static IEnumerable<string> Tokenize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) yield break;

        var normalized = RemoveDiacritics(input.ToLowerInvariant());
        var sb = new StringBuilder();

        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0)
            {
                var token = sb.ToString();
                sb.Clear();
                if (token.Length >= 2 && !StopWords.Contains(token))
                    yield return token;
            }
        }

        if (sb.Length > 0)
        {
            var token = sb.ToString();
            if (token.Length >= 2 && !StopWords.Contains(token))
                yield return token;
        }
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
