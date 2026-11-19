using System.Diagnostics;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Tests.Sync;

/// <summary>PLAN2 P15: the event stream, the engine's use of it, and the status the indicator shows.</summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class SyncRealtimeTests(PostgresServer postgres)
{
    private static string Op() => Guid.NewGuid().ToString();

    private static async Task<string> CaptureAsync(SyncDevice device, string title) =>
        (await device.Tasks.CaptureAsync(new(Op(), string.Empty, title, null))).Task.Id;

    private static async Task<bool> WaitAsync(Func<Task<bool>> condition, TimeSpan limit)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < limit)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return await condition();
    }

    /// <summary>Polling is an hour away, so anything that arrives sooner came through the event stream.</summary>
    private static SyncEngineOptions Realtime() => new() { Jitter = () => 0, PollInterval = TimeSpan.FromHours(1), Debounce = TimeSpan.FromMilliseconds(100) };

    [Test]
    public async Task B_sees_A_change_within_three_seconds_through_the_event_stream_without_polling()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(options: Realtime());
        var b = await world.AddDeviceAsync(options: Realtime());
        a.Engine.Start();
        b.Engine.Start();
        await Assert.That(await WaitAsync(() => Task.FromResult(a.Engine.Runtime.Streaming && b.Engine.Runtime.Streaming), TimeSpan.FromSeconds(10))).IsTrue();
        await Task.Delay(300); // Let each device's first cycle finish so only the event can wake B.

        var watch = Stopwatch.StartNew();
        var id = await CaptureAsync(a, "From A");
        var arrived = await WaitAsync(async () => await b.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{id}';") == 1, TimeSpan.FromSeconds(3));

        await Assert.That(arrived).IsTrue();
        await Assert.That(watch.Elapsed < TimeSpan.FromSeconds(3)).IsTrue();
    }

    [Test]
    public async Task A_device_that_was_away_catches_up_when_its_stream_reconnects()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(options: Realtime());
        var b = await world.AddDeviceAsync(options: new SyncEngineOptions
        {
            Jitter = () => 0,
            PollInterval = TimeSpan.FromHours(1),
            MinBackoff = TimeSpan.FromMilliseconds(100),
            MaxBackoff = TimeSpan.FromMilliseconds(200),
            EventSilenceLimit = TimeSpan.FromMilliseconds(400),
        });
        a.Engine.Start();
        b.Engine.Start();
        await Assert.That(await WaitAsync(() => Task.FromResult(b.Engine.Runtime.Streaming), TimeSpan.FromSeconds(10))).IsTrue();

        // B's open stream hears nothing for longer than its silence limit and is reopened; while it is away it cannot connect.
        b.Network.Offline = true;
        var id = await CaptureAsync(a, "While B was away");
        await a.SyncAsync();
        await Task.Delay(800);
        b.Network.Offline = false;

        // The reconnect's first event carries the current sequence, so B pulls what it missed.
        var arrived = await WaitAsync(async () => await b.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{id}';") == 1, TimeSpan.FromSeconds(10));
        await Assert.That(arrived).IsTrue();
    }

    [Test]
    public async Task The_stream_sends_the_current_sequence_first_and_a_keep_alive_when_idle()
    {
        await using var world = await SyncWorld.StartAsync(postgres, new() { ["Sync:EventKeepAlive"] = "00:00:00.200" });
        var a = await world.AddDeviceAsync();
        await CaptureAsync(a, "One");
        await a.SyncAsync();

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lines = new List<string>();
        await foreach (var line in a.Client.StreamLinesAsync("v1/sync/events", stop.Token))
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }

            if (lines.Count == 2)
            {
                break;
            }
        }

        await Assert.That(lines[0]).StartsWith("data: {\"seq\":");
        await Assert.That(lines[1]).IsEqualTo(": keep-alive");
    }

    [Test]
    public async Task Another_instances_push_reaches_an_open_stream_through_postgres_notify()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new List<string>();
        var reader = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in a.Client.StreamLinesAsync("v1/sync/events", stop.Token))
                {
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        lock (seen)
                        {
                            seen.Add(line);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Done listening.
                return;
            }
        });
        bool Heard(string text)
        {
            lock (seen)
            {
                return seen.Any(line => line.Contains(text, StringComparison.Ordinal));
            }
        }

        await WaitAsync(() => Task.FromResult(Heard("seq")), TimeSpan.FromSeconds(5));

        // What a second instance does after its commit: NOTIFY on the account's channel, with no in-process broadcast.
        await using var db = ServerDatabase.Open(world.Server.ConnectionString);
        var account = await db.Accounts.SingleAsync();
        var channel = Chaos.Server.Sync.SyncNotificationListener.Channel;
        var payload = Chaos.Server.Sync.SyncNotificationListener.Payload(account.Id, 987);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_notify({channel}, {payload})");

        var heard = await WaitAsync(() => Task.FromResult(Heard("987")), TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await Task.WhenAny(reader, Task.Delay(2000));
        await Assert.That(heard).IsTrue();
    }

    [Test]
    public async Task The_status_follows_offline_recovery_attention_and_sign_out()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync();
        var reader = new SyncStatusReader(a.Store, a.Engine);

        await a.SyncAsync();
        var synced = await reader.GetAsync(CancellationToken.None);
        await Assert.That(synced.Kind).IsEqualTo(SyncStatusKind.Synced);
        await Assert.That(synced.LastSyncedAt).IsNotNull();

        a.Network.Offline = true;
        await CaptureAsync(a, "Written offline");
        var offlineCycle = await a.SyncAsync();
        var offline = await reader.GetAsync(CancellationToken.None);
        await Assert.That(offlineCycle.Problem).IsNotNull();
        await Assert.That(offline.Kind).IsEqualTo(SyncStatusKind.Offline);
        await Assert.That(offline.PendingChanges > 0).IsTrue();

        a.Network.Offline = false;
        await a.SyncAsync();
        var back = await reader.GetAsync(CancellationToken.None);
        await Assert.That(back.Kind).IsEqualTo(SyncStatusKind.Synced);
        await Assert.That(back.PendingChanges).IsEqualTo(0L);

        await a.ExecuteAsync("INSERT INTO SyncAttention (Id, Category, Message, CreatedAtUtc) VALUES ('att-1', 'sync.parked', 'Some changes could not be applied.', '2026-11-18T10:00:00.000Z');");
        var attention = await reader.GetAsync(CancellationToken.None);
        await Assert.That(attention.Kind).IsEqualTo(SyncStatusKind.NeedsAttention);
        await Assert.That(attention.Attention.Count).IsEqualTo(1);
        await Assert.That(attention.Attention[0].Actions).Contains("Sync now");

        await reader.ResolveAsync("att-1", CancellationToken.None);
        await Assert.That((await reader.GetAsync(CancellationToken.None)).Kind).IsEqualTo(SyncStatusKind.Synced);

        await a.ExecuteAsync("UPDATE SyncState SET Enabled = 0, AccountId = NULL WHERE Id = 1;");
        await Assert.That((await reader.GetAsync(CancellationToken.None)).Kind).IsEqualTo(SyncStatusKind.SignedOut);
    }

    [Test]
    public async Task A_computer_that_never_used_an_account_shows_no_status()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        await Assert.That((await new SyncStatusReader(a.Store, a.Engine).GetAsync(CancellationToken.None)).Kind).IsEqualTo(SyncStatusKind.Hidden);
    }

    [Test]
    public async Task The_status_rule_with_a_fake_engine_covers_every_state()
    {
        var now = DateTimeOffset.UtcNow;
        var attention = new[] { new SyncAttentionItem("1", "sync.parked", "m", "s", now, ["Dismiss"]) };
        SyncStatus Decide(bool enabled, long pending, IReadOnlyList<SyncAttentionItem> items, SyncRuntime runtime) => SyncStatusReader.Decide(enabled, now, pending, items, runtime);
        var idle = new SyncRuntime(false, null, false, true);

        await Assert.That(Decide(true, 0, [], idle).Kind).IsEqualTo(SyncStatusKind.Synced);
        await Assert.That(Decide(true, 0, [], idle with { Syncing = true }).Kind).IsEqualTo(SyncStatusKind.Syncing);
        await Assert.That(Decide(true, 5, [], idle with { Failure = ServiceFailure.Offline }).Kind).IsEqualTo(SyncStatusKind.Offline);
        var refused = Decide(true, 0, [], idle with { Failure = ServiceFailure.Refused, FailureDetail = "Server needs an update." });
        await Assert.That(refused.Kind).IsEqualTo(SyncStatusKind.NeedsAttention);
        await Assert.That(refused.Detail).IsEqualTo("Server needs an update.");
        await Assert.That(Decide(true, 0, attention, idle).Kind).IsEqualTo(SyncStatusKind.NeedsAttention);
        await Assert.That(Decide(true, 0, attention, idle with { Syncing = true }).Kind).IsEqualTo(SyncStatusKind.NeedsAttention);
        await Assert.That(Decide(true, 0, [], idle with { Failure = ServiceFailure.SignInAgain }).Kind).IsEqualTo(SyncStatusKind.SignedOut);
        await Assert.That(Decide(false, 0, [], idle).Kind).IsEqualTo(SyncStatusKind.SignedOut);
        await Assert.That(Decide(true, 0, attention, idle with { UpdateNeeded = true }).Kind).IsEqualTo(SyncStatusKind.UpdateNeeded);
        await Assert.That(SyncStatusReader.Decide(false, null, 0, [], idle).Kind).IsEqualTo(SyncStatusKind.Hidden);
    }
}
