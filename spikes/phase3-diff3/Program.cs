using System.Diagnostics;
using System.Text;

const string conflict = "<<<<<<< yours\nleft\n=======\nright\n>>>>>>> remote\n";
var fixtures = new (string Name, string Base, string Local, string Remote, string Expected)[]
{
    ("empty", "", "", "", ""),
    ("identity", "a\n", "a\n", "a\n", "a\n"),
    ("local only", "a\n", "left\n", "a\n", "left\n"),
    ("remote only", "a\n", "a\n", "right\n", "right\n"),
    ("same edit", "a\n", "b\n", "b\n", "b\n"),
    ("different lines", "a\nb\nc\n", "A\nb\nc\n", "a\nb\nC\n", "A\nb\nC\n"),
    ("adjacent lines", "a\nb\n", "A\nb\n", "a\nB\n", "A\nB\n"),
    ("same line conflict", "a\n", "left\n", "right\n", conflict),
    ("insert conflict", "a\n", "left\na\n", "right\na\n", conflict + "a\n"),
    ("insert different positions", "a\nb\n", "x\na\nb\n", "a\nb\ny\n", "x\na\nb\ny\n"),
    ("delete and edit", "a\nb\nc\n", "b\nc\n", "a\nb\nC\n", "b\nC\n"),
    ("delete same", "a\nb\n", "b\n", "b\n", "b\n"),
    ("trailing newline", "a\nb\n", "A\nb\n", "a\nB\n", "A\nB\n"),
    ("no trailing newline", "a\nb", "A\nb", "a\nB", "A\nB"),
    ("CRLF", "a\r\nb\r\n", "A\r\nb\r\n", "a\r\nB\r\n", "A\r\nB\r\n"),
    ("RTL", "أ\nب\n", "أول\nب\n", "أ\nثان\n", "أول\nثان\n"),
    ("front matter", "---\ntitle: old\n---\nbody\n", "---\ntitle: new\n---\nbody\n", "---\ntitle: old\n---\nbody changed\n", "---\ntitle: new\n---\nbody changed\n"),
    ("empty base", "", "left\n", "right\n", conflict),
    ("same insert", "a\n", "x\na\n", "x\na\n", "x\na\n"),
    ("edit at end", "a\nb\n", "A\nb\n", "a\nb\nx\n", "A\nb\nx\n"),
};
var failed = 0;
foreach (var fixture in fixtures)
{
    var actual = Diff3.Merge(fixture.Base, fixture.Local, fixture.Remote);
    if (actual == fixture.Expected) Console.WriteLine($"PASS {fixture.Name}");
    else { failed++; Console.Error.WriteLine($"FAIL {fixture.Name}: {actual.Replace("\n", "\\n")}"); }
}
if (failed != 0) return 1;
var line = new string('x', 127) + "\n";
var page = string.Concat(Enumerable.Repeat(line, 8192));
var localPage = "left" + page[4..];
var remotePage = page[..^5] + "right";
var timer = Stopwatch.StartNew();
var merged = Diff3.Merge(page, localPage, remotePage);
timer.Stop();
if (!merged.StartsWith("left") || !merged.EndsWith("right")) return 1;
Console.WriteLine($"{fixtures.Length} fixtures passed; {Encoding.UTF8.GetByteCount(page)} bytes merged in {timer.Elapsed.TotalMilliseconds:F1} ms");
return 0;
