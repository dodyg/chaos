using Chaos.Application.Abstractions;
using Chaos.Application.Transfer;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Tests.Transfer;

/// <summary>PLAN §14 import parsing without storage: RFC 4180 CSV with mapping, and the documented Org subset.</summary>
public sealed class ImportParserTests
{
    [Test]
    public async Task ParseTable_QuotedFieldsSemicolonsAndBom_AreRead()
    {
        var table = CsvImport.ParseTable("﻿Name;Description;Done\r\n\"Say \"\"hi\"\"\";\"two\r\nlines; really\";yes\r\n\r\nPlain;;\r\n");

        await Assert.That(table.Delimiter).IsEqualTo(';');
        await Assert.That(table.Headers).IsEquivalentTo(new[] { "Name", "Description", "Done" });
        await Assert.That(table.Rows.Count).IsEqualTo(2);
        await Assert.That(table.Rows[0].Cells[0]).IsEqualTo("Say \"hi\"");
        await Assert.That(table.Rows[0].Cells[1]).IsEqualTo("two\r\nlines; really");
        await Assert.That(table.Rows[1].Line).IsEqualTo(5);

        var mapping = CsvImport.SuggestMapping(table.Headers);
        await Assert.That(mapping["title"]).IsEqualTo(0);
        await Assert.That(mapping["notes"]).IsEqualTo(1);
        await Assert.That(mapping["status"]).IsEqualTo(2);

        var draft = CsvImport.ToDraft("x.csv", new string('0', 64), table, mapping);
        await Assert.That(draft.Tasks[0].Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That(draft.Tasks[0].Notes).IsEqualTo("two\nlines; really");
    }

    [Test]
    public async Task ParseTable_UnclosedQuote_IsAValidationError()
    {
        await Assert.That(() => CsvImport.ParseTable("title\n\"never closed\n")).Throws<AppException>();
    }

    [Test]
    public async Task ToDraft_UnknownStatusAndPriority_WarnAndUnknownParentOrBadDate_Error()
    {
        var table = CsvImport.ParseTable("title,status,priority,due,id,parent_id\nA,someday,p9,2026-13-40,1,\nB,,,,2,7\nC,,,,3,2\n");
        var draft = CsvImport.ToDraft("x.csv", new string('0', 64), table, CsvImport.SuggestMapping(table.Headers));

        await Assert.That(draft.Warnings.Select(w => w.Field)).IsEquivalentTo(new[] { "status", "priority" });
        await Assert.That(draft.Errors.Select(e => (e.Line, e.Field))).IsEquivalentTo(new[] { (2, "due"), (3, "parent_id"), (4, "parent_id") });
    }

    [Test]
    public async Task ToDraft_NoTitleColumnChosen_IsAnError()
    {
        var table = CsvImport.ParseTable("a,b\n1,2\n");
        var draft = CsvImport.ToDraft("x.csv", new string('0', 64), table, new Dictionary<string, int>());

        await Assert.That(draft.Errors.Single().Field).IsEqualTo("title");
    }

    [Test]
    public async Task OrgParse_ListsTasksSubtasksKeywordsPrioritiesTagsAndDates()
    {
        const string org = """
            #+TITLE: mine
            #+TODO: TODO NEXT | DONE DELEGATED
            * Errands
            ** NEXT [#A] Buy milk :shop:food:
               SCHEDULED: <2026-11-12 Thu 08:15> DEADLINE: <2026-11-13 Fri>
               :PROPERTIES:
               :ID: abc-1
               :END:
               Remember the oat one.
            *** DONE Check fridge
            **** TODO Too deep
            ** DELEGATED Ask Sam
               CLOSED: [2026-11-09 Mon 10:00]
            * TODO Loose task
            #+BEGIN_SRC markdown :chaos-diary 2026-11-08T07:00:00Z
            ,* escaped bullet
            #+END_SRC
            """;

        var draft = OrgImport.Parse("mine.org", new string('0', 64), org);

        await Assert.That(draft.Errors).IsEmpty();
        await Assert.That(draft.Tasks.Count).IsEqualTo(3);
        var milk = draft.Tasks[0];
        await Assert.That((milk.Title, milk.ListName, milk.Priority, milk.Status)).IsEqualTo(("Buy milk", "Errands", TaskPriority.Urgent, TaskItemStatus.NotStarted));
        await Assert.That(milk.TagPaths).IsEquivalentTo(new[] { "shop", "food" });
        await Assert.That(milk.Planned).IsEqualTo(new ImportDate(new DateOnly(2026, 11, 12), new TimeOnly(8, 15), null));
        await Assert.That(milk.Due).IsEqualTo(new ImportDate(new DateOnly(2026, 11, 13), null, null));
        await Assert.That(milk.SourceKey).IsEqualTo("abc-1");
        await Assert.That(milk.Notes.Trim()).IsEqualTo("Remember the oat one.");
        await Assert.That(milk.Subtasks.Select(s => (s.Title, s.Status))).IsEquivalentTo(new[] { ("Check fridge", TaskItemStatus.Done), ("Too deep", TaskItemStatus.NotStarted) });
        await Assert.That(draft.Warnings.Any(w => w.Message.Contains("cannot have subtasks", StringComparison.Ordinal))).IsTrue();

        var sam = draft.Tasks[1];
        await Assert.That(sam.Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That(sam.ClosedAtUtc).IsEqualTo(new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));

        var loose = draft.Tasks[2];
        await Assert.That(loose.ListName).IsNull();
        await Assert.That(loose.Diary.Single().Markdown).IsEqualTo("* escaped bullet");
    }

    [Test]
    public async Task OrgParse_ListProperties_CarryKindDeadlineAndDefaultCategory()
    {
        const string org = """
            * Sprint 21
            :PROPERTIES:
            :CHAOS_KIND: project
            :CHAOS_DEADLINE: 2026-11-30
            :CHAOS_DEFAULT_CATEGORY: work
            :END:
            ** TODO Ship it
            * Errands
            ** TODO Buy milk
            """;

        var draft = OrgImport.Parse("lists.org", new string('0', 64), org);

        await Assert.That(draft.Errors).IsEmpty();
        var ship = draft.Tasks.Single(task => task.Title == "Ship it");
        await Assert.That((ship.ListKind, ship.ListDeadline, ship.ListDefaultCategory)).IsEqualTo((ContainerKind.Project, (DateOnly?)new DateOnly(2026, 11, 30), (CapacityCategory?)CapacityCategory.Work));
        var milk = draft.Tasks.Single(task => task.Title == "Buy milk");
        await Assert.That((milk.ListKind, milk.ListDeadline, milk.ListDefaultCategory)).IsEqualTo((ContainerKind.List, (DateOnly?)null, (CapacityCategory?)null));
    }

    [Test]
    public async Task OrgParse_UnclosedSourceBlock_IsAnError()
    {
        var draft = OrgImport.Parse("x.org", new string('0', 64), "* TODO A\n#+BEGIN_SRC markdown\nnever closed\n");

        await Assert.That(draft.Errors.Single().Field).IsEqualTo("notes");
    }

    [Test]
    public async Task Validate_OverlongTitleAndBadTag_AreErrors()
    {
        var draft = new ImportDraft(ImportSourceKind.Csv, "x", new string('0', 64),
            [new ImportTask { Line = 2, SourceKey = "k", Title = new string('x', 600), TagPaths = ["a//b"] }], [], []);

        var errors = ImportCommands.Validate(draft);

        await Assert.That(errors.Select(e => e.Field)).Contains("title");
        await Assert.That(errors.Select(e => e.Field)).Contains("tags");
    }
}
