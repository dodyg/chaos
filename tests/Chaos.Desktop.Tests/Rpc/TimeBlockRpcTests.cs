using Chaos.Application.Abstractions;
using Chaos.Application.Capture;
using Chaos.Application.Calendar;
using Chaos.Desktop.Platform;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;
using Microsoft.Data.Sqlite;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>Time blocking over RPC (PLAN3 §11): the settings round-trip as strings and plain data, Sync now runs a pass, turning off deletes the calendar when asked.</summary>
public sealed class TimeBlockRpcTests
{
    private const string Subject = "109876543210987654321";

    /// <summary>The smallest Google: one calendar, one event per task, a change feed that only holds what was written.</summary>
    private sealed class FakeGoogle : IGoogleCalendarClient
    {
        public bool IsConfigured => true;
        public string? Calendar { get; private set; }
        public Dictionary<string, GoogleEventBody> Events { get; } = [];
        public int EventWrites { get; private set; }
        public List<string> Methods { get; } = [];

        public Task<GoogleSignIn> SignInAsync(CancellationToken ct) => Task.FromResult(new GoogleSignIn(Subject, "me@example.com", "token"));
        public Task<IReadOnlyList<GoogleCalendarInfo>> ListCalendarsAsync(string refreshToken, CancellationToken ct) => Task.FromResult<IReadOnlyList<GoogleCalendarInfo>>([]);
        public Task<IReadOnlyList<ParsedMeeting>> ListEventsAsync(string refreshToken, string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) => Task.FromResult<IReadOnlyList<ParsedMeeting>>([]);
        public Task<bool> RevokeAsync(string refreshToken, CancellationToken ct) => Task.FromResult(true);
        public Task<GoogleCalendarInfo> CreateCalendarAsync(string refreshToken, string name, CancellationToken ct) { Methods.Add("createCalendar"); Calendar ??= "chaos_calendar"; return Task.FromResult(new GoogleCalendarInfo(Calendar, name, null, false)); }
        public Task DeleteCalendarAsync(string refreshToken, string calendarId, CancellationToken ct) { Methods.Add("deleteCalendar"); Calendar = null; Events.Clear(); return Task.CompletedTask; }
        public Task<GoogleEventWrite> InsertEventAsync(string refreshToken, string calendarId, GoogleEventBody body, CancellationToken ct)
        {
            EventWrites++;
            return Events.ContainsKey(body.Id!) ? Task.FromResult(new GoogleEventWrite(true, false, "e", null)) : Write(body);
        }
        public Task<GoogleEventWrite> PatchEventAsync(string refreshToken, string calendarId, string eventId, GoogleEventBody body, CancellationToken ct)
        {
            EventWrites++;
            return Events.ContainsKey(eventId) ? Write(body with { Id = eventId }) : Task.FromResult(new GoogleEventWrite(false, true, null, null));
        }
        public Task DeleteEventAsync(string refreshToken, string calendarId, string eventId, CancellationToken ct) { EventWrites++; Events.Remove(eventId); return Task.CompletedTask; }
        public Task<GoogleSyncPage> ListEventChangesAsync(string refreshToken, string calendarId, string? syncToken, string? pageToken, CancellationToken ct) =>
            Task.FromResult(new GoogleSyncPage([.. Events.Values.Select(body => new GoogleEventChange(body.Id!, "e", null, false, body.Summary, body.StartUtc, body.EndUtc, "UTC"))], null, "sync", false));
        private Task<GoogleEventWrite> Write(GoogleEventBody body) { Events[body.Id!] = body; return Task.FromResult(new GoogleEventWrite(false, false, "e", null)); }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task SetAsync(string key, string value, CancellationToken ct = default) { Values[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task DeleteAsync(string key, CancellationToken ct = default) { Values.Remove(key); return Task.CompletedTask; }
    }

    [Test]
    public async Task Status_Enable_Update_SyncNow_RunHere_AndTurnOff_RoundTrip()
    {
        var clock = new FixedTimeProvider(new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        using var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
        var store = storage.Store;
        var google = new FakeGoogle();
        var secrets = new MemorySecrets();
        var account = await new GoogleCalendarCommands(store, secrets, google, clock).ConnectAsync();
        var commands = new TimeBlockCommands(store, secrets, google, ids, clock);
        using var scheduler = new TimeBlockScheduler(commands, clock);
        var rpc = new TimeBlockRpcService(commands, scheduler);
        var blocked = (await new CaptureCommands(store, ids, clock).CreateAsync(new(Op(),
            [new CaptureTaskDraft { Title = "Plan the week", PlannedDate = new DateOnly(2026, 11, 25), PlannedTime = new TimeOnly(9, 0), ZoneId = "UTC", EstimateMinutes = 45 }]))).Tasks[0];

        var off = await rpc.StatusAsync(new(), CancellationToken.None);
        await Assert.That(off.Ok).IsTrue();
        await Assert.That(off.Configured).IsTrue();
        await Assert.That(off.Status!.Enabled).IsFalse();
        await Assert.That(off.Status.Spaces.Single().Name).IsEqualTo("Personal");
        await Assert.That(off.Status.Accounts.Single().Email).IsEqualTo("me@example.com");
        await Assert.That(await scheduler.RunNowAsync(CancellationToken.None)).IsNull(); // off: the button's pass makes no request
        await Assert.That(google.EventWrites).IsEqualTo(0);

        var on = await rpc.EnableAsync(new(account.Account.Id, true, null), CancellationToken.None);
        await Assert.That(on.Ok).IsTrue();
        await Assert.That(on.Status!.Enabled).IsTrue();
        await Assert.That(on.Status.IsRunner).IsTrue();
        await Assert.That(on.Status.CalendarId).IsEqualTo("chaos_calendar");
        await Assert.That(on.Status.EventCount).IsEqualTo(1);
        await Assert.That(on.Status.LastError).IsNull();
        await Assert.That(long.Parse(on.Status.ProfileVersion, System.Globalization.CultureInfo.InvariantCulture)).IsGreaterThan(0);
        await Assert.That(google.Events.Values.Single().Summary).IsEqualTo("Plan the week");

        var busy = await rpc.UpdateAsync(new(on.Status.ProfileVersion, false, null), CancellationToken.None);
        await Assert.That(busy.Ok).IsTrue();
        await Assert.That(busy.Status!.ShowTitles).IsFalse();
        var now = await rpc.SyncNowAsync(new(), CancellationToken.None);
        await Assert.That(now.Ok).IsTrue();
        await Assert.That(now.Pass!.Ran).IsTrue();
        await Assert.That(now.Pass.Updated).IsEqualTo(1);
        await Assert.That(google.Events.Values.Single().Summary).IsNull(); // Busy: no title leaves the device

        // A stale version is a conflict returned as data, not a crash.
        var stale = await rpc.RunOnThisDeviceAsync(new(on.Status.ProfileVersion), CancellationToken.None);
        await Assert.That(stale.Ok).IsFalse();
        await Assert.That(stale.Error).IsNotNull();
        var here = await rpc.RunOnThisDeviceAsync(new(busy.Status!.ProfileVersion), CancellationToken.None);
        await Assert.That(here.Ok).IsTrue();
        await Assert.That(here.Status!.IsRunner).IsTrue();

        var kept = await rpc.DisableAsync(new(here.Status.ProfileVersion, false), CancellationToken.None);
        await Assert.That(kept.Ok).IsTrue();
        await Assert.That(kept.Status!.Enabled).IsFalse();
        await Assert.That(kept.CalendarDeleted).IsFalse();
        await Assert.That(google.Calendar).IsNotNull();
        var again = await rpc.EnableAsync(new(account.Account.Id, true, null), CancellationToken.None);
        var gone = await rpc.DisableAsync(new(again.Status!.ProfileVersion, true), CancellationToken.None);
        await Assert.That(gone.CalendarDeleted).IsTrue();
        await Assert.That(google.Calendar).IsNull();
        await Assert.That(gone.Status!.Enabled).IsFalse();
        await Assert.That(gone.Status.AccountId).IsNull();

        // The task itself was never touched by turning the blocks on and off.
        var task = await store.QueryAsync((session, token) => session.Tasks.GetAsync(blocked.Id, token));
        await Assert.That(task!.Version).IsEqualTo(blocked.Version);
        await Assert.That(await store.QueryAsync((session, token) => session.TimeBlocks.ListEventsAsync(token))).IsEmpty();
        using (var connection = new SqliteConnection($"Data Source={storage.DatabasePath}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM ActivityLog WHERE Action LIKE 'timeblock.%' AND Source NOT IN ('user', 'schedule');";
            await Assert.That(Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)).IsEqualTo(0L);
        }
    }

    private static string Op() => Guid.NewGuid().ToString();
}
