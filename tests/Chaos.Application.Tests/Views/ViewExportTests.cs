using Chaos.Application.Contracts;
using Chaos.Application.Views;
using Chaos.Domain.Tasks;
using Chaos.Domain.Views;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Chaos.Application.Tests.Views;

/// <summary>CSV quoting and spreadsheet formula protection for a view's export (PLAN2 §11.2), and the search-to-view conversion.</summary>
public sealed class ViewExportTests
{
    private static TaskQueryRow Row(string title, string container = "Inbox", params string[] tagIds)
    {
        var now = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        var task = new TaskRecord
        {
            Id = "t1", SpaceId = "s", ContainerId = "c", Title = title, Notes = string.Empty, Status = TaskItemStatus.InProgress,
            Priority = TaskPriority.High, Position = 0, CapacityCategory = CapacityCategory.Personal,
            DueDate = new DateOnly(2026, 12, 1), DueTime = new TimeOnly(9, 30), DueZoneId = "UTC", EstimateMinutes = 45,
            Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        return new TaskQueryRow(task, container, false, tagIds, [], null);
    }

    private static readonly IReadOnlyDictionary<string, string> NoTags = new Dictionary<string, string>();

    [Test]
    [Arguments("=HYPERLINK(\"http://x\")", "'=HYPERLINK")]
    [Arguments("+1+1", "'+1+1")]
    [Arguments("-2+3", "'-2+3")]
    [Arguments("@SUM(A1)", "'@SUM(A1)")]
    [Arguments("\tcmd", "'\tcmd")]
    public async Task Csv_TitleThatLooksLikeAFormula_IsPrefixedWithAnApostrophe(string title, string expectedStart)
    {
        var csv = ViewExport.Csv([Row(title)], ["title"], NoTags);

        var line = csv.Split("\r\n")[1];
        await Assert.That(line.TrimStart('"')).StartsWith(expectedStart);
    }

    [Test]
    public async Task Csv_OrdinaryTitles_AreNotChanged()
    {
        var csv = ViewExport.Csv([Row("Plan the week"), Row("5 apples")], ["title"], NoTags);

        await Assert.That(csv).IsEqualTo("Title\r\nPlan the week\r\n5 apples\r\n");
    }

    [Test]
    public async Task Csv_CommasQuotesAndNewlines_FollowRfc4180()
    {
        var csv = ViewExport.Csv([Row("He said \"hi\", twice\nand left")], ["title"], NoTags);

        await Assert.That(csv).IsEqualTo("Title\r\n\"He said \"\"hi\"\", twice\nand left\"\r\n");
    }

    [Test]
    public async Task Csv_ChosenColumns_AppearInOrderWithReadableValues()
    {
        var csv = ViewExport.Csv([Row("A task", "Errands", "x", "y")], ["title", "status", "priority", "container", "due", "estimate", "tags"],
            new Dictionary<string, string> { ["x"] = "home", ["y"] = "work" });

        await Assert.That(csv).IsEqualTo("Title,Status,Priority,List,Due,Estimate (minutes),Tags\r\nA task,InProgress,High,Errands,2026-12-01 09:30,45,home; work\r\n");
    }

    [Test]
    public async Task Csv_TagAndContainerNames_AreAlsoProtected()
    {
        var csv = ViewExport.Csv([Row("ok", "=cmd")], ["container"], NoTags);

        await Assert.That(csv).IsEqualTo("List\r\n'=cmd\r\n");
    }

    [Test]
    public async Task FileName_StripsPathAndSpecialCharacters()
    {
        var name = ViewExport.FileName("..\\..\\Evil: name?/*", new DateTimeOffset(2026, 11, 10, 9, 5, 7, TimeSpan.Zero));

        await Assert.That(name).IsEqualTo("Evil name 20261110-090507.csv");
        await Assert.That(ViewExport.FileName("???", DateTimeOffset.UnixEpoch)).StartsWith("view ");
    }

    [Test]
    public async Task Utf8Bom_EmitsTheByteOrderMark()
    {
        await Assert.That(ViewExport.Utf8Bom.GetPreamble().Length).IsEqualTo(3);
    }

    [Test]
    public async Task FromSearch_TextAndFilters_BecomeAValidDefinition()
    {
        var spec = new TaskQuerySpec
        {
            View = TaskView.Search,
            Text = "  quarterly report ",
            Statuses = [TaskItemStatus.InProgress],
            Priorities = [TaskPriority.High, TaskPriority.Urgent],
            Due = new LocalDateRange(new DateOnly(2026, 11, 1), null),
            Category = CapacityCategory.Work,
        };

        var definition = ViewDefinition.Parse(ViewFromSearch.Build(spec));

        var fields = definition.Root.Items.OfType<ViewCondition>().Select(condition => condition.Field).ToList();
        await Assert.That(fields).Contains("text");
        await Assert.That(fields).Contains("status");
        await Assert.That(fields).Contains("category");
        await Assert.That(fields).Contains("due");
        await Assert.That(definition.Root.Items.OfType<ViewGroup>().Single().Match).IsEqualTo(ViewMatch.Any);
        await Assert.That(definition.Root.Items.OfType<ViewCondition>().First(condition => condition.Field == "text").Text).IsEqualTo("quarterly report");
    }

    [Test]
    public async Task FromSearch_NoStatusFilter_KeepsFinishedTasksOut_UnlessSearchIncludedThem()
    {
        var open = ViewDefinition.Parse(ViewFromSearch.Build(new TaskQuerySpec { View = TaskView.Search, Text = "x" }));
        var all = ViewDefinition.Parse(ViewFromSearch.Build(new TaskQuerySpec { View = TaskView.Search, Text = "x", IncludeCompleted = true }));

        await Assert.That(open.Root.Items.OfType<ViewCondition>().Any(condition => condition is { Field: "status", Operator: "notIn" })).IsTrue();
        await Assert.That(all.Root.Items.OfType<ViewCondition>().Any(condition => condition.Field == "status")).IsFalse();
    }
}
