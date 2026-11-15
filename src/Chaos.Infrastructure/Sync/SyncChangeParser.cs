using System.Text.Json;
using Chaos.Sync;

namespace Chaos.Infrastructure.Sync;

/// <summary>
/// Parses one outbox/wire change (PLAN2 §7.12 shape) into a <see cref="SyncChange"/>. Values are
/// JSON strings, numbers or nulls — exactly what SQLite stored on the sending device.
/// </summary>
public static class SyncChangeParser
{
    public static SyncChange Parse(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var changeId = RequireString(root, "changeId");
        var kind = RequireString(root, "kind");
        var key = RequireString(root, "key");
        var op = RequireString(root, "op") is "purge" ? SyncOp.Purge
            : root.GetProperty("op").GetString() == "upsert" ? SyncOp.Upsert
            : throw new FormatException($"A change for {kind} has an unknown op.");

        var groups = new Dictionary<string, SyncGroupState>(StringComparer.Ordinal);
        foreach (var property in root.GetProperty("groups").EnumerateObject())
        {
            var clockText = property.Value.GetProperty("hlc").GetString()
                ?? throw new FormatException($"A group of {kind} has no clock.");
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var value in property.Value.GetProperty("values").EnumerateObject())
            {
                values[value.Name] = value.Value.ValueKind switch
                {
                    JsonValueKind.String => value.Value.GetString(),
                    JsonValueKind.Number => value.Value.TryGetInt64(out var whole) ? whole : value.Value.GetDouble(),
                    JsonValueKind.True => 1L,
                    JsonValueKind.False => 0L,
                    _ => null,
                };
            }

            groups[property.Name] = new SyncGroupState(Hlc.Parse(clockText), new SyncGroupValues(values));
        }

        return new SyncChange(changeId, kind, key, op, groups);
    }

    private static string RequireString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new FormatException($"A change has no '{name}'.");
}
