using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Application.Pages;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Service;
using Chaos.Infrastructure.Sync;
using Chaos.Server.Sync;
using Chaos.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Chaos.Server.Tests.Sync;

/// <summary>
/// PLAN2 P13 item 4: the two-client end-to-end harness. Two real data roots with real SQLite and a real
/// <see cref="SyncEngine"/>, talking to the in-process service on real PostgreSQL. Each scenario ends with the
/// devices settled and, where it makes sense, byte-identical in their synced columns.
/// </summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class SyncEndToEndTests(PostgresServer postgres)
{
    private static string Op() => Guid.NewGuid().ToString();

    private static async Task<TaskRecord> CaptureAsync(SyncDevice device, string title) =>
        (await device.Tasks.CaptureAsync(new(Op(), string.Empty, title, null))).Task;

    private static async Task RetitleAsync(SyncDevice device, string taskId, string? title = null, string? notes = null)
    {
        var current = (await device.Tasks.GetAsync(taskId))!;
        await device.Tasks.UpdateDetailsAsync(new(Op(), taskId, current.Version, title ?? current.Title, notes ?? current.Notes, null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
    }

    [Test]
    public async Task Calendar_cache_uploads_without_changing_another_desktop_cache()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var b = await world.AddDeviceAsync();
        var now = world.Server.Clock.GetUtcNow();
        var sourceId = Guid.NewGuid().ToString("D");
        await a.Store.ExecuteAsync(async (session, ct) =>
        {
            await session.Calendars.AddSourceAsync(new CalendarSourceRecord
            {
                Id = sourceId, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now, Provider = CalendarProvider.Google,
                ExternalId = "primary", Name = "Calendar", Color = "#123456", CountsAsBusy = true, Enabled = true, Position = 0,
            }, ct);
            await session.Calendars.ReplaceEventsAsync(sourceId,
                [new CalendarEventRecord(sourceId, "event", now, now.AddHours(1), false, null, null,
                    "Planning", "Room", "https://example.com", null, true, now)], ct);
            await session.Calendars.SaveStateAsync(new CalendarSourceState(sourceId, now, now, null, null, null), ct);
            return 0;
        }, CancellationToken.None);
        await world.SettleAsync();
        await using (var db = ServerDatabase.Open(world.Server.ConnectionString))
        {
            var cache = db.SyncEntities.Single(row => row.Kind == "calendar_cache" && row.EntityKey == sourceId);
            await Assert.That(cache.Groups).Contains("Planning");
            await Assert.That(cache.Groups).Contains("Room");
        }
        await Assert.That(await b.CountAsync("SELECT count(*) FROM CalendarEvents;")).IsEqualTo(0L);
        await Assert.That(await b.CountAsync("SELECT count(*) FROM CalendarSourceState;")).IsEqualTo(0L);
    }

    [Test]
    public async Task A_task_created_on_A_appears_on_B()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var b = await world.AddDeviceAsync();

        var task = await CaptureAsync(a, "Buy milk");
        var pushed = await a.SyncAsync();
        var pulled = await b.SyncAsync();

        await Assert.That(pushed.Pushed > 0).IsTrue();
        await Assert.That(pulled.Pulled > 0).IsTrue();
        await Assert.That(await b.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{task.Id}';")).IsEqualTo("Buy milk");
        await Assert.That(await a.CountAsync("SELECT count(*) FROM SyncOutbox;")).IsEqualTo(0L);
        await world.SettleAsync();
        await world.AssertConvergedAsync();
    }

    [Test]
    public async Task Edits_to_different_groups_on_A_and_B_both_survive()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var b = await world.AddDeviceAsync();
        var task = await CaptureAsync(a, "Shared thing");
        await world.SettleAsync();

        await RetitleAsync(a, task.Id, title: "A's title");
        await RetitleAsync(b, task.Id, notes: "B's notes");
        await world.SettleAsync();
        foreach (var device in new[] { a, b })
        {
            await Assert.That(await device.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{task.Id}';")).IsEqualTo("A's title");
            await Assert.That(await device.TextAsync($"SELECT Notes FROM Tasks WHERE Id = '{task.Id}';")).IsEqualTo("B's notes");
        }

        await world.AssertConvergedAsync();
    }

    [Test]
    public async Task A_same_group_conflict_keeps_the_newest_and_the_loser_holds_the_replaced_value()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var b = await world.AddDeviceAsync();
        var task = await CaptureAsync(a, "Shared thing");
        await world.SettleAsync();

        a.Clock.Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        b.Clock.Now = new(2026, 11, 15, 11, 0, 0, TimeSpan.Zero);
        await RetitleAsync(a, task.Id, title: "from A (older)");
        await RetitleAsync(b, task.Id, title: "from B (newer)");
        await world.SettleAsync();

        await Assert.That(await a.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{task.Id}';")).IsEqualTo("from B (newer)");
        await Assert.That(await b.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{task.Id}';")).IsEqualTo("from B (newer)");
        var before = await a.TextAsync($"SELECT SnapshotBefore FROM TaskHistory WHERE TaskId = '{task.Id}' AND EventKind = 'sync.merged';");
        await Assert.That(before?.Contains("from A (older)", StringComparison.Ordinal)).IsTrue();
        await world.AssertConvergedAsync();
    }

    [Test]
    public async Task Offline_changes_on_A_queue_while_B_edits_and_merge_when_A_reconnects()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var b = await world.AddDeviceAsync();
        var task = await CaptureAsync(a, "Shared thing");
        await world.SettleAsync();

        a.Network.Offline = true;
        await RetitleAsync(a, task.Id, title: "A while offline");
        var created = await CaptureAsync(a, "Made offline");
        var offline = await a.SyncAsync();
        await Assert.That(offline.Problem).IsNotNull();
        await Assert.That(await a.CountAsync("SELECT count(*) FROM SyncOutbox;") > 0).IsTrue();

        await RetitleAsync(b, task.Id, notes: "B meanwhile");
        await b.SyncAsync();

        a.Network.Offline = false;
        await world.SettleAsync();

        await Assert.That(await b.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{task.Id}';")).IsEqualTo("A while offline");
        await Assert.That(await a.TextAsync($"SELECT Notes FROM Tasks WHERE Id = '{task.Id}';")).IsEqualTo("B meanwhile");
        await Assert.That(await b.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{created.Id}';")).IsEqualTo(1L);
        await Assert.That(await a.CountAsync("SELECT count(*) FROM SyncOutbox;")).IsEqualTo(0L);
        await world.AssertConvergedAsync();
    }

    [Test]
    public async Task A_delete_on_one_device_and_an_edit_on_the_other_converge_to_a_tombstone_with_the_edit()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var b = await world.AddDeviceAsync();
        var task = await CaptureAsync(a, "Shared thing");
        await world.SettleAsync();

        await a.Tasks.DeleteAsync(new(Op(), task.Id, (await a.Tasks.GetAsync(task.Id))!.Version));
        await RetitleAsync(b, task.Id, title: "B's later title");
        await world.SettleAsync();

        foreach (var device in new[] { a, b })
        {
            await Assert.That(await device.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{task.Id}' AND DeletedAtUtc IS NOT NULL;")).IsEqualTo(1L);
            await Assert.That(await device.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{task.Id}';")).IsEqualTo("B's later title");
        }

        await world.AssertConvergedAsync();
    }

    [Test]
    public async Task A_purge_removes_the_task_and_its_dependents_on_the_other_device_and_the_service_keeps_a_tombstone()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var b = await world.AddDeviceAsync();
        var task = await CaptureAsync(a, "Shared thing");
        await world.SettleAsync();

        await b.Tasks.DeleteAsync(new(Op(), task.Id, (await b.Tasks.GetAsync(task.Id))!.Version));
        await new Chaos.Infrastructure.DataProtection.DataPurge(b.Store, new Chaos.Infrastructure.Attachments.FileAttachmentStore(b.Storage.Options.AttachmentsDirectory)).PurgeAsync(task.Id, CancellationToken.None);
        await world.SettleAsync();

        await Assert.That(await a.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{task.Id}';")).IsEqualTo(0L);
        await Assert.That(await a.CountAsync($"SELECT count(*) FROM TaskHistory WHERE TaskId = '{task.Id}';")).IsEqualTo(0L);
        await Assert.That(await a.CountAsync("SELECT count(*) FROM SyncInboxParked;")).IsEqualTo(0L);

        await using var db = ServerDatabase.Open(world.Server.ConnectionString);
        var tombstone = db.SyncEntities.Single(e => e.Kind == "task" && e.EntityKey == task.Id);
        await Assert.That(tombstone.PurgedAt).IsNotNull();
        await Assert.That(tombstone.Groups).IsEqualTo("{}");
        await world.AssertConvergedAsync();
    }

    [Test]
    public async Task A_recurring_task_completed_on_both_devices_makes_one_successor()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var b = await world.AddDeviceAsync();
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Week, 1, new(2026, 11, 10), null, "UTC", [DayOfWeek.Tuesday], null, null, null, null, RecurrenceEndKind.Never, null, null);
        var created = (await new CaptureCommands(a.Store, a.Ids, a.Clock).CreateAsync(new(Op(),
        [
            new CaptureTaskDraft { Title = "Water plants", Recurrence = rule, PlannedDate = new DateOnly(2026, 11, 10) },
        ]))).Tasks[0];
        await world.SettleAsync();

        foreach (var device in new[] { a, b })
        {
            var current = (await device.Tasks.GetAsync(created.Id))!;
            await device.Tasks.SetStatusAsync(new(Op(), created.Id, current.Version, TaskItemStatus.Done, null));
        }

        await world.SettleAsync();

        foreach (var device in new[] { a, b })
        {
            await Assert.That(await device.CountAsync("SELECT count(*) FROM Tasks WHERE DeletedAtUtc IS NULL AND Status = 0 AND RecurrenceOccurrenceKey IS NOT NULL;")).IsEqualTo(1L);
        }

        await world.AssertConvergedAsync();
    }

    [Test]
    public async Task A_page_edited_on_both_devices_keeps_the_losing_text_in_versions()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var b = await world.AddDeviceAsync();
        var pages = new PageCommands(a.Store, a.Ids, a.Clock);
        var page = (await pages.CreateAsync(new(Op(), null, "Notes", "start"))).Page;
        await world.SettleAsync();

        a.Clock.Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        b.Clock.Now = new(2026, 11, 15, 11, 0, 0, TimeSpan.Zero);
        var onA = (await pages.GetAsync(page.Id)).Page;
        await pages.SaveAsync(new(page.Id, onA.DraftVersion + 1, "Notes", "text typed on A"));
        var pagesB = new PageCommands(b.Store, b.Ids, b.Clock);
        var onB = (await pagesB.GetAsync(page.Id)).Page;
        await pagesB.SaveAsync(new(page.Id, onB.DraftVersion + 1, "Notes", "text typed on B"));
        await world.SettleAsync();

        foreach (var device in new[] { a, b })
        {
            await Assert.That(await device.TextAsync($"SELECT RawMarkdown FROM Pages WHERE Id = '{page.Id}';")).IsEqualTo("text typed on B");
        }

        var versions = await a.RowsAsync($"SELECT RawMarkdown FROM PageRevisions WHERE PageId = '{page.Id}';");
        await Assert.That(versions.Any(text => text.Contains("text typed on A", StringComparison.Ordinal))).IsTrue();
        await world.AssertConvergedAsync();
    }

    [Test]
    public async Task Pushing_the_same_change_twice_adds_it_to_the_log_once()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var task = await CaptureAsync(a, "Once");
        var payloads = await a.RowsAsync("SELECT PayloadJson FROM SyncOutbox ORDER BY Seq;");
        var body = "{\"changes\":[" + string.Join(",", payloads) + "]}";

        var first = await a.Client.CallAsync(HttpMethod.Post, "v1/sync/push", body, CancellationToken.None);
        var second = await a.Client.CallAsync(HttpMethod.Post, "v1/sync/push", body, CancellationToken.None);

        await Assert.That(first.Ok).IsTrue();
        await Assert.That(second.Ok).IsTrue();
        await using var db = ServerDatabase.Open(world.Server.ConnectionString);
        await Assert.That(db.SyncChanges.Count()).IsEqualTo(payloads.Count);
        await Assert.That(db.SyncEntities.Count(e => e.Kind == "task" && e.EntityKey == task.Id)).IsEqualTo(1);
    }

    [Test]
    public async Task A_push_whose_answer_is_lost_is_sent_again_and_lands_once()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var b = await world.AddDeviceAsync();
        var task = await CaptureAsync(a, "Survives a restart");

        a.Network.DropNextPushAnswers(1);
        var lost = await a.SyncAsync();
        await Assert.That(lost.Problem).IsNotNull();
        await Assert.That(await a.CountAsync("SELECT count(*) FROM SyncOutbox;") > 0).IsTrue();

        await world.SettleAsync();
        await Assert.That(await b.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{task.Id}';")).IsEqualTo(1L);
        await using var db = ServerDatabase.Open(world.Server.ConnectionString);
        await Assert.That(db.SyncChanges.GroupBy(c => c.ChangeId).Any(group => group.Count() > 1)).IsFalse();
        await world.AssertConvergedAsync();
    }

    [Test]
    public async Task A_client_with_an_older_schema_does_not_erase_groups_it_does_not_know()
    {
        // The service's kind list says task.notes first appeared one schema after the one device A really speaks;
        // the raw caller below speaks that newer schema (what a newer client would send).
        var older = SqliteDeviceIdentity.ClientSchema;
        var newerSchema = older + 1;
        var newer = SyncKinds.All.Select(kind => kind.Kind == "task"
            ? kind with { Groups = [.. kind.Groups.Select(group => group.Name == "notes" ? group with { Since = newerSchema } : group)] }
            : kind).ToList();
        await using var world = await SyncWorld.StartAsync(postgres, configure: services =>
        {
            services.AddSingleton(new SyncKindCatalog(newer));
        });
        var a = await world.AddDeviceAsync();
        var task = await CaptureAsync(a, "Written by an old client");
        await world.SettleAsync();
        // A newer client sets notes.
        var hlc = Hlc.Create(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000, 0, Guid.NewGuid()).Encode();
        var notesChange = "{\"changes\":[{\"changeId\":\"" + Guid.NewGuid() + "\",\"kind\":\"task\",\"key\":\"" + task.Id + "\",\"op\":\"upsert\",\"groups\":{\"notes\":{\"hlc\":\"" + hlc + "\",\"values\":{\"Notes\":\"from a newer client\"}}}}]}";
        var pushed = await RawAsync(world, a, HttpMethod.Post, "v1/sync/push", schema: newerSchema, notesChange);
        await Assert.That(pushed).Contains("\"accepted\"");

        // The old client never receives the group it cannot know, and edits the title.
        await a.SyncAsync();
        await Assert.That(await a.TextAsync($"SELECT Notes FROM Tasks WHERE Id = '{task.Id}';")).IsNotEqualTo("from a newer client");
        a.Clock.Now = a.Clock.Now.AddHours(2);
        await RetitleAsync(a, task.Id, title: "Retitled by the old client");
        await world.SettleAsync();

        // The service still holds the newer group, now next to the new title.
        await using var db = ServerDatabase.Open(world.Server.ConnectionString);
        var entity = db.SyncEntities.Single(e => e.Kind == "task" && e.EntityKey == task.Id);
        var groups = SyncJson.ReadGroups(entity.Groups);
        await Assert.That(groups["notes"].Values.Values["Notes"]).IsEqualTo("from a newer client");
        await Assert.That(groups["title"].Values.Values["Title"]).IsEqualTo("Retitled by the old client");

        // A newer-schema pull sees both.
        var pulled = await RawAsync(world, a, HttpMethod.Get, "v1/sync/pull?after=0&limit=1000", schema: newerSchema, null);
        await Assert.That(pulled).Contains("from a newer client");
        var pulledOld = await RawAsync(world, a, HttpMethod.Get, "v1/sync/pull?after=0&limit=1000", schema: older, null);
        await Assert.That(pulledOld.Contains("from a newer client", StringComparison.Ordinal)).IsFalse();
    }

    private static async Task<string> RawAsync(SyncWorld world, SyncDevice device, HttpMethod method, string path, int schema, string? body)
    {
        var token = await device.Client.GetAccessTokenAsync(CancellationToken.None);
        var deviceId = Guid.Parse((await device.TextAsync("SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';"))!);
        using var http = world.Server.Factory.CreateClient();
        using var request = new HttpRequestMessage(method, "/" + path);
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add(SyncProtocol.ProtocolHeader, "1");
        request.Headers.Add(SyncProtocol.SchemaHeader, schema.ToString());
        request.Headers.Add(SyncProtocol.DeviceHeader, deviceId.ToString());
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    [Test]
    public async Task A_device_behind_the_change_log_horizon_catches_up_from_the_snapshot()
    {
        await using var world = await SyncWorld.StartAsync(postgres, new() { ["Sync:ChangeLogHorizon"] = "00:00:01" });
        var a = await world.AddDeviceAsync();
        var b = await world.AddDeviceAsync();
        var first = await CaptureAsync(a, "Before the horizon");
        await world.SettleAsync();

        var second = await CaptureAsync(a, "Pruned before B pulled it");
        await RetitleAsync(a, first.Id, title: "Edited, then pruned");
        await a.SyncAsync();

        world.Server.Clock.Advance(TimeSpan.FromSeconds(5));
        await using (var scope = world.Server.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SyncService>().PruneAsync(CancellationToken.None);
        }

        var caughtUp = await b.SyncAsync();
        await Assert.That(caughtUp.SnapshotUsed).IsTrue();
        await Assert.That(await b.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{first.Id}';")).IsEqualTo("Edited, then pruned");
        await Assert.That(await b.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{second.Id}';")).IsEqualTo(1L);

        // And it keeps working afterwards, from the cursor the snapshot gave.
        await RetitleAsync(a, second.Id, title: "After the snapshot");
        await world.SettleAsync();
        await Assert.That(await b.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{second.Id}';")).IsEqualTo("After the snapshot");
        await world.AssertConvergedAsync();
    }

    [Test]
    public async Task A_1000_task_initial_upload_goes_in_batches_without_blocking_a_local_edit()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var b = await world.AddDeviceAsync();
        var capture = new CaptureCommands(a.Store, a.Ids, a.Clock);
        foreach (var half in new[] { 0, 500 })
        {
            await capture.CreateAsync(new(Op(), [.. Enumerable.Range(half + 1, 500).Select(n => new CaptureTaskDraft { Title = $"Imported task {n}" })]));
        }

        var outbox = await a.CountAsync("SELECT count(*) FROM SyncOutbox;");
        await Assert.That(outbox >= 1000).IsTrue();
        var probe = await CaptureAsync(a, "Edited while uploading");
        var pushesBefore = a.Network.Pushes;

        var pushPause = a.Network.PauseNextPush();
        var upload = Task.Run(() => a.SyncAsync());
        try
        {
            await pushPause.Started.WaitAsync(TimeSpan.FromSeconds(10));
            await RetitleAsync(a, probe.Id, notes: "edited while upload request is in flight")
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            pushPause.Release();
            await upload;
        }

        var result = await upload;
        await Assert.That(result.Problem).IsNull();
        await Assert.That(a.Network.Pushes - pushesBefore >= 3).IsTrue(); // 1,000+ changes cannot fit one 500-change push

        await world.SettleAsync();
        await Assert.That(await b.CountAsync("SELECT count(*) FROM Tasks WHERE Title LIKE 'Imported task %';")).IsEqualTo(1000L);
        await world.AssertConvergedAsync();
    }
}
