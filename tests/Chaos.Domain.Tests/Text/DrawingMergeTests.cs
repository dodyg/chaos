using Chaos.Domain.Text;

namespace Chaos.Domain.Tests.Text;

public sealed class DrawingMergeTests
{
    private static string Stroke(string id, double createdAt) =>
        $"{{\"id\":\"{id}\",\"createdAt\":{createdAt},\"tool\":\"pen\",\"color\":\"#111827\",\"size\":4,\"points\":[[0,0,0.5]]}}";

    private static string Drawing(string[] strokes, params string[] erased) =>
        $"{{\"v\":2,\"strokes\":[{string.Join(",", strokes)}],\"erased\":[{string.Join(",", erased.Select(id => $"\"{id}\""))}]}}";

    [Test]
    public async Task Merge_UnitesStrokesById_AndOrdersThemByCreationThenId()
    {
        var a = Drawing([Stroke("b", 5), Stroke("a", 5)]);
        var b = Drawing([Stroke("c", 1), Stroke("a", 5)]);
        var merged = DrawingMerge.Merge(a, b)!;
        var order = new[] { "c", "a", "b" }.Select(id => merged.IndexOf($"\"id\":\"{id}\"", StringComparison.Ordinal)).ToArray();
        await Assert.That(order.All(index => index >= 0)).IsTrue();
        await Assert.That(order[0] < order[1] && order[1] < order[2]).IsTrue();
    }

    [Test]
    public async Task Merge_IsCommutativeIdempotentAndAppliesErasures()
    {
        var a = Drawing([Stroke("a", 1), Stroke("x", 2)]);
        var b = Drawing([Stroke("b", 3)], "x");
        var merged = DrawingMerge.Merge(a, b)!;
        await Assert.That(DrawingMerge.Merge(b, a)).IsEqualTo(merged);
        await Assert.That(DrawingMerge.Merge(merged, merged)).IsEqualTo(merged);
        await Assert.That(DrawingMerge.Merge(merged, a)).IsEqualTo(merged);
        await Assert.That(merged).DoesNotContain("\"id\":\"x\"").And.Contains("\"erased\":[\"x\"]");
    }

    [Test]
    public async Task Merge_GivesALegacyDrawingTheIdsTheRendererAssigns()
    {
        var v1 = "{\"v\":1,\"strokes\":[{\"tool\":\"pen\",\"color\":\"#111827\",\"size\":4,\"points\":[[0,0,0.5]]},{\"tool\":\"pen\",\"color\":\"#111827\",\"size\":4,\"points\":[[1,1,0.5]]}]}";
        var upgraded = DrawingMerge.Canonical(v1)!;
        await Assert.That(upgraded).Contains("\"id\":\"legacy-0\"").And.Contains("\"id\":\"legacy-1\"");
        // The same drawing after the renderer erased its second stroke: the erasure wins over the legacy copy.
        var erasedLater = Drawing([Stroke("legacy-0", 0)], "legacy-1");
        var merged = DrawingMerge.Merge(v1, erasedLater)!;
        await Assert.That(merged).DoesNotContain("\"id\":\"legacy-1\"").And.Contains("\"id\":\"legacy-0\"");
    }

    [Test]
    public async Task Merge_ReturnsNull_ForUnreadableDrawings()
    {
        await Assert.That(DrawingMerge.Merge("nope", Drawing([]))).IsNull();
        await Assert.That(DrawingMerge.Merge("{\"v\":3,\"strokes\":[]}", Drawing([]))).IsNull();
    }
}
