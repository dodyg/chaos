using Chaos.Application.Abstractions;
using Chaos.Application.Transfer;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Tests.Transfer;

/// <summary>PLAN §14 import parsing without storage: RFC 4180 CSV with mapping.</summary>
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
    public async Task Validate_OverlongTitleAndBadTag_AreErrors()
    {
        var draft = new ImportDraft(ImportSourceKind.Csv, "x", new string('0', 64),
            [new ImportTask { Line = 2, SourceKey = "k", Title = new string('x', 600), TagPaths = ["a//b"] }], [], []);

        var errors = ImportCommands.Validate(draft);

        await Assert.That(errors.Select(e => e.Field)).Contains("title");
        await Assert.That(errors.Select(e => e.Field)).Contains("tags");
    }
}
