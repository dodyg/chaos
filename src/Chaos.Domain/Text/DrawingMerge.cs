using System.Text.Json;
using System.Text.Json.Nodes;

namespace Chaos.Domain.Text;

/// <summary>
/// Stroke-level union of two versions of one pad drawing (PLAN3 §9.9). A v2 drawing is
/// <c>{"v":2,"strokes":[{"id":…,"createdAt":ms,…}],"erased":[ids]}</c>. The merge is the union of strokes by ID, minus the
/// union of erased IDs, ordered by <c>(createdAt, id)</c>. It needs no base, is order independent and is idempotent, so two
/// devices that merge the same pair write the same text. A v1 drawing (no IDs) is read with the stroke at index
/// <c>i</c> given the ID <c>legacy-i</c> and <c>createdAt</c> 0, the same IDs the renderer assigns when it upgrades one.
/// </summary>
public static class DrawingMerge
{
    public const string LegacyPrefix = "legacy-";

    private sealed record Parsed(Dictionary<string, JsonObject> Strokes, HashSet<string> Erased);

    /// <summary>The merged drawing, or null when either side cannot be read (the caller keeps the later clock's value).</summary>
    public static string? Merge(string local, string remote)
    {
        if (Parse(local) is not { } left || Parse(remote) is not { } right) return null;
        return Write(Combine(left, right));
    }

    /// <summary>The canonical v2 text of one drawing, or null when it cannot be read.</summary>
    public static string? Canonical(string json) => Parse(json) is { } parsed ? Write(Combine(parsed, new Parsed([], []))) : null;

    private static Parsed Combine(Parsed left, Parsed right)
    {
        var erased = new HashSet<string>(left.Erased, StringComparer.Ordinal);
        erased.UnionWith(right.Erased);
        var strokes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var source in new[] { left.Strokes, right.Strokes })
            foreach (var (id, stroke) in source)
                if (!erased.Contains(id) && !strokes.ContainsKey(id)) strokes[id] = stroke;
        return new Parsed(strokes, erased);
    }

    private static string Write(Parsed drawing)
    {
        var ordered = drawing.Strokes
            .OrderBy(pair => CreatedAt(pair.Value))
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => (JsonNode?)pair.Value.DeepClone());
        var result = new JsonObject
        {
            ["v"] = 2,
            ["strokes"] = new JsonArray(ordered.ToArray()),
            ["erased"] = new JsonArray(drawing.Erased.Order(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        };
        return result.ToJsonString();
    }

    private static double CreatedAt(JsonObject stroke) =>
        stroke["createdAt"] is JsonValue value && value.TryGetValue<double>(out var at) ? at : 0;

    private static Parsed? Parse(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["strokes"] is not JsonArray list) return null;
            var version = root["v"] is JsonValue v && v.TryGetValue<int>(out var number) ? number : 0;
            if (version is not (1 or 2)) return null;
            var strokes = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            for (var index = 0; index < list.Count; index++)
            {
                if (list[index] is not JsonObject stroke) return null;
                var copy = (JsonObject)stroke.DeepClone();
                string id;
                if (version == 1 || copy["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var given) || string.IsNullOrEmpty(given))
                {
                    id = LegacyPrefix + index;
                    copy["id"] = id;
                    if (copy["createdAt"] is null) copy["createdAt"] = 0;
                }
                else id = given;
                strokes.TryAdd(id, copy);
            }

            var erased = new HashSet<string>(StringComparer.Ordinal);
            if (root["erased"] is JsonArray marks)
                foreach (var mark in marks)
                    if (mark is JsonValue item && item.TryGetValue<string>(out var text)) erased.Add(text);
            return new Parsed(strokes, erased);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
