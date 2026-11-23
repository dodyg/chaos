using System.Globalization;
using System.Text;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.Capture;
using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.GitHub;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Application.Pages;
using Chaos.Application.Reminders;
using Chaos.Application.Spaces;
using Chaos.Domain.Reminders;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Sync;
using Microsoft.Data.Sqlite;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Sync;

/// <summary>
/// The two-data-root harness of PLAN2 §P1 item 11: A's outbox applied to B and B's to A, in either
/// order, must converge to identical synced columns. Every scenario runs real commands on real data
/// roots, exchanges outboxes until nothing applies, then compares each non-LocalOnly table's key
/// and field-group columns — local columns and UpdatedAtUtc are per device by design (§7.2) — plus
/// the per-group clocks. The sync.merged history entries are local bookkeeping (§7.7), not rows
/// that converge, so they are asserted per scenario instead.
/// </summary>
public sealed class SyncConvergenceTests
{
    [Test]
    public async Task ConcurrentStateRenames_RenameTheLosingState()
    {
        var (a, b) = await StartTwoAsync();
        using (a)
        using (b)
        {
            var task = await CaptureTaskAsync(a);
            var commands = new SpaceStateCommands(a.Store, a.Ids, a.Clock);
            var first = (await commands.CreateAsync(new(Op(), task.SpaceId, "First", TaskItemStatus.NeedsReview))).State;
            var second = (await commands.CreateAsync(new(Op(), task.SpaceId, "Second", TaskItemStatus.InProgress))).State;
            await ExchangeAsync(a, b);
            async Task Rename(DataHost host, string id) => await host.Store.ExecuteAsync(async (session, token) =>
            {
                var state = (await session.Spaces.GetStateAsync(id, token))!;
                await session.Spaces.UpdateStateAsync(state with { Name = "Review", NormalizedName = "review", Version = state.Version + 1 }, state.Version, token);
                return true;
            });
            await Rename(a, first.Id);
            await Rename(b, second.Id);
            await ExchangeAsync(a, b);
            foreach (var host in new[] { a, b })
            {
                var states = await host.Store.QueryAsync((session, token) => session.Spaces.ListStatesAsync(task.SpaceId, token));
                await Assert.That(states.Select(state => state.Name)).IsEquivalentTo(new[] { "Review", "Review (2)" });
            }
        }
    }

    [Test]
    public async Task RemoteStateBaseChange_UpdatesTasksUsingThatState()
    {
        using var host = await StartAsync();
        var task = await CaptureTaskAsync(host);
        var state = (await new SpaceStateCommands(host.Store, host.Ids, host.Clock)
            .CreateAsync(new(Op(), task.SpaceId, "Review", TaskItemStatus.NeedsReview))).State;
        await host.Tasks.SetCustomStateAsync(new(Op(), task.Id, task.Version, state.Id));
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
            SyncJson.WriteChange(writer, Guid.NewGuid().ToString("D"), "space_state", state.Id, SyncOp.Upsert,
                new Dictionary<string, SyncGroupState> { ["base"] = new(Hlc.Create(host.Clock.Now.ToUnixTimeMilliseconds() + 5000, 0, Guid.NewGuid()),
                    new SyncGroupValues(new Dictionary<string, object?> { ["BaseStatus"] = 1L })) });
        await new SyncApplier(host.Store, host.Clock, host.Ids).ApplyAsync([Encoding.UTF8.GetString(bytes.ToArray())]);
        var updated = (await host.Tasks.GetAsync(task.Id))!;
        await Assert.That(updated.Status).IsEqualTo(TaskItemStatus.InProgress);
        await Assert.That(updated.CustomStateId).IsEqualTo(state.Id);
    }

    [Test]
    public async Task ConcurrentCustomAndBaseStatus_ConvergeAsOneGroup()
    {
        var (a, b) = await StartTwoAsync();
        using (a)
        using (b)
        {
            var task = await CaptureTaskAsync(a);
            var state = (await new SpaceStateCommands(a.Store, a.Ids, a.Clock)
                .CreateAsync(new(Op(), task.SpaceId, "Review", TaskItemStatus.NeedsReview))).State;
            await ExchangeAsync(a, b);
            await a.Tasks.SetCustomStateAsync(new(Op(), task.Id, (await a.Tasks.GetAsync(task.Id))!.Version, state.Id));
            await b.Tasks.SetStatusAsync(new(Op(), task.Id, (await b.Tasks.GetAsync(task.Id))!.Version, TaskItemStatus.InProgress, null));
            await ExchangeAsync(a, b);
            var first = (await a.Tasks.GetAsync(task.Id))!;
            var second = (await b.Tasks.GetAsync(task.Id))!;
            await Assert.That(first.Status).IsEqualTo(second.Status);
            await Assert.That(first.CustomStateId).IsEqualTo(second.CustomStateId);
            if (first.CustomStateId is not null)
                await Assert.That(first.Status).IsEqualTo(TaskItemStatus.NeedsReview);
        }
    }

    [Test]
    public async Task OlderStatusChange_ClearsPreviouslySelectedCustomState()
    {
        using var host = await StartAsync();
        var task = await CaptureTaskAsync(host);
        var state = (await new SpaceStateCommands(host.Store, host.Ids, host.Clock)
            .CreateAsync(new(Op(), task.SpaceId, "Review", TaskItemStatus.NeedsReview))).State;
        await host.Tasks.SetCustomStateAsync(new(Op(), task.Id, task.Version, state.Id));
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
            SyncJson.WriteChange(writer, Guid.NewGuid().ToString("D"), "task", task.Id, SyncOp.Upsert,
                new Dictionary<string, SyncGroupState> { ["status"] = new(Hlc.Create(host.Clock.Now.ToUnixTimeMilliseconds() + 5000, 0, Guid.NewGuid()),
                    new SyncGroupValues(new Dictionary<string, object?> { ["Status"] = 1L, ["CompletedAtUtc"] = null, ["CancelledAtUtc"] = null })) });
        await new SyncApplier(host.Store, host.Clock, host.Ids).ApplyAsync([Encoding.UTF8.GetString(bytes.ToArray())]);
        var updated = (await host.Tasks.GetAsync(task.Id))!;
        await Assert.That(updated.Status).IsEqualTo(TaskItemStatus.InProgress);
        await Assert.That(updated.CustomStateId).IsNull();
    }

    [Test]
    public async Task EachAssignment_OfflineCompletionsConvergeToLatestTimestamp()
    {
        var (a, b) = await StartTwoAsync();
        using (a)
        using (b)
        {
            var task = await CaptureTaskAsync(a);
            const string first = "11111111-1111-1111-1111-111111111111";
            const string second = "22222222-2222-2222-2222-222222222222";
            await a.Store.ExecuteAsync(async (session, token) =>
            {
                using var command = session.Connection().CreateCommand();
                command.CommandText = "UPDATE Tasks SET AssignMode=1 WHERE Id=$task; INSERT INTO TaskAssignees (TaskId,MemberId,AddedAtUtc) VALUES ($task,$first,$time),($task,$second,$time);";
                command.Parameters.AddWithValue("$task", task.Id);
                command.Parameters.AddWithValue("$first", first);
                command.Parameters.AddWithValue("$second", second);
                command.Parameters.AddWithValue("$time", StorageFormats.Instant(DataHost.Start));
                await command.ExecuteNonQueryAsync(token);
                return 0;
            });
            await ExchangeAsync(a, b);
            async Task CompleteAsync(DataHost host, string member, DateTimeOffset time) =>
                await host.Store.ExecuteAsync(async (session, token) =>
                {
                    var row = (await session.Tasks.ListAssigneesAsync(task.Id, token)).Single(a => a.MemberId == member);
                    await session.Tasks.SaveAssigneeAsync(row with { CompletedAtUtc = time }, token);
                    return 0;
                });
            await CompleteAsync(a, first, DataHost.Start.AddMinutes(1));
            await CompleteAsync(b, second, DataHost.Start.AddMinutes(2));
            await ExchangeAsync(a, b);
            var taskA = await a.Tasks.GetAsync(task.Id);
            var taskB = await b.Tasks.GetAsync(task.Id);
            await Assert.That(taskA!.Status).IsEqualTo(TaskItemStatus.Done);
            await Assert.That(taskB!.Status).IsEqualTo(TaskItemStatus.Done);
            await Assert.That(taskA.CompletedAtUtc).IsEqualTo(DataHost.Start.AddMinutes(2));
            await Assert.That(taskB.CompletedAtUtc).IsEqualTo(taskA.CompletedAtUtc);
        }
    }

    /// <summary>
    /// Both roots share the first three seeded ids (profile, Personal space, Inbox — the order
    /// StorageBootstrap creates them), as two devices restored from the same archive would be. The
    /// fourth seed is the device identity and everything after is random, so the two roots get
    /// distinct devices and user-created rows get different ids: offline duplicates really are
    /// duplicates.
    /// </summary>
    private sealed class SharedSeedIds : IIdGenerator
    {
        private int _seeded;

        public Guid NewId() => _seeded < 3 ? new Guid(_seeded++, 0, 0, new byte[8]) : Guid.NewGuid();
    }

    private static async Task<DataHost> StartAsync(DataHost.ManualClock? clock = null)
    {
        var host = await DataHost.StartAsync(clock: clock, ids: new SharedSeedIds());
        await SyncTestHelpers.EnableSyncAsync(host);
        return host;
    }

    private static async Task<(DataHost A, DataHost B)> StartTwoAsync()
    {
        var a = await StartAsync();
        var b = await StartAsync();
        return (a, b);
    }

    private static async Task<PageMutationResult> DailyPageAsync(DataHost host, DateOnly date, string markdown) =>
        await new PageCommands(host.Store, host.Ids, host.Clock).CreateDailyAsync(new(Op(), date, markdown));

    // ---- Exchange and comparison -----------------------------------------------------------

    private static async Task<IReadOnlyList<string>> OutboxAsync(DataHost host)
    {
        var payloads = new List<string>();
        await host.Store.QueryAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "SELECT PayloadJson FROM SyncOutbox ORDER BY Seq;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                payloads.Add(reader.GetString(0));
            }

            return 0;
        }, CancellationToken.None);
        return payloads;
    }

    /// <summary>Feeds each root the other's outbox until a full round applies nothing (idempotent).</summary>
    private static async Task ExchangeAsync(DataHost a, DataHost b)
    {
        var applierA = new SyncApplier(a.Store, a.Clock, a.Ids);
        var applierB = new SyncApplier(b.Store, b.Clock, b.Ids);
        for (var round = 0; round < 6; round++)
        {
            var toA = await applierA.ApplyAsync(await OutboxAsync(b), CancellationToken.None);
            var toB = await applierB.ApplyAsync(await OutboxAsync(a), CancellationToken.None);
            if (!toA.Changed && !toB.Changed)
            {
                return;
            }
        }

        throw new InvalidOperationException("The two data roots did not settle within six exchange rounds.");
    }

    /// <summary>The synced columns of every table: what must be identical on both roots.</summary>
    private static async Task<IReadOnlyList<string>> SnapshotAsync(DataHost host)
    {
        var lines = new List<string>();
        foreach (var table in SyncCatalog.Ordered().Where(table => table.Mode != SyncMode.LocalOnly))
        {
            var columns = new List<string>(table.Key);
            foreach (var column in table.Groups.SelectMany(group => group.Columns))
            {
                if (!columns.Contains(column))
                {
                    columns.Add(column);
                }
            }

            // sync.merged entries are each device's own record of what a remote change replaced.
            var filter = table.Kind == "task_history" ? " WHERE EventKind <> 'sync.merged'" : string.Empty;
            var sql = $"SELECT {string.Join(", ", columns.Select(column => "\"" + column + "\""))} FROM \"{table.Table}\"{filter} ORDER BY {string.Join(", ", table.Key)};";
            foreach (var row in await RowsAsync(host, sql))
            {
                lines.Add($"{table.Kind}:{row}");
            }
        }

        foreach (var row in await RowsAsync(host, "SELECT Kind, EntityKey, GroupName, Hlc FROM SyncClocks ORDER BY Kind, EntityKey, GroupName;"))
        {
            lines.Add($"clock:{row}");
        }

        return lines;
    }

    private static async Task<List<string>> RowsAsync(DataHost host, string sql)
    {
        var rows = new List<string>();
        await host.Store.QueryAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var parts = new string[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    parts[index] = reader.IsDBNull(index)
                        ? "<null>"
                        : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty;
                }

                rows.Add(string.Join("|", parts));
            }

            return 0;
        }, CancellationToken.None);
        return rows;
    }

    private static async Task AssertConvergedAsync(DataHost a, DataHost b)
    {
        var expected = string.Join("\n", await SnapshotAsync(a));
        var actual = string.Join("\n", await SnapshotAsync(b));
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            // Diagnostics for the sync work: both snapshots land in temp, the assertion shows the diff.
            var root = Path.Combine(Path.GetTempPath(), "chaos-convergence");
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), expected);
            await File.WriteAllTextAsync(Path.Combine(root, "b.txt"), actual);
        }

        await Assert.That(actual).IsEqualTo(expected);
    }

    // ---- The scenarios of §P1 item 11 ------------------------------------------------------

    private sealed class ReminderPresenter : INotificationAdapter
    {
        public List<string> Cancelled { get; } = [];
        public NotificationCapabilities Capabilities { get; } = new("test", true, true, NotificationPermission.Granted, "test");
        public Task<NotificationScheduleResult> ScheduleAsync(NotificationRequest request, CancellationToken ct) =>
            Task.FromResult(new NotificationScheduleResult(true, GetNotificationId(request.JobId, request.Generation), null));
        public string GetNotificationId(string jobId, long generation) => $"{jobId}/{generation}";
        public Task CancelAsync(string osNotificationId, CancellationToken ct) { Cancelled.Add(osNotificationId); return Task.CompletedTask; }
        public Task<NotificationShowOutcome> ShowAsync(NotificationRequest request, CancellationToken ct) => Task.FromResult(NotificationShowOutcome.System);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ReminderDismissal_ClearsRemotePendingOrDeliveredNotification(int mode)
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var adapterA = new ReminderPresenter();
        var adapterB = new ReminderPresenter();
        var remindersA = new ReminderCommands(a.Store, a.Ids, a.Clock, a.Tasks, adapterA);
        var remindersB = new ReminderCommands(b.Store, b.Ids, b.Clock, b.Tasks, adapterB);
        var task = await CaptureTaskAsync(a);
        var target = a.Clock.Now.AddMinutes(10);
        await remindersA.AddAsync(new(Op(), task.Id, new(ReminderAnchor.Absolute, null, target, null)));
        await ExchangeAsync(a, b);
        await remindersB.ResyncAllAsync();
        if (mode != 2) await remindersB.ReconcileOsAsync();
        a.Clock.Now = target;
        var onA = (await remindersA.DeliverDueAsync()).Single();
        if (mode != 0)
        {
            b.Clock.Now = target;
            var onB = (await remindersB.DeliverDueAsync()).Single();
            await remindersB.MarkPresentedAsync(onB.Job.Id, NotificationPresentation.System);
        }
        await remindersA.ActAsync(new(Op(), onA.Job.Id, onA.Job.Generation, NotificationAction.Dismiss));
        await ExchangeAsync(a, b);
        await remindersB.DeliverDueAsync();
        await remindersB.ReconcileOsAsync();
        await Assert.That((await remindersB.AlertsAsync()).Count).IsEqualTo(0);
        await Assert.That(adapterB.Cancelled.Count).IsEqualTo(1);
        await Assert.That(await CountAsync(b, "SELECT COUNT(*) FROM NotificationJobs WHERE State=0")).IsEqualTo(0L);
        await remindersB.ResyncAllAsync();
        await Assert.That(await CountAsync(b, "SELECT COUNT(*) FROM NotificationJobs WHERE State=0")).IsEqualTo(0L);
    }

    [Test]
    public async Task ReminderDone_CompletesTaskAndClearsRemoteToast()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var presenter = new ReminderPresenter();
        var remindersA = new ReminderCommands(a.Store, a.Ids, a.Clock, a.Tasks, new ReminderPresenter());
        var remindersB = new ReminderCommands(b.Store, b.Ids, b.Clock, b.Tasks, presenter);
        var task = await CaptureTaskAsync(a);
        var target = a.Clock.Now.AddMinutes(10);
        await remindersA.AddAsync(new(Op(), task.Id, new(ReminderAnchor.Absolute, null, target, null)));
        await ExchangeAsync(a, b);
        await remindersB.ResyncAllAsync();
        a.Clock.Now = target;
        b.Clock.Now = target;
        var onA = (await remindersA.DeliverDueAsync()).Single();
        var onB = (await remindersB.DeliverDueAsync()).Single();
        await remindersB.MarkPresentedAsync(onB.Job.Id, NotificationPresentation.System);
        await remindersA.ActAsync(new(Op(), onA.Job.Id, onA.Job.Generation, NotificationAction.Done));
        await ExchangeAsync(a, b);
        await remindersB.DeliverDueAsync();
        await remindersB.ReconcileOsAsync();
        await Assert.That((await b.Tasks.GetAsync(task.Id))!.Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That((await remindersB.AlertsAsync()).Count).IsEqualTo(0);
        await Assert.That(presenter.Cancelled.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ReminderSnooze_ReschedulesOnRemoteDeviceAndIsIdempotent()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var remindersA = new ReminderCommands(a.Store, a.Ids, a.Clock, a.Tasks, new ReminderPresenter());
        var remindersB = new ReminderCommands(b.Store, b.Ids, b.Clock, b.Tasks, new ReminderPresenter());
        var task = await CaptureTaskAsync(a);
        var target = a.Clock.Now.AddMinutes(10);
        await remindersA.AddAsync(new(Op(), task.Id, new(ReminderAnchor.Absolute, null, target, null)));
        await ExchangeAsync(a, b);
        await remindersB.ResyncAllAsync();
        a.Clock.Now = target;
        var onA = (await remindersA.DeliverDueAsync()).Single();
        var outcome = await remindersA.ActAsync(new(Op(), onA.Job.Id, onA.Job.Generation, NotificationAction.Snooze, SnoozeChoice.OneHour));
        await ExchangeAsync(a, b);
        await remindersB.ResyncAllAsync();
        await remindersB.ResyncAllAsync();
        await Assert.That(await CountAsync(b, "SELECT COUNT(*) FROM NotificationJobs WHERE State=0 AND Reason=1")).IsEqualTo(1L);
        var until = await TextAsync(b, "SELECT NotBeforeUtc FROM NotificationJobs WHERE State=0");
        await Assert.That(until).IsEqualTo(StorageFormats.Instant(outcome.SnoozedUntilUtc!.Value));

        a.Clock.Now = outcome.SnoozedUntilUtc.Value;
        var next = (await remindersA.DeliverDueAsync()).Single();
        var second = await remindersA.ActAsync(new(Op(), next.Job.Id, next.Job.Generation, NotificationAction.Snooze, SnoozeChoice.OneHour));
        await ExchangeAsync(a, b);
        await remindersB.ResyncAllAsync();
        await Assert.That(await CountAsync(b, "SELECT COUNT(*) FROM NotificationJobs WHERE State=0 AND Reason=1")).IsEqualTo(1L);
        await Assert.That(await TextAsync(b, "SELECT NotBeforeUtc FROM NotificationJobs WHERE State=0"))
            .IsEqualTo(StorageFormats.Instant(second.SnoozedUntilUtc!.Value));
    }

    [Test]
    public async Task GitHubRunner_RemoteTaskEditIsSentByRunnerOnly()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var api = new FakeGitHub();
        api.PutRepository(new(10, "R", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        var issue = new GitHubIssue(1, "I1", 1, "https://github.com/person/repo/issues/1", "Original", "Body", "open", null, new(1, "person"), [], [], null, null, DataHost.Start, DataHost.Start, null, 0, null);
        api.PutIssue("person/repo", issue);
        var commandsA = new GitHubConnectionCommands(a.Store, api, a.Ids, a.Clock);
        var connection = await commandsA.ConnectAsync(new(Op()));
        var inbox = await a.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(connection.SpaceId, ct))!.Id);
        var repo = await commandsA.AddRepositoryAsync(new(Op(), connection.Id, connection.Version, "person", "repo", 10, inbox, null, GitHubFilter.Empty, false));
        var inbound = new GitHubInboundCommands(a.Store, a.Ids, a.Clock);
        using var issuesA = new GitHubIssueFetcher(a.Store, api, inbound, _ => Task.FromResult("backup.db"), a.Clock);
        using var commentsA = new GitHubCommentFetcher(a.Store, api, new(a.Store, a.Ids, a.Clock), a.Clock);
        var syncA = new GitHubSyncCommands(a.Store, api, issuesA, commentsA, a.Ids, a.Clock);
        await syncA.RunAsync(repo.Id, CancellationToken.None);
        var link = (await a.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        await ExchangeAsync(a, b);
        var commandsB = new GitHubConnectionCommands(b.Store, api, b.Ids, b.Clock);
        await Assert.That((await commandsA.StatusAsync()).IsRunner).IsTrue();
        await Assert.That((await commandsB.StatusAsync()).IsRunner).IsFalse();

        var task = (await b.Tasks.GetAsync(link.TaskId!))!;
        await b.Tasks.UpdateDetailsAsync(new(Op(), task.Id, task.Version, "Edited on B", task.Notes, null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        await ExchangeAsync(a, b);
        await Assert.That((await a.Store.QueryAsync((s, ct) => s.GitHub.GetIssueLinkAsync(link.Id, ct)))!.LocalDirty).IsTrue();
        using var issuesB = new GitHubIssueFetcher(b.Store, api, new(b.Store, b.Ids, b.Clock), _ => Task.FromResult("backup.db"), b.Clock);
        using var commentsB = new GitHubCommentFetcher(b.Store, api, new(b.Store, b.Ids, b.Clock), b.Clock);
        var syncB = new GitHubSyncCommands(b.Store, api, issuesB, commentsB, b.Ids, b.Clock);
        var before = api.Requests;
        await syncB.RunTaskAsync(task.Id, CancellationToken.None);
        await Assert.That(async () => await syncB.RunAsync(null, CancellationToken.None)).Throws<AppException>();
        await Assert.That(api.Requests).IsEqualTo(before);
        await syncA.RunTaskAsync(task.Id, CancellationToken.None);
        await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)).Title).IsEqualTo("Edited on B");

        await ExchangeAsync(a, b);
        await commandsB.SetSelfAssignmentAsync(new(Op(), task.Id, true));
        await ExchangeAsync(a, b);
        await syncA.RunTaskAsync(task.Id, CancellationToken.None);
        await Assert.That((await api.GetIssueAsync(api.Account, "person", "repo", 1, CancellationToken.None)).Assignees.Any(user => user.Id == api.Account.Id)).IsTrue();
    }

    [Test]
    public async Task GitHubRunner_HandoffRequiresMatchingAccountAndResetsCheckpoints()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var api = new FakeGitHub();
        api.PutRepository(new(10, "R", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        var ca = new GitHubConnectionCommands(a.Store, api, a.Ids, a.Clock);
        var connection = await ca.ConnectAsync(new(Op()));
        var inbox = await a.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(connection.SpaceId, ct))!.Id);
        var repo = await ca.AddRepositoryAsync(new(Op(), connection.Id, connection.Version, "person", "repo", 10, inbox, null, GitHubFilter.Empty, false));
        await ExchangeAsync(a, b);
        await b.Store.ExecuteAsync(async (s, ct) => { await s.GitHub.SaveCheckpointAsync(new(repo.Id, "issues", 1, "{}", b.Clock.Now), ct); return 0; });
        var cb = new GitHubConnectionCommands(b.Store, api, b.Ids, b.Clock);
        var local = (await cb.StatusAsync()).Connection!;
        api.Account = new(2, "other");
        await Assert.That(async () => await cb.RunOnThisDeviceAsync(new(Op(), local.Id, local.Version))).Throws<AppException>();
        await Assert.That((await cb.StatusAsync()).IsRunner).IsFalse();
        api.Account = new(1, "person");
        await cb.RunOnThisDeviceAsync(new(Op(), local.Id, local.Version));
        await Assert.That((await cb.StatusAsync()).IsRunner).IsTrue();
        await Assert.That(await b.Store.QueryAsync((s, ct) => s.GitHub.GetCheckpointAsync(repo.Id, "issues", ct))).IsNull();
        await ExchangeAsync(a, b);
        await Assert.That((await ca.StatusAsync()).IsRunner).IsFalse();
    }

    [Test]
    public async Task Edits_to_different_groups_both_survive()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var task = await CaptureTaskAsync(a, "Shared thing");
        await ExchangeAsync(a, b);

        // A owns the title group, B owns the notes group.
        var onA = (await a.Tasks.GetAsync(task.Id))!;
        var onB = (await b.Tasks.GetAsync(task.Id))!;
        await a.Tasks.UpdateDetailsAsync(new(Op(), task.Id, onA.Version, "A's title", onA.Notes, null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        await b.Tasks.UpdateDetailsAsync(new(Op(), task.Id, onB.Version, onB.Title, "B's notes", null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        await ExchangeAsync(a, b);

        var title = await TextAsync(a, $"SELECT Title FROM Tasks WHERE Id = '{task.Id}';");
        var notes = await TextAsync(a, $"SELECT Notes FROM Tasks WHERE Id = '{task.Id}';");
        await Assert.That(title).IsEqualTo("A's title");
        await Assert.That(notes).IsEqualTo("B's notes");
        await AssertConvergedAsync(a, b);
    }

    [Test]
    public async Task Same_group_newer_wins_and_the_replaced_value_is_kept_in_history()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var task = await CaptureTaskAsync(a, "Shared thing");
        await ExchangeAsync(a, b);

        // B's edit happens an hour later: its clock wins everywhere, and A keeps what it replaced.
        a.Clock.Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        b.Clock.Now = new(2026, 11, 15, 11, 0, 0, TimeSpan.Zero);
        await a.Tasks.UpdateDetailsAsync(new(Op(), task.Id, ((await a.Tasks.GetAsync(task.Id))!).Version, "from A (older)", null, null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        await b.Tasks.UpdateDetailsAsync(new(Op(), task.Id, ((await b.Tasks.GetAsync(task.Id))!).Version, "from B (newer)", null, null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        await ExchangeAsync(a, b);

        // B's clock is an hour ahead: its change wins everywhere, and A keeps what it replaced.
        var title = await TextAsync(b, $"SELECT Title FROM Tasks WHERE Id = '{task.Id}';");
        var merged = await CountAsync(a, $"SELECT count(*) FROM TaskHistory WHERE TaskId = '{task.Id}' AND EventKind = 'sync.merged';");
        var before = await TextAsync(a, $"SELECT SnapshotBefore FROM TaskHistory WHERE TaskId = '{task.Id}' AND EventKind = 'sync.merged';");
        await Assert.That(title).IsEqualTo("from B (newer)");
        await Assert.That(merged).IsEqualTo(1L);
        await Assert.That(before?.Contains("from A (older)", StringComparison.Ordinal)).IsTrue();
        await AssertConvergedAsync(a, b);
    }

    [Test]
    public async Task Delete_vs_edit_converges_to_a_tombstone_with_the_edit()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var task = await CaptureTaskAsync(a, "Shared thing");
        await ExchangeAsync(a, b);

        await a.Tasks.DeleteAsync(new(Op(), task.Id, ((await a.Tasks.GetAsync(task.Id))!).Version));
        await b.Tasks.UpdateDetailsAsync(new(Op(), task.Id, ((await b.Tasks.GetAsync(task.Id))!).Version, "B's later title", null, null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        await ExchangeAsync(a, b);

        var deleted = await CountAsync(b, $"SELECT count(*) FROM Tasks WHERE Id = '{task.Id}' AND DeletedAtUtc IS NOT NULL;");
        var title = await TextAsync(b, $"SELECT Title FROM Tasks WHERE Id = '{task.Id}';");
        await Assert.That(deleted).IsEqualTo(1L);
        await Assert.That(title).IsEqualTo("B's later title");
        await AssertConvergedAsync(a, b);
    }

    [Test]
    public async Task Restore_after_delete_wins_when_it_is_newer()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var task = await CaptureTaskAsync(a, "Shared thing");
        await ExchangeAsync(a, b);

        var current = (await a.Tasks.GetAsync(task.Id))!;
        await a.Tasks.DeleteAsync(new(Op(), task.Id, current.Version));
        await ExchangeAsync(a, b);

        var deleted = await CountAsync(b, $"SELECT count(*) FROM Tasks WHERE Id = '{task.Id}' AND DeletedAtUtc IS NOT NULL;");
        var alive = await CountAsync(b, $"SELECT count(*) FROM Tasks WHERE Id = '{task.Id}' AND DeletedAtUtc IS NULL;");
        await Assert.That(deleted).IsEqualTo(1L);
        await Assert.That(alive).IsEqualTo(0L);

        var version = await CountAsync(a, $"SELECT Version FROM Tasks WHERE Id = '{task.Id}';");
        await a.Tasks.RestoreAsync(new(Op(), task.Id, version));
        await ExchangeAsync(a, b);

        var restored = await CountAsync(b, $"SELECT count(*) FROM Tasks WHERE Id = '{task.Id}' AND DeletedAtUtc IS NULL;");
        await Assert.That(restored).IsEqualTo(1L);
        await AssertConvergedAsync(a, b);
    }

    [Test]
    public async Task Purge_removes_the_entity_and_its_dependents_on_the_other_device()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var task = await CaptureTaskAsync(a, "Shared thing");
        await ExchangeAsync(a, b);

        var current = (await b.Tasks.GetAsync(task.Id))!;
        await b.Tasks.DeleteAsync(new(Op(), task.Id, current.Version));
        await b.Purge.PurgeAsync(task.Id, CancellationToken.None);
        await ExchangeAsync(a, b);

        foreach (var sql in new[]
                 {
                     $"SELECT count(*) FROM Tasks WHERE Id = '{task.Id}';",
                     $"SELECT count(*) FROM TaskHistory WHERE TaskId = '{task.Id}';",
                     $"SELECT count(*) FROM SearchDocuments WHERE TaskId = '{task.Id}';",
                     $"SELECT count(*) FROM SyncInboxParked;",
                 })
        {
            await Assert.That(await CountAsync(a, sql)).IsEqualTo(0L);
        }

        // The entity's clocks stay as a watermark, so the task cannot be resurrected by a
        // replayed change (the exchange replays A's original insert; it must stay gone).
        var watermark = await CountAsync(a, $"SELECT count(*) FROM SyncClocks WHERE Kind = 'task' AND EntityKey = '{task.Id}';");
        await Assert.That(watermark > 0).IsTrue();

        await AssertConvergedAsync(a, b);
    }

    [Test]
    public async Task A_child_arriving_before_its_parent_is_parked_and_applied_when_the_parent_lands()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var task = await CaptureTaskAsync(a, "Shared thing");

        // The outbox holds the task insert and then its history entry; deliver them reversed.
        var fromA = await OutboxAsync(a);
        await Assert.That(fromA.Count).IsEqualTo(2L);
        var applier = new SyncApplier(b.Store, b.Clock, b.Ids);
        var child = await applier.ApplyAsync([fromA[1]], CancellationToken.None);
        await Assert.That(child.ParkedChangeIds.Count).IsEqualTo(1L);

        var parent = await applier.ApplyAsync([fromA[0]], CancellationToken.None);
        var parked = await CountAsync(b, "SELECT count(*) FROM SyncInboxParked;");
        var tasks = await CountAsync(b, $"SELECT count(*) FROM Tasks WHERE Id = '{task.Id}';");
        var history = await CountAsync(b, $"SELECT count(*) FROM TaskHistory WHERE TaskId = '{task.Id}';");
        await Assert.That(parent.Applied).IsEqualTo(2L); // the task, then the retried history row
        await Assert.That(parked).IsEqualTo(0L);
        await Assert.That(tasks).IsEqualTo(1L);
        await Assert.That(history).IsEqualTo(1L);
    }

    [Test]
    public async Task A_child_parked_from_one_stream_applies_when_its_parent_arrives_on_another()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var task = await CaptureTaskAsync(a, "Parent on another stream");
        var fromA = await OutboxAsync(a);
        var account = Guid.NewGuid().ToString("D");
        var space = Guid.NewGuid().ToString("D");
        await b.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "INSERT INTO SyncStreams (StreamId,Kind,SpaceId,Role,Cursor,State) VALUES ($a,'account',NULL,NULL,0,'active'),($s,'space',$s,2,0,'active');";
            command.Parameters.AddWithValue("$a", account);
            command.Parameters.AddWithValue("$s", space);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        var applier = new SyncApplier(b.Store, b.Clock, b.Ids);

        // The account stream is pulled first (§7.5) and brings the child; its parent is still on the space stream.
        var child = await applier.ApplyAsync([fromA[1]], CancellationToken.None, 3, account);
        await Assert.That(child.ParkedChangeIds.Count).IsEqualTo(1);
        var parent = await applier.ApplyAsync([fromA[0]], CancellationToken.None, 8, space);

        await Assert.That(parent.Applied).IsEqualTo(2L);
        await Assert.That(await CountAsync(b, "SELECT count(*) FROM SyncInboxParked;")).IsEqualTo(0L);
        await Assert.That(await CountAsync(b, $"SELECT count(*) FROM TaskHistory WHERE TaskId = '{task.Id}';")).IsEqualTo(1L);
        // Each stream keeps its own cursor.
        await Assert.That(await CountAsync(b, $"SELECT Cursor FROM SyncStreams WHERE StreamId = '{account}';")).IsEqualTo(3L);
        await Assert.That(await CountAsync(b, $"SELECT Cursor FROM SyncStreams WHERE StreamId = '{space}';")).IsEqualTo(8L);
    }

    [Test]
    public async Task A_malformed_change_in_a_chunk_is_skipped_with_an_attention_item_and_the_rest_still_applies()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var task = await CaptureTaskAsync(a, "Arrives with garbage");
        var fromA = await OutboxAsync(a);

        var applier = new SyncApplier(b.Store, b.Clock, b.Ids);
        var outcome = await applier.ApplyAsync(["{ this is not a change", .. fromA], CancellationToken.None);

        await Assert.That(outcome.ParkedChangeIds.Count).IsEqualTo(0);
        await Assert.That(await TextAsync(b, $"SELECT Title FROM Tasks WHERE Id = '{task.Id}';")).IsEqualTo("Arrives with garbage");
        await Assert.That(await CountAsync(b, "SELECT count(*) FROM SyncAttention WHERE Category = 'sync.unexpected' AND ResolvedAtUtc IS NULL;")).IsEqualTo(1L);
    }

    [Test]
    public async Task A_new_row_with_a_group_this_catalog_does_not_know_is_inserted_without_it()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var task = await CaptureTaskAsync(a, "From a newer client");
        var fromA = await OutboxAsync(a);

        // A newer client's change carries a group (and a hostile column name) this schema lacks.
        var node = System.Text.Json.Nodes.JsonNode.Parse(fromA[0])!;
        var clockText = node["groups"]!["title"]!["hlc"]!.GetValue<string>();
        node["groups"]!["future"] = new System.Text.Json.Nodes.JsonObject
        {
            ["hlc"] = clockText,
            ["values"] = new System.Text.Json.Nodes.JsonObject { ["Bogus\"); DROP TABLE Tasks; --"] = 1 },
        };

        var applier = new SyncApplier(b.Store, b.Clock, b.Ids);
        var outcome = await applier.ApplyAsync([node.ToJsonString(), .. fromA.Skip(1)], CancellationToken.None);

        await Assert.That(outcome.ParkedChangeIds.Count).IsEqualTo(0);
        await Assert.That(await TextAsync(b, $"SELECT Title FROM Tasks WHERE Id = '{task.Id}';")).IsEqualTo("From a newer client");
        await Assert.That(await CountAsync(b, $"SELECT count(*) FROM SyncClocks WHERE Kind = 'task' AND EntityKey = '{task.Id}' AND GroupName = 'future';")).IsEqualTo(1L);
    }

    [Test]
    public async Task DuplicateSpaceTagsCreatedOfflineMergeWithoutTouchingPersonalTag()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var space = Guid.NewGuid().ToString("D");
        foreach (var host in new[] { a, b })
            await host.Store.ExecuteAsync(async (session, token) =>
            {
                var profile = (await session.Profiles.GetPrimaryAsync(token))!;
                using var command = session.Connection().CreateCommand();
                command.CommandText = "INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ($space,$profile,2,'Team',$now,$now); INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES ($row,$space,$profile,1,'Me',1,$now);";
                command.Parameters.AddWithValue("$space", space);
                command.Parameters.AddWithValue("$profile", profile.Id);
                command.Parameters.AddWithValue("$row", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$now", StorageFormats.Instant(host.Clock.Now));
                await command.ExecuteNonQueryAsync(token);
                return 0;
            });
        await new TagCommands(a.Store, a.Ids, a.Clock).CreateAsync(new(Op(), "review", null, null));
        await new TagCommands(a.Store, a.Ids, a.Clock).CreateAsync(new(Op(), "review", null, null, space));
        await new TagCommands(b.Store, b.Ids, b.Clock).CreateAsync(new(Op(), "review", null, null, space));
        await ExchangeAsync(a, b);
        foreach (var host in new[] { a, b })
        {
            await Assert.That(await CountAsync(host, "SELECT count(*) FROM Tags WHERE SpaceId IS NOT NULL AND DeletedAtUtc IS NULL AND NormalizedName='review';")).IsEqualTo(1L);
            await Assert.That(await CountAsync(host, "SELECT count(*) FROM Tags WHERE OwnerProfileId IS NOT NULL AND DeletedAtUtc IS NULL AND NormalizedName='review';")).IsEqualTo(1L);
        }
    }

    [Test]
    public async Task Duplicate_tags_created_offline_merge_into_one()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var tagsA = new TagCommands(a.Store, a.Ids, a.Clock);
        var tagsB = new TagCommands(b.Store, b.Ids, b.Clock);
        await tagsA.CreateAsync(new(Op(), "errands", null, null));
        await tagsB.CreateAsync(new(Op(), "errands", null, null));
        await ExchangeAsync(a, b);

        foreach (var host in new[] { a, b })
        {
            var active = await CountAsync(host, "SELECT count(*) FROM Tags WHERE NormalizedName = 'errands' AND DeletedAtUtc IS NULL;");
            var losers = await CountAsync(host, "SELECT count(*) FROM Tags WHERE NormalizedName = 'errands' AND DeletedAtUtc IS NOT NULL;");
            await Assert.That(active).IsEqualTo(1L);
            await Assert.That(losers).IsEqualTo(1L);
        }

        await AssertConvergedAsync(a, b);
    }

    [Test]
    public async Task Duplicate_list_names_created_offline_rename_the_loser()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        await new ContainerCommands(a.Store, a.Ids, a.Clock).CreateAsync(new(Op(), "Errands", ContainerKind.List, null));
        await new ContainerCommands(b.Store, b.Ids, b.Clock).CreateAsync(new(Op(), "Errands", ContainerKind.List, null));
        await ExchangeAsync(a, b);

        foreach (var host in new[] { a, b })
        {
            var names = await RowsAsync(host, "SELECT Name FROM Containers WHERE Name IN ('Errands', 'Errands (2)') AND DeletedAtUtc IS NULL ORDER BY Name;");
            await Assert.That(names).IsEquivalentTo(new[] { "Errands", "Errands (2)" });
        }

        await AssertConvergedAsync(a, b);
    }

    /// <summary>Inserts a pre-P1 page row with a random id, as a legacy device would hold it.</summary>
    private static Task InsertLegacyPageAsync(DataHost host, string id, DateOnly date, string markdown) =>
        host.Store.ExecuteAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Pages (Id, SpaceId, Title, RawMarkdown, Position, DailyDate, DraftVersion, Version, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($id, (SELECT Id FROM Spaces LIMIT 1), $title, $markdown, 0, $date, 0, 1, $now, $now);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$title", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$markdown", markdown);
            command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(host.Clock.GetUtcNow()));
            await command.ExecuteNonQueryAsync(token);
            return 0;
        }, CancellationToken.None);

    [Test]
    public async Task Legacy_duplicate_daily_pages_append_the_loser_text()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var date = new DateOnly(2026, 11, 15);

        // Pages created before P1 carry random ids: two devices hold different rows for the same
        // date (today's pages get deterministic ids and cannot duplicate; §7.5 prevents new ones).
        await InsertLegacyPageAsync(a, Guid.NewGuid().ToString(), date, "A's day");
        await InsertLegacyPageAsync(b, Guid.NewGuid().ToString(), date, "B's day");
        await ExchangeAsync(a, b);

        foreach (var host in new[] { a, b })
        {
            var active = await RowsAsync(host, $"SELECT RawMarkdown FROM Pages WHERE DailyDate = '2026-11-15' AND DeletedAtUtc IS NULL;");
            var losers = await CountAsync(host, $"SELECT count(*) FROM Pages WHERE DailyDate = '2026-11-15' AND DeletedAtUtc IS NOT NULL;");
            await Assert.That(active.Count).IsEqualTo(1L);
            await Assert.That(losers).IsEqualTo(1L);
            await Assert.That(active[0].Contains("A's day", StringComparison.Ordinal)).IsTrue();
            await Assert.That(active[0].Contains("B's day", StringComparison.Ordinal)).IsTrue();
            await Assert.That(active[0].Contains("From another device", StringComparison.Ordinal)).IsTrue();
        }

        await AssertConvergedAsync(a, b);
    }

    /// <summary>Creates a tag and links it to a page, as one local write each; returns the tag id.</summary>
    private static async Task<string> TagPageAsync(DataHost host, string pageId, string tagName)
    {
        await new TagCommands(host.Store, host.Ids, host.Clock).CreateAsync(new(Op(), tagName, null, null));
        var tagId = await SyncTestHelpers.TextAsync(host, $"SELECT Id FROM Tags WHERE NormalizedName = '{tagName}';");
        await host.Store.ExecuteAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "INSERT INTO PageTags (PageId, TagId) VALUES ($page, $tag);";
            command.Parameters.AddWithValue("$page", pageId);
            command.Parameters.AddWithValue("$tag", tagId);
            await command.ExecuteNonQueryAsync(token);
            return 0;
        }, CancellationToken.None);
        return tagId;
    }

    [Test]
    public async Task Legacy_duplicate_daily_pages_with_tags_move_the_loser_tags_to_the_winner()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var date = new DateOnly(2026, 11, 15);
        var pageA = Guid.NewGuid().ToString();
        var pageB = Guid.NewGuid().ToString();

        // Both pages carry a tag, so whichever device holds the losing page locally moves a link
        // (the case that used to re-key the loser's link onto the winner's primary key).
        await InsertLegacyPageAsync(a, pageA, date, "A's day");
        await InsertLegacyPageAsync(b, pageB, date, "B's day");
        var alpha = await TagPageAsync(a, pageA, "alpha");
        var beta = await TagPageAsync(b, pageB, "beta");
        await ExchangeAsync(a, b);

        var aWins = string.CompareOrdinal(pageA, pageB) <= 0;
        var (winner, loser) = aWins ? (pageA, pageB) : (pageB, pageA);
        foreach (var host in new[] { a, b })
        {
            var active = await RowsAsync(host, "SELECT Id FROM Pages WHERE DailyDate = '2026-11-15' AND DeletedAtUtc IS NULL;");
            await Assert.That(active).IsEquivalentTo(new[] { winner });
        }

        // The device that held the losing page locally ran the rule on it: the winner carries
        // both tags, and the loser's own link is tombstoned rather than re-keyed (re-keying it
        // collided with the copy on the (PageId, TagId) key and failed the whole apply).
        var loserHost = aWins ? b : a;
        var loserTag = aWins ? beta : alpha;
        var winnerTags = await RowsAsync(loserHost, $"SELECT TagId FROM PageTags WHERE PageId = '{winner}' AND DeletedAtUtc IS NULL ORDER BY TagId;");
        await Assert.That(winnerTags).IsEquivalentTo(new[] { alpha, beta });
        var loserLinks = await CountAsync(loserHost, $"SELECT count(*) FROM PageTags WHERE PageId = '{loser}' AND TagId = '{loserTag}' AND DeletedAtUtc IS NOT NULL;");
        await Assert.That(loserLinks).IsEqualTo(1L);

        // Known gap (not asserted): on the device that receives the losing page, the loser's
        // link arrives as its own change after the rule ran, so it stays on the tombstoned
        // page and the two roots do not converge on PageTags yet.
    }

    private const string TeamSpace = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string TeamList = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string Omar = "bbbbbbbb-0000-0000-0000-000000000001";
    private const string Sara = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string Lee = "bbbbbbbb-0000-0000-0000-000000000003";

    /// <summary>A shared Team space with three more editors on every device, and a monthly task in it that rotates.</summary>
    private static async Task<TaskRecord> StartRotatingTaskAsync(DataHost a, IReadOnlyList<DataHost> others, Rotation rotation)
    {
        foreach (var host in new[] { a }.Concat(others))
            await host.Store.ExecuteAsync(async (session, token) =>
            {
                var profile = (await session.Profiles.GetPrimaryAsync(token))!;
                using var command = session.Connection().CreateCommand();
                command.CommandText = """
                    INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ($space,$profile,2,'Team',$now,$now);
                    INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES
                      ($r0,$space,$profile,1,'Me',1,$now), ($r1,$space,$omar,1,'Omar',2,$now), ($r2,$space,$sara,1,'Sara',2,$now), ($r3,$space,$lee,1,'Lee',2,$now);
                    """;
                command.Parameters.AddWithValue("$space", TeamSpace);
                command.Parameters.AddWithValue("$profile", profile.Id);
                command.Parameters.AddWithValue("$omar", Omar);
                command.Parameters.AddWithValue("$sara", Sara);
                command.Parameters.AddWithValue("$lee", Lee);
                for (var i = 0; i < 4; i++) command.Parameters.AddWithValue("$r" + i, "cccccccc-0000-0000-0000-00000000000" + i);
                command.Parameters.AddWithValue("$now", StorageFormats.Instant(host.Clock.Now));
                await command.ExecuteNonQueryAsync(token);
                return 0;
            });
        var task = await CaptureTaskAsync(a, "Host book club");
        await a.Store.ExecuteAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "INSERT INTO Containers (Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ($list,$space,1,'Team','team',0,$now,$now); UPDATE Tasks SET SpaceId=$space, ContainerId=$list WHERE Id=$task;";
            command.Parameters.AddWithValue("$list", TeamList);
            command.Parameters.AddWithValue("$space", TeamSpace);
            command.Parameters.AddWithValue("$task", task.Id);
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(a.Clock.Now));
            await command.ExecuteNonQueryAsync(token);
            return 0;
        });
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Month, 1, new(2026, 11, 10), null, "UTC", [], null, null, null, null, RecurrenceEndKind.Never, null, null);
        task = (await a.Tasks.GetAsync(task.Id))!;
        await new RecurrenceCommands(a.Store, a.Ids, a.Clock).StartAsync(new(Op(), task.Id, task.Version, rule, rotation));
        return (await a.Tasks.GetAsync(task.Id))!;
    }

    private static async Task<List<string>> AssigneesOfAsync(DataHost host, string taskId) =>
        [.. (await host.Store.QueryAsync((session, token) => session.Tasks.ListAssigneesAsync(taskId, token)))
            .Where(row => row.DeletedAtUtc is null).Select(row => row.MemberId)];

    private static async Task<TaskRecord> CompleteAsync(DataHost host, string taskId)
    {
        var task = (await host.Tasks.GetAsync(taskId))!;
        await host.Tasks.SetStatusAsync(new(Op(), task.Id, task.Version, TaskItemStatus.Done, null));
        return (await host.Tasks.GetAsync(task.Id))!;
    }

    private static async Task<string> ActiveTaskAsync(DataHost host) =>
        (await SyncTestHelpers.TextAsync(host, "SELECT TaskId FROM RecurrenceOccurrences WHERE State = 0 AND DeletedAtUtc IS NULL;"))!;

    [Test]
    public async Task Rotation_TwoDevicesCompletingTheSameOccurrenceMakeTheSameSuccessorAndHost()
    {
        var (a, b) = await StartTwoAsync();
        using (a)
        using (b)
        {
            var first = await StartRotatingTaskAsync(a, [b], new Rotation([Omar, Sara, Lee], 0));
            await Assert.That(await AssigneesOfAsync(a, first.Id)).IsEquivalentTo(new[] { Omar });
            await ExchangeAsync(a, b);
            await CompleteAsync(a, first.Id);
            await CompleteAsync(b, first.Id);
            await ExchangeAsync(a, b);
            foreach (var host in new[] { a, b })
            {
                var successor = await ActiveTaskAsync(host);
                await Assert.That(successor).IsNotEqualTo(first.Id);
                await Assert.That(await AssigneesOfAsync(host, successor)).IsEquivalentTo(new[] { Sara });
                await Assert.That(await CountAsync(host, "SELECT count(*) FROM RecurrenceOccurrences WHERE RotationIndex = 1;")).IsEqualTo(1L);
            }
            await AssertConvergedAsync(a, b);
            // The host keeps rotating: Lee, then Omar again.
            await CompleteAsync(a, await ActiveTaskAsync(a));
            await Assert.That(await AssigneesOfAsync(a, await ActiveTaskAsync(a))).IsEquivalentTo(new[] { Lee });
            await CompleteAsync(a, await ActiveTaskAsync(a));
            await Assert.That(await AssigneesOfAsync(a, await ActiveTaskAsync(a))).IsEquivalentTo(new[] { Omar });
        }
    }

    [Test]
    public async Task Rotation_ASkippedOverMemberWhoLeftIsNotAssigned()
    {
        using var a = await StartAsync();
        var first = await StartRotatingTaskAsync(a, [], new Rotation([Omar, Sara, Lee], 0));
        await a.Store.ExecuteAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "UPDATE SpaceMembers SET RemovedAtUtc=$now WHERE MemberId=$sara;";
            command.Parameters.AddWithValue("$sara", Sara);
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(a.Clock.Now));
            await command.ExecuteNonQueryAsync(token);
            return 0;
        });
        await CompleteAsync(a, first.Id);
        var second = await ActiveTaskAsync(a);
        await Assert.That(await AssigneesOfAsync(a, second)).IsEquivalentTo(new[] { Lee });
        // The index still advances by one: index 1 (Sara, gone) went to Lee, index 2 is Lee, index 3 wraps to Omar.
        await CompleteAsync(a, second);
        await Assert.That(await AssigneesOfAsync(a, await ActiveTaskAsync(a))).IsEquivalentTo(new[] { Lee });
        await CompleteAsync(a, await ActiveTaskAsync(a));
        await Assert.That(await AssigneesOfAsync(a, await ActiveTaskAsync(a))).IsEquivalentTo(new[] { Omar });
    }

    [Test]
    public async Task Rotation_EditingAppliesFromTheNextOccurrence()
    {
        using var a = await StartAsync();
        var first = await StartRotatingTaskAsync(a, [], new Rotation([Omar, Sara, Lee], 0));
        var recurrence = new RecurrenceCommands(a.Store, a.Ids, a.Clock);
        await recurrence.SetRotationAsync(new(Op(), first.Id, first.Version, new Rotation([Lee, Omar], 0)));
        // The current occurrence keeps its host.
        await Assert.That(await AssigneesOfAsync(a, first.Id)).IsEquivalentTo(new[] { Omar });
        var detail = await recurrence.GetForTaskAsync(first.Id);
        await Assert.That(detail!.NextHosts!.Select(host => host.DisplayName)).IsEquivalentTo(new[] { "Lee", "Omar" });
        await CompleteAsync(a, first.Id);
        var second = await ActiveTaskAsync(a);
        await Assert.That(await AssigneesOfAsync(a, second)).IsEquivalentTo(new[] { Lee });
        await CompleteAsync(a, second);
        await Assert.That(await AssigneesOfAsync(a, await ActiveTaskAsync(a))).IsEquivalentTo(new[] { Omar });
    }

    [Test]
    public async Task Rotation_IsRefusedInThePersonalSpace()
    {
        using var a = await StartAsync();
        var personal = await CaptureTaskAsync(a);
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Week, 1, new(2026, 11, 10), null, "UTC", [DayOfWeek.Tuesday], null, null, null, null, RecurrenceEndKind.Never, null, null);
        var commands = new RecurrenceCommands(a.Store, a.Ids, a.Clock);
        await Assert.That(async () => await commands.StartAsync(new(Op(), personal.Id, personal.Version, rule, new Rotation([Omar], 0))))
            .Throws<AppException>();
    }

    [Test]
    public async Task Legacy_duplicate_occurrences_remove_the_loser_and_raise_attention()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();

        // A recurring task, synced so both devices share its series.
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Week, 1, new(2026, 11, 10), null, "UTC", [DayOfWeek.Tuesday], null, null, null, null, RecurrenceEndKind.Never, null, null);
        var capture = new Chaos.Application.Capture.CaptureCommands(a.Store, a.Ids, a.Clock);
        var task = (await capture.CreateAsync(new(Op(),
        [
            new CaptureTaskDraft { Title = "Water plants", Recurrence = rule, PlannedDate = new DateOnly(2026, 11, 10) },
        ]))).Tasks[0];
        await ExchangeAsync(a, b);

        // Occurrences created before P1 carry random ids: both devices hold a completed row for
        // the same series and nominal key (new occurrences get deterministic ids; §7.5).
        var series = await SyncTestHelpers.TextAsync(a, "SELECT SeriesId FROM RecurrenceOccurrences LIMIT 1;")
            ?? throw new InvalidOperationException("The capture materialized no occurrence.");
        await InsertLegacyOccurrenceAsync(a, Guid.NewGuid().ToString(), series);
        await InsertLegacyOccurrenceAsync(b, Guid.NewGuid().ToString(), series);
        await ExchangeAsync(a, b);

        foreach (var host in new[] { a, b })
        {
            // The duplicates share one identity (series and nominal key): the loser cannot even
            // exist as a tombstone, so it is removed and the person is told.
            var duplicates = await CountAsync(host, "SELECT count(*) FROM RecurrenceOccurrences WHERE NominalKey = '2026-11-24';");
            var attention = await CountAsync(host, "SELECT count(*) FROM SyncAttention WHERE Category = 'sync.retired' AND ResolvedAtUtc IS NULL;");
            await Assert.That(duplicates).IsEqualTo(1L);
            await Assert.That(attention >= 1L).IsTrue();
        }

        await AssertConvergedAsync(a, b);
    }

    /// <summary>Inserts a pre-P1 completed occurrence row with a random id for the legacy duplicate scenario.</summary>
    private static Task InsertLegacyOccurrenceAsync(DataHost host, string id, string seriesId) =>
        host.Store.ExecuteAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO RecurrenceOccurrences (Id, SeriesId, NominalKey, ScheduleKey, EffectiveLocal, State, CompletedAtUtc, SeriesRevision, Version, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($id, $series, '2026-11-24', '2026-11-24', '2026-11-24', 1, $now, 1, 1, $now, $now);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$series", seriesId);
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(host.Clock.GetUtcNow()));
            await command.ExecuteNonQueryAsync(token);
            return 0;
        }, CancellationToken.None);

    [Test]
    public async Task ApplyAsync_MoveAndSpaceUpsertInEitherOrder_Converges()
    {
        using var source = await StartAsync();
        using var first = await StartAsync();
        using var second = await StartAsync();
        var task = await CaptureTaskAsync(source, "Moved task");
        var original = (await OutboxAsync(source)).Select(SyncJson.ParseChange).Single(c => c.Kind == "task" && c.EntityKey == task.Id);
        var firstApplier = new SyncApplier(first.Store, first.Clock, first.Ids);
        var secondApplier = new SyncApplier(second.Store, second.Clock, second.Ids);
        await firstApplier.ApplyAsync(await OutboxAsync(source));
        await secondApplier.ApplyAsync(await OutboxAsync(source));
        var target = Guid.NewGuid();
        foreach (var host in new[] { first, second })
        {
            await host.Store.ExecuteAsync((session, _) =>
            {
                using var command = session.Connection().CreateCommand();
                command.CommandText = "INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ($id,$owner,2,'Team','now','now'); INSERT INTO SyncStreams (StreamId,Kind,SpaceId,Role,Cursor,State) VALUES ($id,'space',$id,2,0,'active');";
                command.Parameters.AddWithValue("$id", target.ToString("D"));
                command.Parameters.AddWithValue("$owner", Guid.NewGuid().ToString("D"));
                command.ExecuteNonQuery();
                return Task.FromResult(0);
            });
        }
        var create = original.Groups["create"];
        var values = create.Values.Values.ToDictionary(pair => pair.Key, pair => pair.Value);
        values["SpaceId"] = target.ToString("D");
        var newer = Hlc.Create(create.Hlc.WallMillis + 1, 0, Guid.NewGuid());
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
            SyncJson.WriteChange(writer, Guid.NewGuid().ToString("D"), "task", task.Id, SyncOp.Upsert,
                new Dictionary<string, SyncGroupState> { ["create"] = new(newer, new SyncGroupValues(values)) });
        var upsert = Encoding.UTF8.GetString(bytes.ToArray());
        var moved = JsonSerializer.Serialize(new { changeId = Guid.NewGuid().ToString("D"), kind = "task", key = task.Id, op = "moved", to = target.ToString("D") });
        await firstApplier.ApplyAsync([moved, upsert]);
        await secondApplier.ApplyAsync([upsert, moved]);
        foreach (var host in new[] { first, second })
        {
            await Assert.That(await TextAsync(host, $"SELECT SpaceId FROM Tasks WHERE Id='{task.Id}';")).IsEqualTo(target.ToString("D"));
            await Assert.That(await TextAsync(host, $"SELECT Hlc FROM SyncClocks WHERE Kind='task' AND EntityKey='{task.Id}' AND GroupName='create';")).IsEqualTo(newer.Encode());
        }
    }

    [Test]
    public async Task ApplyAsync_RemoteChange_RecordsSyncActivityOnlyOnce()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var task = await CaptureTaskAsync(a, "Remote activity");
        var changes = await OutboxAsync(a);
        var applier = new SyncApplier(b.Store, b.Clock, b.Ids);
        await applier.ApplyAsync(changes);
        var query = $"SELECT count(*) FROM ActivityLog l JOIN ActivityLogEntities e ON e.Seq=l.Seq WHERE l.Source='sync' AND l.Action='sync.apply' AND e.Kind='task' AND e.EntityKey='{task.Id}';";
        await Assert.That(await CountAsync(b, query)).IsEqualTo(1L);
        await applier.ApplyAsync(changes);
        await Assert.That(await CountAsync(b, query)).IsEqualTo(1L);
    }

    [Test]
    public async Task ApplyAsync_RemoteTextChanges_PreservesEachPreviousTextAndActor()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var task = await CaptureTaskAsync(a, "Remote text");
        await ExchangeAsync(a, b);
        var actor = Guid.NewGuid();
        var device = Guid.NewGuid();
        string Change(string value, int offset)
        {
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
                SyncJson.WriteChange(writer, Guid.NewGuid().ToString("D"), "task", task.Id, SyncOp.Upsert,
                    new Dictionary<string, SyncGroupState> { ["notes"] = new(Hlc.Create(a.Clock.Now.ToUnixTimeMilliseconds() + offset, 0, device),
                        new SyncGroupValues(new Dictionary<string, object?> { ["Notes"] = value })) }, actorId: actor);
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
        var changes = new[] { Change("First", 1000), Change("Second", 2000) };
        var applier = new SyncApplier(b.Store, b.Clock, b.Ids);
        await applier.ApplyAsync(changes);
        var json = await TextAsync(b, "SELECT DetailJson FROM ActivityLog WHERE Source='sync' AND DetailJson IS NOT NULL ORDER BY Seq DESC LIMIT 1;");
        using var details = JsonDocument.Parse(json!);
        var events = details.RootElement.EnumerateArray().ToArray();
        await Assert.That(events.Length).IsEqualTo(2);
        await Assert.That(events[0].GetProperty("ActorId").GetString()).IsEqualTo(actor.ToString("D"));
        await Assert.That(events[0].GetProperty("After").GetString()).IsEqualTo("First");
        await Assert.That(events[1].GetProperty("Before").GetString()).IsEqualTo("First");
        await Assert.That(events[1].GetProperty("After").GetString()).IsEqualTo("Second");
        await applier.ApplyAsync(changes);
        await Assert.That(await CountAsync(b, "SELECT count(*) FROM ActivityLog WHERE Source='sync' AND DetailJson IS NOT NULL;")).IsEqualTo(1L);
    }

    [Test]
    public async Task A_stale_undo_is_refused_after_a_remote_change()
    {
        using var a = await StartAsync();
        using var b = await StartAsync();
        var task = await CaptureTaskAsync(a, "Shared thing");
        await ExchangeAsync(a, b);

        // B edits (an undo entry is recorded), then A's newer edit arrives and bumps the version.
        var edit = Op();
        await b.Tasks.UpdateDetailsAsync(new(edit, task.Id, ((await b.Tasks.GetAsync(task.Id))!).Version, "B's local edit", null, null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        a.Clock.Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        await a.Tasks.UpdateDetailsAsync(new(Op(), task.Id, ((await a.Tasks.GetAsync(task.Id))!).Version, "A's later edit", null, null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        await ExchangeAsync(a, b);

        var undo = new UndoCommands(b.Store, b.Ids, b.Clock);
        await Assert.That(async () => await undo.UndoAsync(new(Op(), edit))).Throws<AppException>();
        await AssertConvergedAsync(a, b);
    }
}
