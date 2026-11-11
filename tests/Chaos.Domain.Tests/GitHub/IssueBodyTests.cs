using Chaos.Domain.GitHub;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Chaos.Domain.Tests.GitHub;

public sealed class IssueBodyTests
{
    [Test]
    public async Task ScanChecklist_NestedAndFencedItems_AreIgnored()
    {
        const string body = "- [ ] parent\r\n  - [x] child\r\n\r\n~~~csharp\r\n- [x] code\r\n~~~\r\n1. [X] آخِر 😀";
        var items = IssueBody.ScanChecklist(body);
        await Assert.That(items.Count).IsEqualTo(2);
        await Assert.That(items[0].Text).IsEqualTo("parent");
        await Assert.That(items[1].Text).IsEqualTo("آخِر 😀");
        await Assert.That(items[1].Checked).IsTrue();
    }

    [Test]
    public async Task Rewrite_ChangedAndDeletedItems_PreservesOtherBytes()
    {
        const string body = "intro\r\n+ [X] old  \r\n- [ ] remove\r\nend";
        var result = IssueBody.Rewrite(body, [new(0, "new", false), new(1, "", false, true)], []);
        await Assert.That(result).IsEqualTo("intro\r\n+ [ ] new\r\nend");
    }

    [Test]
    public async Task Rewrite_NoChanges_PreservesGeneratedBodies()
    {
        var random = new Random(8129);
        string[] fragments = ["text", "- [ ] عَربي 😀", "- [X] duplicate", "  - [ ] nested", "```", "~~~", "    code", "", "combining e\u0301"];
        for (var i = 0; i < 500; i++)
        {
            var newline = i % 2 == 0 ? "\r\n" : "\n";
            var body = string.Join(newline, Enumerable.Range(0, random.Next(1, 30)).Select(_ => fragments[random.Next(fragments.Length)]));
            if (i % 3 == 0) body += newline;
            var edits = IssueBody.ScanChecklist(body).Select(item => new ChecklistEdit(item.Ordinal, item.Text, item.Checked)).ToArray();
            await Assert.That(IssueBody.Rewrite(body, edits, [])).IsEqualTo(body);
        }
    }

    [Test]
    public async Task Rewrite_NewItem_AppendsAfterLastChecklistItem()
    {
        var body = "start\n- [ ] first\nafter";
        await Assert.That(IssueBody.Rewrite(body, [], [new("second", true)])).IsEqualTo("start\n- [ ] first\n- [x] second\nafter");
    }

    [Test]
    public async Task Rewrite_MultilineTitle_IsRejected()
    {
        await Assert.That(() => IssueBody.Rewrite("", [], [new("bad\ntitle", false)])).Throws<ArgumentException>();
    }
    [Test]
    public async Task ScanChecklist_LongerFenceAndIndentedCode_AreIgnored()
    {
        var body = "````\n- [ ] hidden\n```\n- [x] still hidden\n````\n    - [ ] code\n1) [ ] visible\n";
        var items = IssueBody.ScanChecklist(body);
        await Assert.That(items.Count).IsEqualTo(1);
        await Assert.That(items[0].Text).IsEqualTo("visible");
    }

    [Test]
    public async Task Rewrite_LargeChecklistWithDuplicateTexts_PreservesIdentity()
    {
        var body = string.Join("\r\n", Enumerable.Repeat("- [ ] same", 500));
        var items = IssueBody.ScanChecklist(body);
        await Assert.That(items.Count).IsEqualTo(500);
        await Assert.That(IssueBody.Rewrite(body, items.Select(item => new ChecklistEdit(item.Ordinal, item.Text, item.Checked)).ToArray(), [])).IsEqualTo(body);
    }

    [Test]
    public async Task Rewrite_TrailingCarriageReturn_IsNotDiscarded()
    {
        const string body = "- [ ] text\r";
        var item = IssueBody.ScanChecklist(body).Single();
        await Assert.That(IssueBody.Rewrite(body, [new(0, item.Text, item.Checked)], [])).IsEqualTo(body);
    }
}
