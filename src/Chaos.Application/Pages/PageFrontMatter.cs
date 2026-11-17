namespace Chaos.Application.Pages;

/// <summary>
/// The front matter of a page (SPEC Pages): a YAML block between two <c>---</c> lines at the
/// very top. Only the small part of YAML pages need is read: <c>key: value</c> lines with plain,
/// quoted or <c>[flow, list]</c> values, block lists of <c>- item</c> lines, and comments. The
/// block is data and never run. When it cannot be read, <see cref="Error"/> says why and every
/// property is ignored until it is fixed; the text itself is never changed here.
/// </summary>
public sealed record PageFrontMatter(
    bool Present,
    string? Error,
    IReadOnlyList<PageProperty> Properties,
    int BodyStart)
{
    public static readonly PageFrontMatter None = new(false, null, [], 0);

    /// <summary>The value of a property read as one string, or null.</summary>
    public string? Scalar(string key) =>
        Error is null ? Properties.FirstOrDefault(property => string.Equals(property.Key, key, StringComparison.Ordinal))?.Values switch
        {
            [var single] => single,
            _ => null,
        } : null;

    /// <summary>The values of a list property (a single value counts as a list of one).</summary>
    public IReadOnlyList<string> List(string key) =>
        Error is null ? Properties.FirstOrDefault(property => string.Equals(property.Key, key, StringComparison.Ordinal))?.Values ?? [] : [];

    public string? Icon => Scalar("icon");
    public string? Background => Scalar("background");

    /// <summary>The attached image a page's <c>background:</c> names (<c>attachments/&lt;file name&gt;</c>), or null.</summary>
    public string? BackgroundAttachmentName => AttachmentBackgroundName(Background);

    /// <summary>
    /// The file name in an <c>attachments/&lt;name&gt;</c> value: one file name, no folders, drive
    /// letters, web addresses, parent paths or control characters. Mirrors <c>attachmentBackgroundName</c>
    /// in the front end; both are tested against the same table.
    /// </summary>
    public static string? AttachmentBackgroundName(string? value)
    {
        const string prefix = "attachments/";
        var text = (value ?? "").Trim();
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = text[prefix.Length..].Trim();
        if (name.Length == 0 || name.Length > 200 || name.Contains("..", StringComparison.Ordinal)
            || name.Any(ch => ch is '/' or '\\' or ':' || ch <= '\u001f') || name[^1] is '.' or ' ')
        {
            return null;
        }

        return name;
    }
    public IReadOnlyList<string> Tags => [.. List("tags").Select(tag => tag.TrimStart('#').Trim()).Where(tag => tag.Length > 0)];
    public IReadOnlyList<string> Aliases => [.. List("aliases").Select(alias => alias.Trim()).Where(alias => alias.Length > 0)];

    public static PageFrontMatter Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return None;
        }

        var lines = SplitLines(markdown);
        if (lines.Count == 0 || lines[0].Text.TrimEnd() != "---")
        {
            return None;
        }

        var close = -1;
        for (var index = 1; index < lines.Count; index++)
        {
            var trimmed = lines[index].Text.TrimEnd();
            if (trimmed is "---" or "...")
            {
                close = index;
                break;
            }
        }

        if (close < 0)
        {
            return new PageFrontMatter(true, "The front matter has no closing --- line.", [], 0);
        }

        var bodyStart = lines[close].End;
        var properties = new List<PageProperty>();
        PageProperty? openList = null;
        for (var index = 1; index < close; index++)
        {
            var line = lines[index].Text.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed == "-")
            {
                if (openList is null)
                {
                    return Failed(index, "A list item has no key above it.", bodyStart);
                }

                string? itemError = null;
                var item = trimmed.Length == 1 ? string.Empty : Unquote(trimmed[2..].Trim(), out itemError);
                if (itemError is not null)
                {
                    return Failed(index, itemError, bodyStart);
                }

                openList = openList with { Values = [.. openList.Values, item] };
                properties[^1] = openList;
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
            {
                return Failed(index, "Nested values are not supported. Use key: value or a list.", bodyStart);
            }

            var colon = FindKeyColon(line);
            if (colon <= 0)
            {
                return Failed(index, "Expected key: value.", bodyStart);
            }

            var key = line[..colon].Trim();
            if (key.StartsWith('"') || key.StartsWith('\''))
            {
                key = Unquote(key, out _);
            }

            if (properties.Any(property => property.Key == key))
            {
                return Failed(index, $"The key \"{key}\" appears twice.", bodyStart);
            }

            var raw = line[(colon + 1)..].Trim();
            var comment = CommentStart(raw);
            if (comment >= 0)
            {
                raw = raw[..comment].TrimEnd();
            }

            IReadOnlyList<string> values;
            if (raw.Length == 0)
            {
                values = [];
            }
            else if (raw.StartsWith('['))
            {
                if (!raw.EndsWith(']'))
                {
                    return Failed(index, "A [list] is not closed.", bodyStart);
                }

                var parsed = new List<string>();
                foreach (var part in SplitFlow(raw[1..^1]))
                {
                    var value = Unquote(part.Trim(), out var partError);
                    if (partError is not null)
                    {
                        return Failed(index, partError, bodyStart);
                    }

                    if (value.Length > 0 || part.Trim().Length > 0)
                    {
                        parsed.Add(value);
                    }
                }

                values = parsed;
            }
            else if (raw.StartsWith('{'))
            {
                return Failed(index, "Nested values are not supported. Use key: value or a list.", bodyStart);
            }
            else
            {
                var value = Unquote(raw, out var valueError);
                if (valueError is not null)
                {
                    return Failed(index, valueError, bodyStart);
                }

                values = [value];
            }

            var property = new PageProperty(key, values, raw);
            properties.Add(property);
            openList = raw.Length == 0 ? property : null;
        }

        return new PageFrontMatter(true, null, properties, bodyStart);
    }

    /// <summary>The page text after the front matter (all of it when there is none or it cannot be read).</summary>
    public static string Body(string markdown)
    {
        var parsed = Parse(markdown);
        return parsed.Present && parsed.Error is null ? markdown[parsed.BodyStart..] : markdown;
    }

    private static PageFrontMatter Failed(int lineIndex, string message, int bodyStart) =>
        new(true, $"Line {lineIndex + 1}: {message}", [], bodyStart);

    private static int FindKeyColon(string line)
    {
        var quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'' && index == 0)
            {
                quote = character;
                continue;
            }

            if (character == ':' && (index + 1 == line.Length || line[index + 1] == ' ' || line[index + 1] == '\t'))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>Where a trailing <c># comment</c> starts outside quotes, or -1.</summary>
    private static int CommentStart(string value)
    {
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '#' && index > 0 && char.IsWhiteSpace(value[index - 1]))
            {
                return index;
            }
        }

        return -1;
    }

    private static IEnumerable<string> SplitFlow(string inner)
    {
        var start = 0;
        var quote = '\0';
        for (var index = 0; index < inner.Length; index++)
        {
            var character = inner[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == ',')
            {
                yield return inner[start..index];
                start = index + 1;
            }
        }

        yield return inner[start..];
    }

    private static string Unquote(string value, out string? error)
    {
        error = null;
        if (value.Length >= 1 && (value[0] == '"' || value[0] == '\''))
        {
            var quote = value[0];
            if (value.Length < 2 || value[^1] != quote)
            {
                error = "A quoted value is not closed.";
                return value;
            }

            var inner = value[1..^1];
            return quote == '"' ? inner.Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal) : inner.Replace("''", "'", StringComparison.Ordinal);
        }

        return value;
    }

    private static List<(string Text, int End)> SplitLines(string markdown)
    {
        var lines = new List<(string, int)>();
        var start = 0;
        while (start < markdown.Length)
        {
            var newline = markdown.IndexOf('\n', start);
            var end = newline < 0 ? markdown.Length : newline + 1;
            var text = markdown[start..(newline < 0 ? markdown.Length : newline)].TrimEnd('\r');
            lines.Add((text, end));
            start = end;
        }

        return lines;
    }
}

/// <summary>One front matter key with its values (one for a scalar) and its raw text after the colon.</summary>
public sealed record PageProperty(string Key, IReadOnlyList<string> Values, string Raw);
