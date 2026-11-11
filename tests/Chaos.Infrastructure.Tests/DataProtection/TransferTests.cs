using System.IO.Compression;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Transfer;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Infrastructure.Transfer;

namespace Chaos.Infrastructure.Tests.DataProtection;

/// <summary>
/// PLAN §15 WP12 transfer on real SQLite: lossless JSON round trip of every persisted feature
/// (including deleted rows), hostile archives, Markdown/Org exports, CSV/Org/JSON imports with
/// preview, duplicates, conflicts and atomic rollback, recovery from a bad import with a local
/// backup (exit criterion), and permanent purge.
/// </summary>
public sealed class TransferTests
{
    [Test]
    public async Task Purge_LinkedTask_KeepsIgnoredIssueAndRemovesDependentMappings()
    {
        using var host = await DataHost.StartAsync();
        var seed = await host.SeedAsync();
        await host.Store.ExecuteAsync(async (session, ct) =>
        {
            using var command = ((SqliteDataSession)session).Connection.CreateCommand();
            command.CommandText = """
                INSERT INTO GitHubConnections(Id,SpaceId,ProfileId,WebBaseUrl,ApiBaseUrl,AccountId,AccountLogin,CreatedAtUtc,UpdatedAtUtc)
                SELECT 'g',SpaceId,(SELECT OwnerProfileId FROM Spaces WHERE Id=SpaceId),'https://github.com','https://api.github.com',1,'person',CreatedAtUtc,UpdatedAtUtc FROM Tasks WHERE Id=$task;
                INSERT INTO GitHubRepositories(Id,ConnectionId,RepositoryId,Owner,Name,FullName,HtmlUrl,IsPrivate,TargetContainerId,FilterJson,CreatedAtUtc,UpdatedAtUtc)
                SELECT 'r','g',1,'person','repo','person/repo','url',1,ContainerId,'{}',CreatedAtUtc,UpdatedAtUtc FROM Tasks WHERE Id=$task;
                INSERT INTO GitHubIssueLinks(Id,RepositorySubscriptionId,ConnectionId,TaskId,IssueId,IssueNodeId,IssueNumber,HtmlUrl,BaseJson,BaseRemoteUpdatedUtc,CreatedAtUtc,UpdatedAtUtc)
                SELECT 'l','r','g',Id,1,'node',1,'url','{}',UpdatedAtUtc,CreatedAtUtc,UpdatedAtUtc FROM Tasks WHERE Id=$task;
                INSERT INTO GitHubChecklistItems SELECT Id,'l',Position,Title,0 FROM Subtasks WHERE TaskId=$task;
                INSERT INTO GitHubCommentLinks(DiaryEntryId,IssueLinkId,Origin,State) SELECT Id,'l',1,0 FROM DiaryEntries WHERE TaskId=$task;
                """;
            command.Parameters.AddWithValue("$task", seed.PaintTaskId);
            await command.ExecuteNonQueryAsync(ct);
            return true;
        });
        var task = (await host.Tasks.GetAsync(seed.PaintTaskId))!;
        await host.Tasks.DeleteAsync(new Application.Tasks.DeleteTaskCommand(DataHost.Op(), task.Id, task.Version));
        await host.Purge.PurgeAsync(task.Id, CancellationToken.None);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM GitHubIssueLinks WHERE TaskId IS NULL AND State=3 AND LocalDirty=0;")).IsEqualTo(1L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM GitHubChecklistItems;")).IsEqualTo(0L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM GitHubCommentLinks;")).IsEqualTo(0L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM pragma_foreign_key_check;")).IsEqualTo(0L);
    }

    private static ImportSource Read(ImportSourceKind kind, string name, string text, IReadOnlyDictionary<string, int>? mapping = null) =>
        DataTransfer.Read(kind, name, Encoding.UTF8.GetBytes(text), mapping);

    [Test]
    public async Task JsonArchive_ExportThenRestoreIntoCleanRoot_IsLossless()
    {
        using var source = await DataHost.StartAsync();
        await source.SeedAsync();
        var expected = await source.DumpAsync();
        var export = await source.Transfer.ExportAsync(ExportFormat.Json, Path.Combine(source.Root, "out"), CancellationToken.None);

        using var target = await DataHost.StartAsync();
        ArchiveContent archive;
        await using (var stream = File.OpenRead(export.Path))
        {
            archive = JsonArchive.Read(stream);
        }

        await target.Protection.RestoreArchiveAsync(archive, export.FileName, CancellationToken.None);

        await BackupRestoreTests.AssertSameData(expected, await target.DumpAsync());
        // Deleted rows travel too, and tombstones stay tombstones.
        await Assert.That(await target.CountAsync("SELECT count(*) FROM Tasks WHERE DeletedAtUtc IS NOT NULL;")).IsEqualTo(1L);
        await Assert.That(await target.CountAsync("SELECT count(*) FROM RecurrenceSeriesRevisions;")).IsEqualTo(await source.CountAsync("SELECT count(*) FROM RecurrenceSeriesRevisions;"));
        await Assert.That(archive.Manifest.Tables.ContainsKey("OperationReceipts")).IsFalse();
        await Assert.That(archive.Manifest.Tables["Tasks"]).IsEqualTo(5L);
    }

    [Test]
    public async Task JsonArchive_HostileOrDamagedArchives_AreRefusedBeforeAnythingIsWritten()
    {
        using var host = await DataHost.StartAsync();
        await host.SeedAsync();
        var export = await host.Transfer.ExportAsync(ExportFormat.Json, Path.Combine(host.Root, "out"), CancellationToken.None);
        var good = await File.ReadAllBytesAsync(export.Path);

        byte[] Rewrite(Action<ZipArchive> change)
        {
            using var stream = new MemoryStream();
            stream.Write(good);
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
            {
                change(zip);
            }

            return stream.ToArray();
        }

        var traversal = Rewrite(zip => zip.CreateEntry("../../evil.txt"));
        var extra = Rewrite(zip => zip.CreateEntry("notes.txt"));
        var edited = Rewrite(zip =>
        {
            var data = zip.GetEntry(JsonArchive.DataEntry)!;
            string text;
            using (var reader = new StreamReader(data.Open()))
            {
                text = reader.ReadToEnd();
            }

            data.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(JsonArchive.DataEntry).Open());
            writer.Write(text.Replace("Call the plumber", "Call the plumber!", StringComparison.Ordinal));
        });
        var newer = Rewrite(zip =>
        {
            var manifest = zip.GetEntry(JsonArchive.ManifestEntry)!;
            string text;
            using (var reader = new StreamReader(manifest.Open()))
            {
                text = reader.ReadToEnd();
            }

            manifest.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(JsonArchive.ManifestEntry).Open());
            writer.Write(System.Text.RegularExpressions.Regex.Replace(text, "\"schemaVersion\": \\d+", "\"schemaVersion\": 999"));
        });

        foreach (var (bytes, expected) in new[] { (traversal, "unsafe path"), (extra, "exactly manifest.json"), (edited, "checksum"), (newer, "newer version"), (Encoding.UTF8.GetBytes("PK not a zip"), "ZIP") })
        {
            var error = await Assert.That(() => DataTransfer.Read(ImportSourceKind.ChaosJson, "x.zip", bytes, null)).Throws<AppException>();
            await Assert.That(error!.Message).Contains(expected);
        }

        await Assert.That(SafeArchive.IsSafeEntryName("C:/Windows/x")).IsFalse();
        await Assert.That(SafeArchive.IsSafeEntryName("/etc/passwd")).IsFalse();
        await Assert.That(SafeArchive.IsSafeEntryName("a\\..\\b")).IsFalse();
        await Assert.That(SafeArchive.IsSafeEntryName("tasks/01 Inbox/x.md")).IsTrue();
    }

    [Test]
    public async Task OrgExport_ThenImportIntoCleanRoot_KeepsTasksSubtasksTagsNotesAndDiary()
    {
        using var source = await DataHost.StartAsync();
        var seed = await source.SeedAsync();
        var export = await source.Transfer.ExportAsync(ExportFormat.Org, Path.Combine(source.Root, "out"), CancellationToken.None);
        var org = await File.ReadAllTextAsync(export.Path);
        await Assert.That(org).Contains("#+TODO: TODO STARTED WAITING REVIEW | DONE CANCELLED");
        await Assert.That(org).Contains("SCHEDULED: <2026-11-11 Wed 09:30> DEADLINE: <2026-11-14 Sat>");
        await Assert.That(org).Contains(",* not a heading"); // Markdown line escaped inside the src block
        await Assert.That(org).DoesNotContain("Old idea"); // deleted

        var draft = Read(ImportSourceKind.Org, export.FileName, org).Draft;
        await Assert.That(draft.Errors).IsEmpty();
        var paint = draft.Tasks.Single(task => task.Title == "Buy paint — أزرق 🎨");
        await Assert.That(paint.ListName).IsEqualTo("Home project");
        await Assert.That(paint.Priority).IsEqualTo(TaskPriority.High);
        await Assert.That(paint.Planned).IsEqualTo(new ImportDate(new DateOnly(2026, 11, 11), new TimeOnly(9, 30), "UTC"));
        await Assert.That(paint.Due!.Date).IsEqualTo(new DateOnly(2026, 11, 14));
        await Assert.That(paint.TagPaths).IsEquivalentTo(new[] { "colour", "home/errands" });
        await Assert.That(paint.Notes).Contains("* not a heading");
        await Assert.That(paint.Notes).Contains("| hall | blue |");
        await Assert.That(paint.Subtasks.Select(s => (s.Title, s.Status))).IsEquivalentTo(new[] { ("Pick shade", TaskItemStatus.Done), ("Get brushes", TaskItemStatus.NotStarted) });
        await Assert.That(paint.Diary.Single().Markdown).IsEqualTo("Bought the **blue** sample.\n* careful");
        await Assert.That(draft.Tasks.Single(task => task.Title == "Finished thing").Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That(draft.Warnings.Any(w => w.Field == "recurrence")).IsTrue();

        using var target = await DataHost.StartAsync();
        var result = await target.Imports.CommitAsync(draft, new ImportCommitOptions(new Dictionary<string, string>(), null, null));
        await Assert.That(result.Created).IsEqualTo(4);
        await Assert.That(result.CreatedListNames).IsEquivalentTo(new[] { "Home project" });
        await Assert.That(await target.CountAsync("SELECT count(*) FROM DiaryEntries;")).IsEqualTo(1L);
        await Assert.That(await target.CountAsync("SELECT count(*) FROM TaskHistory WHERE EventKind = 'task.imported';")).IsEqualTo(4L);

        // Importing the same file into the database it came from finds the tasks already there.
        var again = await source.Imports.PreviewAsync(draft);
        await Assert.That(again.NewTasks).IsEqualTo(0);
        await Assert.That(again.Duplicates).IsEqualTo(4);
        _ = seed;
    }

    [Test]
    public async Task MarkdownExport_HostileTitles_GetSafeUniqueFileNames()
    {
        using var host = await DataHost.StartAsync();
        await host.SeedAsync();
        var inbox = await host.Store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token);
            var space = await session.Spaces.GetPersonalByOwnerAsync(profile!.Id, token);
            return (await session.Containers.GetInboxAsync(space!.Id, token))!.Id;
        });
        foreach (var title in new[] { "../../evil", "CON", "a:b*c?", "   ...   ", "same", "same" })
        {
            await host.Tasks.CaptureAsync(new Application.Tasks.CaptureTaskCommand(DataHost.Op(), inbox, title, "note for " + title));
        }

        var export = await host.Transfer.ExportAsync(ExportFormat.Markdown, Path.Combine(host.Root, "out"), CancellationToken.None);

        using var zip = ZipFile.OpenRead(export.Path);
        var names = zip.Entries.Select(entry => entry.FullName).ToList();
        await Assert.That(names.All(SafeArchive.IsSafeEntryName)).IsTrue();
        await Assert.That(names.Distinct(StringComparer.OrdinalIgnoreCase).Count()).IsEqualTo(names.Count);
        await Assert.That(names.Any(name => name.Contains("_CON (", StringComparison.Ordinal))).IsTrue();
        await Assert.That(names).Contains("README.md");
        var paint = zip.Entries.Single(entry => entry.FullName.Contains("Buy paint", StringComparison.Ordinal));
        using var reader = new StreamReader(paint.Open());
        var markdown = await reader.ReadToEndAsync();
        await Assert.That(markdown).Contains("tags: [\"colour\",\"home/errands\"]");
        await Assert.That(markdown).Contains("- [x] Pick shade");
        await Assert.That(markdown).Contains("| hall | blue |");
        await Assert.That(names.Any(name => name.StartsWith("pad/", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task CsvImport_PreviewThenCommit_CreatesListsTagsSubtasksAndSkipsDuplicatesOnReimport()
    {
        using var host = await DataHost.StartAsync();
        const string csv = "Title,Notes,List,Status,Due,Priority,Tags,id,parent_id\n"
            + "Plan trip,\"Line one\nLine two, with comma\",Travel,in progress,2026-12-01,high,\"travel; family\",1,\n"
            + "Book hotel,,Travel,done,,,,2,1\n"
            + "Inbox thing,,,,2026-12-02 14:30,,,,\n";
        var source = Read(ImportSourceKind.Csv, "todo.csv", csv);
        await Assert.That(source.CsvMapping!["title"]).IsEqualTo(0);
        await Assert.That(source.CsvMapping["parent_id"]).IsEqualTo(8);

        var preview = await host.Imports.PreviewAsync(source.Draft);
        await Assert.That(preview.Errors).IsEmpty();
        await Assert.That(preview.NewTasks).IsEqualTo(2);
        await Assert.That(preview.Subtasks).IsEqualTo(1);
        await Assert.That(preview.Lists.Single().SourceName).IsEqualTo("Travel");
        await Assert.That(preview.Lists.Single().ExistingContainerId).IsNull();

        var result = await host.Imports.CommitAsync(source.Draft, new ImportCommitOptions(new Dictionary<string, string>(), null, "backup.db"));
        await Assert.That(result.Created).IsEqualTo(2);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Containers WHERE Name = 'Travel';")).IsEqualTo(1L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks WHERE Title = 'Plan trip' AND Status = 1 AND Notes = 'Line one\nLine two, with comma' AND DueDate = '2026-12-01' AND Priority = 3;")).IsEqualTo(1L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Subtasks WHERE Title = 'Book hotel' AND Status = 4 AND CompletedAtUtc IS NOT NULL;")).IsEqualTo(1L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks WHERE Title = 'Inbox thing' AND DueTime = '14:30:00' AND DueZoneId IS NOT NULL;")).IsEqualTo(1L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tags WHERE DisplayName IN ('travel','family');")).IsEqualTo(2L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM SearchIndex WHERE SearchIndex MATCH 'comma';")).IsEqualTo(1L);

        var again = await host.Imports.CommitAsync(Read(ImportSourceKind.Csv, "todo.csv", csv).Draft, new ImportCommitOptions(new Dictionary<string, string>(), null, null));
        await Assert.That(again.Created).IsEqualTo(0);
        await Assert.That(again.Duplicates).IsEqualTo(2);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(2L);
        await Assert.That((await host.Imports.ListBatchesAsync(10)).Count).IsEqualTo(2);
    }

    [Test]
    public async Task CsvImport_LastRowInvalid_PreviewReportsItAndCommitWritesNothing()
    {
        using var host = await DataHost.StartAsync();
        var draft = Read(ImportSourceKind.Csv, "bad.csv", "title,due\nOne,2026-12-01\nTwo,2026-12-02\nThree,next tuesday\n").Draft;

        var preview = await host.Imports.PreviewAsync(draft);
        await Assert.That(preview.CanCommit).IsFalse();
        await Assert.That(preview.Errors.Single().Line).IsEqualTo(4);

        var error = await Assert.That(async () => await host.Imports.CommitAsync(draft, new ImportCommitOptions(new Dictionary<string, string>(), null, null))).Throws<AppException>();
        await Assert.That(error!.Message).Contains("line 4");
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(0L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM ImportBatches;")).IsEqualTo(0L);
    }

    [Test]
    public async Task ImportCommit_WriteFailsOnTheLastItem_RollsBackEveryEarlierItem()
    {
        using var host = await DataHost.StartAsync();
        var draft = Read(ImportSourceKind.Csv, "three.csv", "title\nFirst\nSecond\nThird\n").Draft;
        var failing = new ImportCommands(new FailOnThirdTaskStore(host.Store), host.Ids, host.Clock);

        await Assert.That(async () => await failing.CommitAsync(draft, new ImportCommitOptions(new Dictionary<string, string>(), null, null))).Throws<AppException>();

        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(0L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM ImportBatches;")).IsEqualTo(0L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM TaskHistory;")).IsEqualTo(0L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM SearchDocuments;")).IsEqualTo(0L);
    }

    [Test]
    public async Task BadImport_RecoveredFromTheLocalPreImportBackup()
    {
        using var host = await DataHost.StartAsync();
        await host.SeedAsync();
        var before = await host.DumpAsync();

        var backup = await host.Protection.BackupNowAsync(BackupReason.PreImport, CancellationToken.None);
        var wrongFile = string.Join('\n', new[] { "title,list" }.Concat(Enumerable.Range(1, 40).Select(n => $"Row {n} from the wrong file,Imported junk"))) + "\n";
        await host.Imports.CommitAsync(Read(ImportSourceKind.Csv, "wrong.csv", wrongFile).Draft, new ImportCommitOptions(new Dictionary<string, string>(), null, backup.FileName));
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(45L);

        await host.Protection.RestoreBackupAsync(backup.FileName, backup.ChecksumSha256, CancellationToken.None);

        await BackupRestoreTests.AssertSameData(before, await host.DumpAsync());
    }

    [Test]
    public async Task JsonMerge_IdsTakenByDifferentLocalTasks_AreConflictsAndLocalDataIsKept()
    {
        // Sequential IDs in both roots make the archive's task IDs collide with different local tasks.
        using var source = await DataHost.StartAsync(ids: new SequentialIdGenerator());
        await source.SeedAsync();
        var export = await source.Transfer.ExportAsync(ExportFormat.Json, Path.Combine(source.Root, "out"), CancellationToken.None);
        var bytes = await File.ReadAllBytesAsync(export.Path);

        using var target = await DataHost.StartAsync(ids: new SequentialIdGenerator());
        await target.SeedAsync();
        await target.Store.ExecuteAsync(async (session, token) =>
        {
            using var command = ((SqliteDataSession)session).Connection.CreateCommand();
            command.CommandText = "UPDATE Tasks SET Title = 'Local version ' || Title;";
            return await command.ExecuteNonQueryAsync(token);
        });
        var localBefore = await target.CountAsync("SELECT count(*) FROM Tasks;");

        var merge = DataTransfer.Read(ImportSourceKind.ChaosJson, export.FileName, bytes, null).Draft;
        var preview = await target.Imports.PreviewAsync(merge);
        await Assert.That(preview.NewTasks).IsEqualTo(0);
        await Assert.That(preview.Conflicts).IsEqualTo(4);
        await Assert.That(preview.Items.First(item => item.Outcome == ImportItemOutcome.Conflict).Detail!).Contains("Local version");

        await target.Imports.CommitAsync(merge, new ImportCommitOptions(new Dictionary<string, string>(), null, null));
        await Assert.That(await target.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(localBefore);
        await Assert.That(await target.CountAsync("SELECT count(*) FROM Tasks WHERE Title NOT LIKE 'Local version %';")).IsEqualTo(0L);
    }

    [Test]
    public async Task JsonMerge_IntoAnotherDatabase_KeepsIdsListsTagsSubtasksAndDiary()
    {
        using var source = await DataHost.StartAsync();
        var seed = await source.SeedAsync();
        var export = await source.Transfer.ExportAsync(ExportFormat.Json, Path.Combine(source.Root, "out"), CancellationToken.None);

        using var target = await DataHost.StartAsync();
        var merge = DataTransfer.Read(ImportSourceKind.ChaosJson, export.FileName, await File.ReadAllBytesAsync(export.Path), null).Draft;
        var result = await target.Imports.CommitAsync(merge, new ImportCommitOptions(new Dictionary<string, string>(), null, null));

        await Assert.That(result.Created).IsEqualTo(4);
        await Assert.That(await target.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{seed.PaintTaskId}' AND ContainerId = '{seed.ProjectId}';")).IsEqualTo(1L);
        await Assert.That(await target.CountAsync($"SELECT count(*) FROM Subtasks WHERE TaskId = '{seed.PaintTaskId}';")).IsEqualTo(2L);
        await Assert.That(await target.CountAsync($"SELECT count(*) FROM DiaryEntries WHERE TaskId = '{seed.PaintTaskId}';")).IsEqualTo(1L);
        await Assert.That(await target.CountAsync($"SELECT count(*) FROM TaskTags WHERE TaskId = '{seed.PaintTaskId}';")).IsEqualTo(2L);
        await Assert.That(merge.Warnings.Any()).IsTrue();
    }

    [Test]
    public async Task Purge_DeletedTask_RemovesItAndEverythingOnlyItOwned()
    {
        using var host = await DataHost.StartAsync();
        var seed = await host.SeedAsync();
        var paint = (await host.Tasks.GetAsync(seed.PaintTaskId))!;
        await host.Tasks.DeleteAsync(new Application.Tasks.DeleteTaskCommand(DataHost.Op(), paint.Id, paint.Version));
        var activeBefore = await host.CountAsync("SELECT count(*) FROM Tasks WHERE DeletedAtUtc IS NULL;");

        var preview = await host.Purge.PreviewAsync(null, CancellationToken.None);
        await Assert.That(preview.Tasks).IsEqualTo(2L); // the paint task and the earlier deleted one
        await Assert.That(preview.Subtasks).IsEqualTo(2L);
        await Assert.That(preview.DiaryEntries).IsEqualTo(1L);

        await Assert.That(async () => await host.Purge.PurgeAsync(seed.WaitingTaskId, CancellationToken.None)).Throws<AppException>();
        var outcome = await host.Purge.PurgeAsync(null, CancellationToken.None);

        await Assert.That(outcome.Removed.Tasks).IsEqualTo(2L);
        foreach (var table in new[] { "Tasks WHERE Id", "Subtasks WHERE TaskId", "DiaryEntries WHERE TaskId", "TaskHistory WHERE TaskId", "TaskTags WHERE TaskId", "ReminderRules WHERE TaskId", "NotificationJobs WHERE TaskId", "DayPlanItems WHERE TaskId", "SearchDocuments WHERE TaskId" })
        {
            await Assert.That(await host.CountAsync($"SELECT count(*) FROM {table} = '{paint.Id}';")).IsEqualTo(0L);
        }

        await Assert.That(await host.CountAsync($"SELECT count(*) FROM UndoEntries WHERE instr(InversePayload, '{paint.Id}') > 0;")).IsEqualTo(0L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tasks WHERE DeletedAtUtc IS NULL;")).IsEqualTo(activeBefore);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Tags WHERE DisplayName = 'someday';")).IsEqualTo(1L); // still a live tag
        await Assert.That(await host.CountAsync("SELECT count(*) FROM pragma_foreign_key_check;")).IsEqualTo(0L);
    }

    /// <summary>A store whose third task insert fails, to prove the import transaction is all-or-nothing.</summary>
    private sealed class FailOnThirdTaskStore(SqliteApplicationStore inner) : IApplicationStore
    {
        public Task<TResult> ExecuteAsync<TResult>(Func<IDataSession, CancellationToken, Task<TResult>> operation, CancellationToken ct = default) =>
            inner.ExecuteAsync((session, token) => operation(new FailingSession(session), token), ct);

        public Task<TResult> QueryAsync<TResult>(Func<IDataSession, CancellationToken, Task<TResult>> query, CancellationToken ct = default) =>
            inner.QueryAsync(query, ct);

        private sealed class FailingSession(IDataSession inner) : IDataSession
        {
            public IProfileStore Profiles => inner.Profiles;
            public ISpaceStore Spaces => inner.Spaces;
            public IContainerStore Containers => inner.Containers;
            public ISectionStore Sections => inner.Sections;
            public ITaskStore Tasks { get; } = new FailingTasks(inner.Tasks);
            public ISubtaskStore Subtasks => inner.Subtasks;
            public ITemplateStore Templates => inner.Templates;
            public ITagStore Tags => inner.Tags;
            public ITaskQueryStore Queries => inner.Queries;
            public ITaskHistoryStore History => inner.History;
            public IDiaryStore Diary => inner.Diary;
            public IUndoStore Undo => inner.Undo;
            public IChecklistConversionStore ChecklistConversions => inner.ChecklistConversions;
            public IPadStore Pad => inner.Pad;
            public IRecurrenceStore Recurrence => inner.Recurrence;
            public IReminderRuleStore ReminderRules => inner.ReminderRules;
            public IWaitingStore Waiting => inner.Waiting;
            public INotificationJobStore Notifications => inner.Notifications;
            public IDayPlanStore DayPlans => inner.DayPlans;
            public IWeekPlanStore Weeks => inner.Weeks;
            public ISummaryStore Summaries => inner.Summaries;
            public IOperationReceiptStore Receipts => inner.Receipts;
            public IBackupCatalogStore Backups => inner.Backups;
            public IImportStore Imports => inner.Imports;
            public IGitHubStore GitHub => inner.GitHub;
        }

        private sealed class FailingTasks(ITaskStore inner) : ITaskStore
        {
            private int _adds;
            public Task<TaskRecord?> GetAsync(string taskId, CancellationToken ct) => inner.GetAsync(taskId, ct);
            public Task<TaskRecord?> GetIncludingDeletedAsync(string taskId, CancellationToken ct) => inner.GetIncludingDeletedAsync(taskId, ct);
            public Task<IReadOnlyList<TaskRecord>> ListByContainerAsync(string containerId, CancellationToken ct) => inner.ListByContainerAsync(containerId, ct);
            public Task<IReadOnlyList<TaskRecord>> ListDeletedByContainerAsync(string containerId, CancellationToken ct) => inner.ListDeletedByContainerAsync(containerId, ct);
            public Task<TaskRecord?> UpdateAsync(TaskRecord updated, long expectedVersion, CancellationToken ct) => inner.UpdateAsync(updated, expectedVersion, ct);
            public Task<long> GetNextPositionAsync(string containerId, CancellationToken ct) => inner.GetNextPositionAsync(containerId, ct);

            public Task AddAsync(TaskRecord task, CancellationToken ct) =>
                ++_adds == 3 ? throw new AppException(AppErrorKind.StorageFull, "Simulated full disk on the last row.") : inner.AddAsync(task, ct);
        }
    }
}
