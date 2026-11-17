using Chaos.Application.Abstractions;
using System.Text;
using System.Text.Json;
using Chaos.Application.Contracts;

namespace Chaos.Application.Pad;

/// <summary>
/// Checks what the drawing canvas sends before it is stored (PLAN2 P10). The stored form is
/// <c>{"v":1,"strokes":[{"tool":"pen","color":"#112233","size":4,"points":[[x,y,pressure],...]}]}</c>.
/// Only the shape and the size limits are enforced here; the renderer owns how strokes look.
/// </summary>
public static class PadDrawingValidation
{
    public const int MaxStrokesBytes = 4 * 1024 * 1024;
    public const int MaxStrokes = 20_000;
    public const int MaxPoints = 1_000_000;
    public const int MinSide = 1;
    public const int MaxSide = 20_000;
    public const int DefaultWidth = 1600;
    public const int DefaultHeight = 1200;
    public const string EmptyStrokes = "{\"v\":1,\"strokes\":[]}";

    public static int Side(int value, string field)
    {
        if (value is < MinSide or > MaxSide)
        {
            throw AppException.Validation(field, $"The drawing {field} must be between {MinSide} and {MaxSide}.");
        }

        return value;
    }

    public static string Strokes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw AppException.Validation("strokes", "The drawing has no strokes data.");
        }

        if (Encoding.UTF8.GetByteCount(json) > MaxStrokesBytes)
        {
            throw AppException.Validation("strokes", "This drawing is too large to save. Erase some strokes or start a new drawing.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw AppException.Validation("strokes", "The drawing data is not valid.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("strokes", out var strokes) || strokes.ValueKind != JsonValueKind.Array)
            {
                throw AppException.Validation("strokes", "The drawing data is not in a format Chaos understands.");
            }

            var count = 0;
            var points = 0L;
            foreach (var stroke in strokes.EnumerateArray())
            {
                if (++count > MaxStrokes)
                {
                    throw AppException.Validation("strokes", "This drawing has too many strokes to save.");
                }

                if (stroke.ValueKind != JsonValueKind.Object || !stroke.TryGetProperty("points", out var list) || list.ValueKind != JsonValueKind.Array)
                {
                    throw AppException.Validation("strokes", "A stroke in the drawing is malformed.");
                }

                points += list.GetArrayLength();
                if (points > MaxPoints)
                {
                    throw AppException.Validation("strokes", "This drawing has too many points to save.");
                }
            }
        }

        return json;
    }

    /// <summary>Validates a region against the drawing's size and returns its stored JSON.</summary>
    public static string Region(PadRegion region, int width, int height)
    {
        if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.W) || !double.IsFinite(region.H)
            || region.W <= 0 || region.H <= 0 || region.X < 0 || region.Y < 0
            || region.X + region.W > width + 0.5 || region.Y + region.H > height + 0.5)
        {
            throw AppException.Validation("region", "Select a part of the drawing first.");
        }

        return JsonSerializer.Serialize(new { x = region.X, y = region.Y, w = region.W, h = region.H });
    }

    public static PadRegion? ParseRegion(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new PadRegion(root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble(), root.GetProperty("w").GetDouble(), root.GetProperty("h").GetDouble());
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
