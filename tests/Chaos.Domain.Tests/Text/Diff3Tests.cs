using Chaos.Domain.Text;

namespace Chaos.Domain.Tests.Text;

public sealed class Diff3Tests
{
    private const string Conflict = "<<<<<<< yours\nleft\n=======\nright\n>>>>>>> remote\n";

    public static IEnumerable<(string Name, string Base, string Local, string Remote, string Expected)> Fixtures()
    {
        yield return ("empty", "", "", "", "");
        yield return ("identity", "a\n", "a\n", "a\n", "a\n");
        yield return ("local only", "a\n", "left\n", "a\n", "left\n");
        yield return ("remote only", "a\n", "a\n", "right\n", "right\n");
        yield return ("same edit", "a\n", "b\n", "b\n", "b\n");
        yield return ("different lines", "a\nb\nc\n", "A\nb\nc\n", "a\nb\nC\n", "A\nb\nC\n");
        yield return ("adjacent lines", "a\nb\n", "A\nb\n", "a\nB\n", "A\nB\n");
        yield return ("same line conflict", "a\n", "left\n", "right\n", Conflict);
        yield return ("insert conflict", "a\n", "left\na\n", "right\na\n", Conflict + "a\n");
        yield return ("insert different positions", "a\nb\n", "x\na\nb\n", "a\nb\ny\n", "x\na\nb\ny\n");
        yield return ("delete and edit", "a\nb\nc\n", "b\nc\n", "a\nb\nC\n", "b\nC\n");
        yield return ("delete same", "a\nb\n", "b\n", "b\n", "b\n");
        yield return ("trailing newline", "a\nb\n", "A\nb\n", "a\nB\n", "A\nB\n");
        yield return ("no trailing newline", "a\nb", "A\nb", "a\nB", "A\nB");
        yield return ("CRLF", "a\r\nb\r\n", "A\r\nb\r\n", "a\r\nB\r\n", "A\r\nB\r\n");
        yield return ("RTL", "أ\nب\n", "أول\nب\n", "أ\nثان\n", "أول\nثان\n");
        yield return ("front matter", "---\ntitle: old\n---\nbody\n", "---\ntitle: new\n---\nbody\n", "---\ntitle: old\n---\nbody changed\n", "---\ntitle: new\n---\nbody changed\n");
        yield return ("empty base", "", "left\n", "right\n", Conflict);
        yield return ("same insert", "a\n", "x\na\n", "x\na\n", "x\na\n");
        yield return ("edit at end", "a\nb\n", "A\nb\n", "a\nb\nx\n", "A\nb\nx\n");
    }

    [Test]
    [MethodDataSource(nameof(Fixtures))]
    public async Task Merge_MatchesFixture(string name, string @base, string local, string remote, string expected)
    {
        await Assert.That(Diff3.Merge(@base, local, remote)).IsEqualTo(expected).Because(name);
    }

    [Test]
    public async Task Merge_WithItself_IsIdentity_AndOneSidedChangeWins()
    {
        var random = new Random(11);
        for (var round = 0; round < 200; round++)
        {
            var text = RandomText(random);
            var changed = Mutate(random, text);
            await Assert.That(Diff3.Merge(text, text, text)).IsEqualTo(text);
            await Assert.That(Diff3.Merge(text, changed, text)).IsEqualTo(changed);
            await Assert.That(Diff3.Merge(text, text, changed)).IsEqualTo(changed);
            await Assert.That(Diff3.Merge(text, changed, changed)).IsEqualTo(changed);
        }
    }

    [Test]
    public async Task Merge_IsOrderIndependent_WhenCleanAndKeepsTheSameLinesWhenNot()
    {
        var random = new Random(23);
        for (var round = 0; round < 300; round++)
        {
            var text = RandomText(random);
            var a = Mutate(random, text);
            var b = Mutate(random, text);
            var forward = Diff3.Merge(text, a, b, "A", "B");
            var backward = Diff3.Merge(text, b, a, "B", "A");
            if (!Diff3.HasConflictMarkers(forward))
            {
                await Assert.That(backward).IsEqualTo(forward);
                continue;
            }

            // A conflict lists the sides in the order the caller gave; the lines themselves are the same.
            await Assert.That(Diff3.HasConflictMarkers(backward)).IsTrue();
            await Assert.That(string.Join("|", forward.Split((char)10).Order(StringComparer.Ordinal)))
                .IsEqualTo(string.Join("|", backward.Split((char)10).Order(StringComparer.Ordinal)).Replace("<<<<<<< B", "<<<<<<< A").Replace(">>>>>>> A", ">>>>>>> B"));
        }
    }

    [Test]
    public async Task Merge_KeepsEveryDisjointEdit_WhenTheyDoNotOverlap()
    {
        var @base = string.Concat(Enumerable.Range(0, 40).Select(n => $"line {n}\n"));
        var a = @base.Replace("line 3\n", "A3\n");
        var b = @base.Replace("line 30\n", "B30\n");
        var merged = Diff3.Merge(@base, a, b);
        await Assert.That(merged).Contains("A3\n").And.Contains("B30\n");
        await Assert.That(Diff3.HasConflictMarkers(merged)).IsFalse();
        await Assert.That(Diff3.Merge(@base, b, a)).IsEqualTo(merged);
    }

    [Test]
    public async Task Merge_OfAnOversizeComparison_ConflictsInsteadOfThrowing()
    {
        var @base = string.Concat(Enumerable.Range(0, 3000).Select(n => $"base {n}\n"));
        var a = string.Concat(Enumerable.Range(0, 3000).Select(n => $"left {n}\n"));
        var b = string.Concat(Enumerable.Range(0, 3000).Select(n => $"right {n}\n"));
        var merged = Diff3.Merge(@base, a, b);
        await Assert.That(Diff3.HasConflictMarkers(merged)).IsTrue();
        await Assert.That(merged).Contains("left 0\n").And.Contains("right 2999\n");
    }

    [Test]
    public async Task HasConflictMarkers_FindsMarkerLines_Only()
    {
        await Assert.That(Diff3.HasConflictMarkers("a\n<<<<<<< Ada\nx\n")).IsTrue();
        await Assert.That(Diff3.HasConflictMarkers("<<<<<<< Ada\nx\n")).IsTrue();
        await Assert.That(Diff3.HasConflictMarkers("a <<<<<<< b\n")).IsFalse();
    }

    private static string RandomText(Random random) =>
        string.Concat(Enumerable.Range(0, random.Next(0, 12)).Select(_ => $"w{random.Next(0, 6)}\n"));

    private static string Mutate(Random random, string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        for (var edits = random.Next(1, 3); edits > 0; edits--)
        {
            switch (random.Next(3))
            {
                case 0 when lines.Count > 0: lines.RemoveAt(random.Next(lines.Count)); break;
                case 1 when lines.Count > 0: lines[random.Next(lines.Count)] = $"e{random.Next(100)}"; break;
                default: lines.Insert(random.Next(lines.Count + 1), $"i{random.Next(100)}"); break;
            }
        }

        var result = string.Concat(lines.Select(line => line + "\n"));
        return result == text ? result + "tail\n" : result;
    }
}
