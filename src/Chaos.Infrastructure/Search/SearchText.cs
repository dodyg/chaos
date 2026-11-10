using System.Globalization;
using System.Text;

namespace Chaos.Infrastructure.Search;

/// <summary>
/// Text normalization shared by the index writer and query parser (PLAN §5 FTS rules). FTS5
/// is lexical, so both sides fold the same way: compatibility normalization, combining marks
/// removed (Latin accents and Arabic harakat), the Arabic tatweel dropped, and Arabic-Indic
/// and Persian digits mapped to ASCII digits. Case folding is left to the unicode61 tokenizer.
/// </summary>
public static class SearchText
{
    /// <summary>Bump when normalization changes; startup then rebuilds the index.</summary>
    public const int NormalizationVersion = 1;

    private const char ArabicTatweel = 'ـ';

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            switch (character)
            {
                case ArabicTatweel:
                    continue;
                case >= '٠' and <= '٩':
                    builder.Append((char)('0' + (character - '٠')));
                    continue;
                case >= '۰' and <= '۹':
                    builder.Append((char)('0' + (character - '۰')));
                    continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString().Normalize(NormalizationForm.FormKC);
    }

    /// <summary>
    /// Turns free text into a safe FTS5 MATCH expression: the normalized input is split into
    /// letter/digit runs, each run becomes a quoted prefix term, and terms are ANDed. Quotes,
    /// operators and punctuation therefore can never form FTS syntax or crash a query.
    /// </summary>
    /// <returns>The expression, or null when the input has no searchable word.</returns>
    public static string? ToMatchExpression(string? text)
    {
        var terms = Terms(Normalize(text));
        return terms.Count == 0 ? null : string.Join(" AND ", terms.Select(term => $"\"{term}\"*"));
    }

    /// <summary>Letter/digit runs of already-normalized text (surrogate pairs kept whole).</summary>
    public static IReadOnlyList<string> Terms(string normalized)
    {
        var terms = new List<string>();
        var current = new StringBuilder();
        for (var index = 0; index < normalized.Length; index++)
        {
            var isPair = char.IsHighSurrogate(normalized[index]) && index + 1 < normalized.Length && char.IsLowSurrogate(normalized[index + 1]);
            var isWordCharacter = isPair
                ? char.IsLetterOrDigit(normalized, index)
                : char.IsLetterOrDigit(normalized[index]);
            if (isWordCharacter)
            {
                current.Append(normalized[index]);
                if (isPair)
                {
                    current.Append(normalized[++index]);
                }

                continue;
            }

            if (isPair)
            {
                index++;
            }

            Flush();
        }

        Flush();
        return terms;

        void Flush()
        {
            if (current.Length > 0)
            {
                terms.Add(current.ToString());
                current.Clear();
            }
        }
    }
}
