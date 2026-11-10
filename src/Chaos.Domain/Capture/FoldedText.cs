using System.Globalization;
using System.Text;

namespace Chaos.Domain.Capture;

/// <summary>
/// A comparison form of capture input (PLAN §8): lower-cased, diacritics and Arabic harakat
/// removed, tatweel dropped, Arabic-Indic and Persian digits folded to ASCII, and common
/// Arabic letter variants unified. Every folded character remembers the UTF-16 range of the
/// source it came from, so a match in the folded text maps back to exact source offsets.
/// Indices into the folded text are never returned to callers.
/// </summary>
public sealed class FoldedText
{
    private readonly int[] _sourceStart;
    private readonly int[] _sourceEnd;

    private FoldedText(string value, int[] sourceStart, int[] sourceEnd)
    {
        Value = value;
        _sourceStart = sourceStart;
        _sourceEnd = sourceEnd;
    }

    public string Value { get; }

    /// <summary>Folds the source; surrogate pairs stay one unit, so an emoji never splits.</summary>
    public static FoldedText Fold(string source)
    {
        var builder = new StringBuilder(source.Length);
        var starts = new List<int>(source.Length);
        var ends = new List<int>(source.Length);
        for (var index = 0; index < source.Length;)
        {
            var length = char.IsHighSurrogate(source[index]) && index + 1 < source.Length && char.IsLowSurrogate(source[index + 1]) ? 2 : 1;
            var folded = FoldUnit(source.AsSpan(index, length));
            if (folded.Length == 0 && ends.Count > 0)
            {
                ends[^1] = index + length; // a removed mark belongs to the character before it
            }

            foreach (var character in folded)
            {
                builder.Append(character);
                starts.Add(index);
                ends.Add(index + length);
            }

            index += length;
        }

        return new FoldedText(builder.ToString(), [.. starts], [.. ends]);
    }

    /// <summary>The comparison form alone, used to fold language-pack vocabulary the same way.</summary>
    public static string FoldWord(string text) => Fold(text).Value;

    /// <summary>Maps a folded range [start, start+length) to its source UTF-16 range.</summary>
    public (int Start, int End) ToSource(int start, int length) =>
        length <= 0 ? (_sourceStart[start], _sourceStart[start]) : (_sourceStart[start], _sourceEnd[start + length - 1]);

    private static string FoldUnit(ReadOnlySpan<char> unit)
    {
        if (unit.Length == 1)
        {
            var character = unit[0];
            switch (character)
            {
                case >= '٠' and <= '٩': return ((char)('0' + character - '٠')).ToString();
                case >= '۰' and <= '۹': return ((char)('0' + character - '۰')).ToString();
                case 'ـ': return string.Empty; // tatweel
                case 'ة': return "ه"; // taa marbuta → haa
                case 'ى': return "ي"; // alef maqsura → yaa
                case 'ٱ': return "ا"; // alef wasla → alef
                case '’' or '‘' or 'ʼ': return "'";
                case ' ' or ' ' or ' ': return " ";
            }
        }

        var decomposed = unit.ToString().Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
            {
                continue;
            }

            result.Append(character);
        }

        return result.ToString().ToLowerInvariant();
    }
}
