using System.Text;
using System.Text.Json;

namespace Chaos.Sync;

/// <summary>
/// The wire and storage JSON of changes and field groups (PLAN2 §7.12): <c>{changeId, kind, key, op,
/// groups: {name: {hlc, values: {column: value}}}}</c>. Values are JSON strings, numbers or nulls,
/// exactly as SQLite stored them; the service never interprets them. The desktop outbox, the wire
/// and the service's jsonb columns all use this one shape.
/// </summary>
public static class SyncJson
{
    public static SyncChange ParseChange(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return ParseChange(document.RootElement);
    }

    public static SyncChange ParseChange(JsonElement root)
    {
        var changeId = RequireString(root, "changeId");
        var kind = RequireString(root, "kind");
        var key = RequireString(root, "key");
        var op = RequireString(root, "op") switch
        {
            "purge" => SyncOp.Purge,
            "upsert" => SyncOp.Upsert,
            _ => throw new FormatException($"A change for {kind} has an unknown op."),
        };

        var groups = root.TryGetProperty("groups", out var groupsElement)
            ? ParseGroups(groupsElement, kind)
            : new Dictionary<string, SyncGroupState>(StringComparer.Ordinal);
        return new SyncChange(changeId, kind, key, op, groups);
    }

    public static Dictionary<string, SyncGroupState> ReadGroups(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ParseGroups(document.RootElement, "entity");
    }

    public static string WriteGroups(IReadOnlyDictionary<string, SyncGroupState> groups)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteGroups(writer, groups);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static void WriteGroups(Utf8JsonWriter writer, IReadOnlyDictionary<string, SyncGroupState> groups)
    {
        writer.WriteStartObject();
        foreach (var (name, group) in groups.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteStartObject(name);
            writer.WriteString("hlc", group.Hlc.Encode());
            writer.WriteStartObject("values");
            foreach (var (column, value) in group.Values.Values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(column);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    /// <summary>Writes one change, with the service's sequence number when it came from the change log.</summary>
    public static void WriteChange(Utf8JsonWriter writer, string changeId, string kind, string key, SyncOp op, IReadOnlyDictionary<string, SyncGroupState> groups, long? seq = null)
    {
        writer.WriteStartObject();
        writer.WriteString("changeId", changeId);
        writer.WriteString("kind", kind);
        writer.WriteString("key", key);
        writer.WriteString("op", op == SyncOp.Purge ? "purge" : "upsert");
        if (seq is { } sequence)
        {
            writer.WriteNumber("seq", sequence);
        }

        writer.WritePropertyName("groups");
        WriteGroups(writer, groups);
        writer.WriteEndObject();
    }

    private static Dictionary<string, SyncGroupState> ParseGroups(JsonElement element, string owner)
    {
        var groups = new Dictionary<string, SyncGroupState>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"The groups of {owner} are not an object.");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object
                || !property.Value.TryGetProperty("hlc", out var clock) || clock.ValueKind != JsonValueKind.String
                || !property.Value.TryGetProperty("values", out var valuesElement) || valuesElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"A group of {owner} is malformed.");
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var value in valuesElement.EnumerateObject())
            {
                values[value.Name] = value.Value.ValueKind switch
                {
                    JsonValueKind.String => value.Value.GetString(),
                    JsonValueKind.Number => value.Value.TryGetInt64(out var whole) ? (object)whole : value.Value.GetDouble(),
                    JsonValueKind.True => 1L,
                    JsonValueKind.False => 0L,
                    JsonValueKind.Null => null,
                    _ => throw new FormatException($"A value in {owner} is neither text, a number nor null."),
                };
            }

            groups[property.Name] = new SyncGroupState(Hlc.Parse(clock.GetString()!), new SyncGroupValues(values));
        }

        return groups;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case long whole: writer.WriteNumberValue(whole); break;
            case int whole: writer.WriteNumberValue(whole); break;
            case double real: writer.WriteNumberValue(real); break;
            case bool flag: writer.WriteNumberValue(flag ? 1 : 0); break;
            default: throw new FormatException("A value is neither text, a number nor null.");
        }
    }

    private static string RequireString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"A change has no '{name}'.");
}
