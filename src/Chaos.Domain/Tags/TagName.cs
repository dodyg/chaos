using System.Globalization;
using System.Text;
using Chaos.Domain.Validation;

namespace Chaos.Domain.Tags;

/// <summary>A validated tag segment: the spelling the person typed plus its identity key.</summary>
public readonly record struct TagSegment(string DisplayName, string NormalizedName);

/// <summary>
/// Tag naming rules (PLAN §6 Tags): identity uses Unicode normalization plus invariant case
/// folding while the display spelling is preserved. Distinct singular/plural names are never
/// merged silently; suggestions handle near matches. A slash-delimited path selects or
/// creates nested segments, stored as adjacency rather than a path string.
/// </summary>
public static class TagName
{
    public const int MaxUnicodeScalarValues = 64;

    /// <summary>Deepest nesting allowed, counting the root segment as level 1.</summary>
    public const int MaxDepth = 8;

    /// <summary>Validates one segment. A single leading '#' (quick-add style) is dropped.</summary>
    /// <exception cref="DomainValidationException">Blank, contains '/', or longer than the limit.</exception>
    public static TagSegment Segment(string? raw)
    {
        var display = (raw ?? string.Empty).Trim();
        if (display.StartsWith('#'))
        {
            display = display[1..].Trim();
        }

        if (display.Length == 0)
        {
            throw new DomainValidationException("A tag needs a name. Enter at least one character.");
        }

        if (display.Contains('/'))
        {
            throw new DomainValidationException("A tag name cannot contain '/'. Use a path such as home/garden to nest tags.");
        }

        if (display.Any(char.IsControl))
        {
            throw new DomainValidationException("A tag name cannot contain line breaks or control characters.");
        }

        var scalars = display.Length - display.Count(char.IsHighSurrogate);
        if (scalars > MaxUnicodeScalarValues)
        {
            throw new DomainValidationException(
                $"A tag name can hold at most {MaxUnicodeScalarValues} characters. The entered name has {scalars}.");
        }

        return new TagSegment(display, Normalize(display));
    }

    /// <summary>Splits "home/garden" into validated segments; rejects empty segments such as "home//garden".</summary>
    /// <exception cref="DomainValidationException">An empty segment, an invalid segment, or deeper than <see cref="MaxDepth"/>.</exception>
    public static IReadOnlyList<TagSegment> Path(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        var parts = text.Split('/');
        if (parts.Any(part => part.Trim().Length == 0))
        {
            throw new DomainValidationException(
                text.Length == 0
                    ? "A tag needs a name. Enter at least one character."
                    : "A tag path cannot have an empty part. Write nested tags like home/garden.");
        }

        if (parts.Length > MaxDepth)
        {
            throw new DomainValidationException($"Tags can be nested at most {MaxDepth} levels deep.");
        }

        return [.. parts.Select(Segment)];
    }

    /// <summary>The identity key: NFKC normalization, then invariant lower-case folding.</summary>
    public static string Normalize(string display) =>
        display.Normalize(NormalizationForm.FormKC).ToUpperInvariant().ToLowerInvariant();

    /// <summary>
    /// True when two distinct identity keys look like near duplicates (one is a prefix of the
    /// other, such as errand/errands). Used only to suggest an existing tag, never to merge.
    /// </summary>
    public static bool IsNearMatch(string normalizedInput, string normalizedExisting) =>
        normalizedInput.Length > 0
        && (normalizedExisting.StartsWith(normalizedInput, StringComparison.Ordinal)
            || (normalizedInput.Length >= 3 && normalizedInput.StartsWith(normalizedExisting, StringComparison.Ordinal))
            || normalizedExisting.Contains(normalizedInput, StringComparison.Ordinal));

    /// <summary>Culture-independent ordering key for sibling display.</summary>
    public static int Compare(string left, string right) =>
        string.Compare(left, right, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
}

/// <summary>
/// The fixed accessible tag palette (PLAN §6: colors use a fixed palette initially). Stored
/// as a short key; the renderer maps each key to light/dark tokens with sufficient contrast.
/// </summary>
public static class TagColors
{
    public static readonly IReadOnlyList<string> All =
        ["slate", "red", "orange", "amber", "green", "teal", "blue", "violet", "pink"];

    /// <returns>The validated key, or null for no color.</returns>
    /// <exception cref="DomainValidationException">A key outside the palette.</exception>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var key = raw.Trim().ToLowerInvariant();
        return All.Contains(key)
            ? key
            : throw new DomainValidationException($"Choose one of the tag colors: {string.Join(", ", All)}.");
    }
}
