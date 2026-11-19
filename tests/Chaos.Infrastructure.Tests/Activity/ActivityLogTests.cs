using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Infrastructure.Activity;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Tests.DataProtection;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Activity;

/// <summary>
/// The activity log (PLAN2 §6.4): one row per committed write that changed something, in commit
/// order, written in the writing transaction so a rollback leaves nothing; capture works whether
/// or not sync is enabled; device-only writers add notes; pruning respects cursors and floors.
/// </summary>
public sealed class ActivityLogTests
{
    private static IDisposable User(string action = "tasks.create") => ActivityScope.Open(ActivitySources.User, action);

    private static async Task<string> NewTaskAsync(DataHost host)
    {
        using var scope = User();
        return (await CaptureTaskAsync(host)).Id;
    }

    private static ActivityFeed FeedOf(DataHost host) => new(host.Store, host.Clock);

    [Test]
    public async Task A_commit_writes_one_row_with_its_entities_and_versions()
    {
        using var host = await DataHost.StartAsync();
        using var strict = ActivityScope.UseStrict();
        var before = await FeedOf(host).HeadAsync(CancellationToken.None);

        var id = await NewTaskAsync(host);

        var page = await FeedOf(host).SinceAsync(before, 50, CancellationToken.None);
        await Assert.That(page.Entries.Count).IsEqualTo(1);
        var entry = page.Entries[0];
        await Assert.That(entry.Source).IsEqualTo("user");
        await Assert.That(entry.Action).IsEqualTo("tasks.create");
        await Assert.That(entry.Outcome).IsEqualTo("ok");
        var task = entry.Entities.Single(entity => entity.Kind == "task");
        await Assert.That(task.Key).IsEqualTo(id);
        await Assert.That(task.Op).IsEqualTo("upsert");
        await Assert.That(task.Version).IsEqualTo(1L);
    }

    [Test]
    public async Task A_rollback_writes_no_row()
    {
        using var host = await DataHost.StartAsync();
        using var strict = ActivityScope.UseStrict();
        var id = await NewTaskAsync(host);
        var before = await FeedOf(host).HeadAsync(CancellationToken.None);

        using (User("tasks.edit"))
        {
            await Assert.That(async () => await host.Store.ExecuteAsync<int>(async (session, token) =>
            {
                using var command = session.Connection().CreateCommand();
                command.CommandText = "UPDATE Tasks SET Title = 'changed', Version = Version + 1 WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(token);
                session.Activity.Note("calendar_source", "s", ActivityOp.Upsert);
                throw new InvalidOperationException("boom");
            }, CancellationToken.None)).Throws<AppException>();
        }

        await Assert.That(await FeedOf(host).HeadAsync(CancellationToken.None)).IsEqualTo(before);
    }

    [Test]
    public async Task A_transaction_that_changes_nothing_writes_no_row()
    {
        using var host = await DataHost.StartAsync();
        using var strict = ActivityScope.UseStrict();
        var before = await FeedOf(host).HeadAsync(CancellationToken.None);

        // Unscoped on purpose: nothing changed, so there is nothing to attribute and strict mode must not trip.
        await host.Store.ExecuteAsync((session, _) => Task.FromResult(0), CancellationToken.None);

        await Assert.That(await FeedOf(host).HeadAsync(CancellationToken.None)).IsEqualTo(before);
    }

    [Test]
    public async Task A_hard_delete_becomes_a_delete_entity()
    {
        using var host = await DataHost.StartAsync();
        using var strict = ActivityScope.UseStrict();
        var id = await NewTaskAsync(host);
        var before = await FeedOf(host).HeadAsync(CancellationToken.None);

        using (User("tasks.delete"))
        {
            await host.Store.ExecuteAsync(async (session, token) =>
            {
                var connection = session.Connection();
                var dependents = new List<(string Table, string Column)>();
                using (var find = connection.CreateCommand())
                {
                    find.CommandText = "SELECT m.name, f.\"from\" FROM sqlite_master m, pragma_foreign_key_list(m.name) f WHERE m.type = 'table' AND f.\"table\" = 'Tasks';";
                    using var reader = find.ExecuteReader();
                    while (reader.Read())
                    {
                        dependents.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }

                foreach (var (table, column) in dependents)
                {
                    using var clear = connection.CreateCommand();
                    clear.CommandText = $"DELETE FROM \"{table}\" WHERE \"{column}\" = $id;";
                    clear.Parameters.AddWithValue("$id", id);
                    await clear.ExecuteNonQueryAsync(token);
                }

                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM Tasks WHERE Id = $id;";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync(token);
                return 0;
            }, CancellationToken.None);
        }

        var page = await FeedOf(host).SinceAsync(before, 50, CancellationToken.None);
        var entity = page.Entries.Single().Entities.Single(candidate => candidate.Kind == "task" && candidate.Key == id);
        await Assert.That(entity.Op).IsEqualTo("delete");
        await Assert.That(entity.Version).IsNull();
    }

    [Test]
    public async Task Capture_works_with_sync_disabled_and_enabled_and_leaves_the_outbox_alone()
    {
        using var host = await DataHost.StartAsync();
        var feed = FeedOf(host);

        var head0 = await feed.HeadAsync(CancellationToken.None);
        await NewTaskAsync(host);
        var head1 = await feed.HeadAsync(CancellationToken.None);
        await Assert.That(head1).IsGreaterThan(head0);
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM SyncOutbox;")).IsEqualTo(0L);

        await EnableSyncAsync(host);
        await NewTaskAsync(host);
        var head2 = await feed.HeadAsync(CancellationToken.None);
        await Assert.That(head2).IsGreaterThan(head1);
        // The outbox gets exactly the entities the log names, no more.
        var logged = await CountAsync(host, $"SELECT count(*) FROM ActivityLogEntities WHERE Seq = {head2} AND Kind <> 'task' OR Seq = {head2};");
        var outbox = await CountAsync(host, "SELECT count(*) FROM SyncOutbox;");
        await Assert.That(outbox).IsGreaterThan(0L);
        await Assert.That(logged).IsGreaterThan(0L);
    }

    [Test]
    public async Task Device_only_notes_are_recorded_and_a_note_is_the_only_entity_when_nothing_synced_changed()
    {
        using var host = await DataHost.StartAsync();
        using var strict = ActivityScope.UseStrict();
        var before = await FeedOf(host).HeadAsync(CancellationToken.None);

        using (ActivityScope.Open(ActivitySources.Schedule, ActivityActions.CalendarRefresh))
        {
            await host.Store.ExecuteAsync((session, _) =>
            {
                session.Activity.Note("calendar_source", "source-1", ActivityOp.Upsert, 3);
                return Task.FromResult(0);
            }, CancellationToken.None);
        }

        var page = await FeedOf(host).SinceAsync(before, 50, CancellationToken.None);
        var entry = page.Entries.Single();
        await Assert.That(entry.Source).IsEqualTo("schedule");
        await Assert.That(entry.Action).IsEqualTo("calendar.refresh");
        var entity = entry.Entities.Single();
        await Assert.That(entity.Kind).IsEqualTo("calendar_source");
        await Assert.That(entity.Key).IsEqualTo("source-1");
        await Assert.That(entity.Version).IsEqualTo(3L);
    }

    [Test]
    public async Task Strict_mode_throws_for_an_unscoped_write_and_release_records_it_as_system()
    {
        using var host = await DataHost.StartAsync();
        var before = await FeedOf(host).HeadAsync(CancellationToken.None);

        using (ActivityScope.UseStrict())
        {
            await Assert.That(async () => await CaptureTaskAsync(host)).Throws<AppException>();
        }

        await CaptureTaskAsync(host);
        var entry = (await FeedOf(host).SinceAsync(before, 50, CancellationToken.None)).Entries.Single();
        await Assert.That(entry.Source).IsEqualTo("system");
        await Assert.That(entry.Action).IsEqualTo("unscoped");
    }

    [Test]
    public async Task Strict_mode_rejects_an_action_that_is_not_in_the_closed_list()
    {
        using var host = await DataHost.StartAsync();
        using var strict = ActivityScope.UseStrict();
        using var scope = User("tasks.madeUp");
        await Assert.That(async () => await CaptureTaskAsync(host)).Throws<AppException>();
    }

    [Test]
    public async Task Seq_follows_commit_order_across_interleaved_callers()
    {
        using var host = await DataHost.StartAsync();
        var feed = FeedOf(host);
        var before = await feed.HeadAsync(CancellationToken.None);

        var ids = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            await Task.Yield();
            return await NewTaskAsync(host);
        }));

        var entries = (await feed.SinceAsync(before, 100, CancellationToken.None)).Entries;
        await Assert.That(entries.Count).IsEqualTo(12);
        await Assert.That(entries.Select(entry => entry.Seq).SequenceEqual(entries.Select(entry => entry.Seq).Order())).IsTrue();
        var keys = entries.SelectMany(entry => entry.Entities).Where(entity => entity.Kind == "task").Select(entity => entity.Key).ToHashSet();
        await Assert.That(keys.SetEquals(ids)).IsTrue();
        // Commit order is OccurredAt order: the clock does not move, so the entries stay in Seq order and ids do not repeat.
        await Assert.That(entries.Select(entry => entry.OperationId).Distinct().Count()).IsEqualTo(12);
    }

    [Test]
    public async Task Pruning_keeps_seven_days_and_rows_a_cursor_still_needs_and_resets_a_pruned_position()
    {
        using var host = await DataHost.StartAsync();
        var feed = FeedOf(host);
        await NewTaskAsync(host);
        await NewTaskAsync(host);
        var old = await feed.HeadAsync(CancellationToken.None);

        host.Clock.Now = host.Clock.Now.AddDays(3);
        await NewTaskAsync(host);
        // Nothing is older than the seven-day floor yet.
        await Assert.That(await feed.PruneAsync(CancellationToken.None)).IsEqualTo(0);

        host.Clock.Now = host.Clock.Now.AddDays(6); // the first two rows are now 9 days old
        await feed.SetCursorAsync("slow", 1, CancellationToken.None);
        // The cursor still needs row 2: only rows up to it may go, and only those past the floor.
        await Assert.That(await feed.PruneAsync(CancellationToken.None)).IsEqualTo(1);
        await Assert.That((await feed.SinceAsync(0, 50, CancellationToken.None)).Reset).IsTrue();

        await feed.SetCursorAsync("slow", await feed.HeadAsync(CancellationToken.None), CancellationToken.None);
        await Assert.That(await feed.PruneAsync(CancellationToken.None)).IsGreaterThanOrEqualTo(1);
        var page = await feed.SinceAsync(old, 50, CancellationToken.None);
        await Assert.That(page.Reset).IsFalse();
    }

    [Test]
    public async Task The_hard_cap_removes_old_rows_even_when_a_cursor_stalled()
    {
        using var host = await DataHost.StartAsync();
        var feed = FeedOf(host);
        await NewTaskAsync(host);
        await feed.SetCursorAsync("stalled", 0, CancellationToken.None);
        host.Clock.Now = host.Clock.Now.AddDays(31);
        await NewTaskAsync(host);

        await Assert.That(await feed.PruneAsync(CancellationToken.None)).IsGreaterThanOrEqualTo(1);
        await Assert.That((await feed.SinceAsync(0, 50, CancellationToken.None)).Reset).IsTrue();
    }

    [Test]
    public async Task Job_runs_record_noop_ok_and_failed_passes_and_keep_the_last_200()
    {
        using var host = await DataHost.StartAsync();
        var runs = new JobRuns(host.Store, host.Clock);
        var feed = FeedOf(host);

        await JobPass.RunAsync(runs, ActivitySources.Schedule, ActivityActions.ReminderTick, _ => Task.CompletedTask, CancellationToken.None);
        await JobPass.RunAsync(runs, ActivitySources.Schedule, ActivityActions.ReminderTick, async _ => await CaptureTaskAsync(host), CancellationToken.None);
        await Assert.That(async () => await JobPass.RunAsync(runs, ActivitySources.Sync, ActivityActions.GitHubSync, _ => throw new InvalidOperationException("nope"), CancellationToken.None))
            .Throws<InvalidOperationException>();

        var outcomes = await ScalarAsync(host, "SELECT group_concat(Outcome) FROM (SELECT Outcome FROM JobRuns WHERE Job = 'reminders.tick' ORDER BY StartedAtUtc, rowid);");
        await Assert.That(outcomes).IsEqualTo("noop,ok");
        var jobs = await feed.JobsAsync(CancellationToken.None);
        await Assert.That(jobs.Single(job => job.Job == "github.sync").LastOutcome).IsEqualTo("failed");
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM ActivityLog WHERE Outcome = 'failed' AND JobRunId IS NOT NULL;")).IsEqualTo(1L);
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM ActivityLogEntities e JOIN ActivityLog l ON l.Seq = e.Seq WHERE l.Outcome = 'failed';")).IsEqualTo(0L);
        // The pass that wrote data stamped its job run id on the row.
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM ActivityLog l JOIN JobRuns r ON r.Id = l.JobRunId WHERE r.Job = 'reminders.tick' AND r.ActivityCount = 1;")).IsEqualTo(1L);

        for (var index = 0; index < JobRuns.KeepPerJob + 20; index++)
        {
            var id = await runs.StartAsync("reminders.tick");
            await runs.FinishAsync(id, JobOutcome.Ok);
        }

        await Assert.That(await CountAsync(host, "SELECT count(*) FROM JobRuns WHERE Job = 'reminders.tick';")).IsEqualTo((long)JobRuns.KeepPerJob);
    }

    [Test]
    public async Task The_notifier_hears_each_commit_that_wrote_activity()
    {
        using var host = await DataHost.StartAsync();
        var notifier = new RecordingNotifier();
        host.Store.ActivityNotifier = notifier;

        await NewTaskAsync(host);
        await host.Store.ExecuteAsync((session, _) => Task.FromResult(0), CancellationToken.None);

        await Assert.That(notifier.Heads.Count).IsEqualTo(1);
        await Assert.That(notifier.Heads[0]).IsEqualTo(await FeedOf(host).HeadAsync(CancellationToken.None));
    }

    private sealed class RecordingNotifier : IActivityNotifier
    {
        public List<long> Heads { get; } = [];
        public void HeadChanged(long seq) => Heads.Add(seq);
        public void JobsChanged() { }
    }
}
