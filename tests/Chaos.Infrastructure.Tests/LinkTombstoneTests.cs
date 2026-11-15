using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Pages;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Infrastructure.Transfer;
using System.Security.Cryptography;
using System.Text.Json;

namespace Chaos.Infrastructure.Tests;

/// <summary>
/// Link tables are tombstoned (migration 024, PLAN2 §7.2): removing a link records when it
/// went away so the removal can sync, re-adding restores the same row, reads only see active
/// links, and a purge still removes the rows. Proven on real file-backed SQLite through the
/// real commands.
/// </summary>
public sealed class LinkTombstoneTests
{
    private static string Op() => Guid.NewGuid().ToString();

    private static async Task<TaskRecord> CaptureTaskAsync(DataHost host, string title = "Tagged thing") =>
        (await host.Tasks.CaptureAsync(new(Op(), string.Empty, title, null))).Task;

    private static async Task<TagRecord> TagAsync(DataHost host, string path)
    {
        var tags = new TagCommands(host.Store, host.Ids, host.Clock);
        return (await tags.CreateAsync(new(Op(), path, null, null))).Tag;
    }

    private static async Task LinkAsync(DataHost host, TagTargetKind kind, string targetId, string tagId)
    {
        var tags = new TagCommands(host.Store, host.Ids, host.Clock);
        await tags.LinkAsync(new(Op(), kind, targetId, tagId, null));
    }

    private static async Task UnlinkAsync(DataHost host, TagTargetKind kind, string targetId, string tagId)
    {
        var tags = new TagCommands(host.Store, host.Ids, host.Clock);
        await tags.UnlinkAsync(new(Op(), kind, targetId, tagId));
    }

    private static async Task<long> CountAsync(DataHost host, string sql) => await host.CountAsync(sql);

    private static async Task<string> TextAsync(DataHost host, string sql) =>
        await host.Store.QueryAsync(async (session, token) =>
        {
            using var command = ((SqliteDataSession)session).Connection.CreateCommand();
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync(token);
            return value as string ?? throw new InvalidOperationException($"No text result for: {sql}");
        });

    private static async Task<IReadOnlyList<string>> ListPageTagIdsAsync(DataHost host, string pageId) =>
        await host.Store.QueryAsync((session, token) => session.Pages.ListTagIdsAsync(pageId, token));

    [Test]
    public async Task Fresh_root_seeds_exactly_one_device_identity()
    {
        using var host = await DataHost.StartAsync();

        var rows = await CountAsync(host, "SELECT count(*) FROM DeviceSettings WHERE Key = 'DeviceId' AND Value <> '';");

        await Assert.That(rows).IsEqualTo(1L);
    }

    [Test]
    public async Task Removed_tag_link_tombstones_and_relink_restores_the_same_row()
    {
        using var host = await DataHost.StartAsync();
        var task = await CaptureTaskAsync(host);
        var tag = await TagAsync(host, "colour");
        await LinkAsync(host, TagTargetKind.Task, task.Id, tag.Id);
        await UnlinkAsync(host, TagTargetKind.Task, task.Id, tag.Id);

        var tombstoned = await CountAsync(host, $"SELECT count(*) FROM TaskTags WHERE TaskId = '{task.Id}' AND DeletedAtUtc IS NOT NULL;");
        var active = await CountAsync(host, $"SELECT count(*) FROM TaskTags WHERE TaskId = '{task.Id}' AND DeletedAtUtc IS NULL;");
        await Assert.That(tombstoned).IsEqualTo(1L);
        await Assert.That(active).IsEqualTo(0L);

        await LinkAsync(host, TagTargetKind.Task, task.Id, tag.Id);

        var rows = await CountAsync(host, $"SELECT count(*) FROM TaskTags WHERE TaskId = '{task.Id}';");
        var restored = await CountAsync(host, $"SELECT count(*) FROM TaskTags WHERE TaskId = '{task.Id}' AND DeletedAtUtc IS NULL;");
        await Assert.That(rows).IsEqualTo(1L);
        await Assert.That(restored).IsEqualTo(1L);
    }

    [Test]
    public async Task Purge_takes_a_deleted_tags_links_with_it_but_keeps_other_tombstones()
    {
        using var host = await DataHost.StartAsync();
        var task = await CaptureTaskAsync(host);
        var kept = await TagAsync(host, "colour");
        var removed = await TagAsync(host, "someday");
        await LinkAsync(host, TagTargetKind.Task, task.Id, kept.Id);
        await UnlinkAsync(host, TagTargetKind.Task, task.Id, kept.Id);
        await LinkAsync(host, TagTargetKind.Task, task.Id, removed.Id);
        await UnlinkAsync(host, TagTargetKind.Task, task.Id, removed.Id);

        var tags = new TagCommands(host.Store, host.Ids, host.Clock);
        await tags.DeleteAsync(new DeleteTagCommand(Op(), removed.Id, removed.Version), CancellationToken.None);
        await host.Purge.PurgeAsync(null, CancellationToken.None);

        // The purged tag takes its tombstoned link with it; the live tag's tombstoned link
        // stays, because the removal it records still has to sync.
        var purgedTag = await CountAsync(host, $"SELECT count(*) FROM Tags WHERE Id = '{removed.Id}';");
        var purgedLink = await CountAsync(host, $"SELECT count(*) FROM TaskTags WHERE TagId = '{removed.Id}';");
        var keptLink = await CountAsync(host, $"SELECT count(*) FROM TaskTags WHERE TagId = '{kept.Id}' AND DeletedAtUtc IS NOT NULL;");
        await Assert.That(purgedTag).IsEqualTo(0L);
        await Assert.That(purgedLink).IsEqualTo(0L);
        await Assert.That(keptLink).IsEqualTo(1L);
    }

    [Test]
    public async Task Page_front_matter_rewrites_tombstone_removed_tags_in_place()
    {
        using var host = await DataHost.StartAsync();
        var tag = await TagAsync(host, "alpha");
        var pages = new PageCommands(host.Store, host.Ids, host.Clock);
        var page = (await pages.CreateAsync(new CreatePageCommand(Op(), null, "Notes", "---\ntags:\n- alpha\n---\n\nBody"))).Page;

        var tagged = await ListPageTagIdsAsync(host, page.Id);
        await Assert.That(tagged).IsEquivalentTo([tag.Id]);

        await pages.SaveAsync(new SavePageCommand(page.Id, 1, "Notes", "Body without tags"), CancellationToken.None);
        var afterRemoval = await ListPageTagIdsAsync(host, page.Id);
        var tombstoned = await CountAsync(host, $"SELECT count(*) FROM PageTags WHERE PageId = '{page.Id}' AND DeletedAtUtc IS NOT NULL;");
        await Assert.That(afterRemoval).IsEmpty();
        await Assert.That(tombstoned).IsEqualTo(1L);

        await pages.SaveAsync(new SavePageCommand(page.Id, 2, "Notes", "---\ntags:\n- alpha\n---\n\nBody again"), CancellationToken.None);
        var rows = await CountAsync(host, $"SELECT count(*) FROM PageTags WHERE PageId = '{page.Id}';");
        var active = await ListPageTagIdsAsync(host, page.Id);
        await Assert.That(rows).IsEqualTo(1L);
        await Assert.That(active).IsEquivalentTo([tag.Id]);
    }

    [Test]
    public async Task Week_content_replacement_replaces_goal_task_links_with_their_goals()
    {
        using var host = await DataHost.StartAsync();
        var task = await CaptureTaskAsync(host);
        var planId = "wp-test";
        var now = DataHost.Start;
        await host.Store.ExecuteAsync(async (session, token) =>
        {
            using var insert = ((SqliteDataSession)session).Connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO WeekPlans (Id, ProfileId, WeekStartDate, State, ConfirmedRevision, ConfirmedAtUtc, DraftRevision, Version, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($id, $profile, '2026-11-09', 1, 1, $created, 0, 1, $created, $created);
                """;
            insert.Parameters.AddWithValue("$id", planId);
            insert.Parameters.AddWithValue("$profile", host.Storage.PrimaryProfileId);
            insert.Parameters.AddWithValue("$created", StorageFormats.Instant(now));
            await insert.ExecuteNonQueryAsync(token);
            return 0;
        }, CancellationToken.None);

        var goal = new WeekGoalRecord { Id = "goal-1", WeekPlanId = planId, Title = "Paint", Position = 0, TaskIds = [task.Id], Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now };
        await host.Store.ExecuteAsync(async (session, token) => { await session.Weeks.ReplaceContentAsync(planId, [goal], [], token); return 0; }, CancellationToken.None);
        var linked = await CountAsync(host, "SELECT count(*) FROM WeekGoalTasks WHERE GoalId = 'goal-1' AND DeletedAtUtc IS NULL;");
        await Assert.That(linked).IsEqualTo(1L);

        // Clearing the content removes the goals with their links (wholesale replacement); the
        // goals' FK no longer cascades, so the store must take the link rows itself.
        await host.Store.ExecuteAsync(async (session, token) => { await session.Weeks.ReplaceContentAsync(planId, [], [], token); return 0; }, CancellationToken.None);
        var goalRows = await CountAsync(host, "SELECT count(*) FROM WeekGoals WHERE Id = 'goal-1';");
        var linkRows = await CountAsync(host, "SELECT count(*) FROM WeekGoalTasks WHERE GoalId = 'goal-1';");
        await Assert.That(goalRows).IsEqualTo(0L);
        await Assert.That(linkRows).IsEqualTo(0L);
    }

    [Test]
    public async Task Archive_restore_keeps_this_devices_settings()
    {
        using var host = await DataHost.StartAsync();
        var deviceId = await TextAsync(host, "SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';");

        // A minimal archive (one profile row): DeviceSettings is excluded from archives, so the
        // restored file carries none, and the current device's identity must survive the restore.
        await host.Protection.RestoreArchiveAsync(MinimalArchive(), "test-archive.json", CancellationToken.None);

        var restored = await TextAsync(host, "SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';");
        await Assert.That(restored).IsEqualTo(deviceId);
    }

    [Test]
    public async Task Backup_restore_keeps_this_devices_settings()
    {
        using var host = await DataHost.StartAsync();
        var deviceId = await TextAsync(host, "SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';");
        var manifest = await host.Backups.CreateAsync(BackupReason.Manual, CancellationToken.None);

        // Every restore rewrites DeviceSettings with the live file's rows (OpenAndPrepareAsync),
        // so a backup restored onto this data root never changes the device's identity.
        await host.Protection.RestoreBackupAsync(manifest.FileName, manifest.ChecksumSha256, CancellationToken.None);

        var restored = await TextAsync(host, "SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';");
        await Assert.That(restored).IsEqualTo(deviceId);
    }

    [Test]
    public async Task Archives_exclude_device_and_sync_tables()
    {
        using var host = await DataHost.StartAsync();
        var tables = (await host.DumpAsync()).Select(table => table.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var excluded in new[] { "DeviceSettings", "SyncPending", "SyncApplyState", "SyncState", "SyncClocks", "SyncOutbox", "SyncInboxParked", "SyncAttention" })
        {
            await Assert.That(tables.Contains(excluded)).IsFalse();
        }
    }

    private static ArchiveContent MinimalArchive()
    {
        var columns = new[]
        {
            "Id", "DisplayName", "ZoneId", "Locale", "DateFormat", "FirstWeekday", "PlanningTime", "SummaryTime",
            "DefaultReminderTime", "QuietHoursEnabled", "QuietStart", "QuietEnd", "Version", "CreatedAtUtc",
            "UpdatedAtUtc", "DeletedAtUtc", "MorningTime", "WeeklySummaryDay", "CaptureShortcut", "PadShortcut", "KeepRunningInTray",
        };
        object?[] row =
        [
            "profile-archived", "Archived", "UTC", "en-US", "MDY", 0L, null, null,
            "09:00:00", 0L, null, null, 1L, "2026-11-14T08:00:00.0000000Z",
            "2026-11-14T08:00:00.0000000Z", null, null, null, "Ctrl+Alt+N", "Ctrl+Alt+P", 1L,
        ];
        // data.json in the archive's own shape (ArchiveTable): numbers as numbers, nulls as null.
        var data = JsonSerializer.SerializeToUtf8Bytes(new
        {
            tables = new[] { new { name = "Profiles", columns, rows = new[] { row } } },
        });
        var manifest = new ArchiveManifest
        {
            SchemaVersion = 24,
            AppVersion = "0.1.0",
            ExportedAtUtc = DataHost.Start,
            ImportPolicy = JsonArchive.ImportPolicyText,
            Files = [new ArchiveFileEntry(JsonArchive.DataEntry, Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), data.Length)],
            Tables = new Dictionary<string, long> { ["Profiles"] = 1 },
            NotIncluded = [],
        };
        return new ArchiveContent(manifest, [new ArchiveTable("Profiles", columns, [row])]);
    }
}
