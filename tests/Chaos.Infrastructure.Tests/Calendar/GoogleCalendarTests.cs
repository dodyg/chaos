using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Calendar;
using Chaos.Application.Contracts;
using Chaos.Infrastructure.Calendar;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Transfer;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Calendar;

/// <summary>Google Calendar commands and refresh against real SQLite with a fake Google and fake secrets (PLAN2 P6).</summary>
public sealed class GoogleCalendarTests
{
    private const string Token = "1//refresh-token-DO-NOT-LEAK-7f3a9c";
    private const string Subject = "109876543210987654321";

    private sealed class FakeGoogle : IGoogleCalendarClient
    {
        public bool IsConfigured { get; set; } = true;
        public int SignIns { get; private set; }
        public int CalendarCalls { get; private set; }
        public List<string> EventCalls { get; } = [];
        public List<string> Revoked { get; } = [];
        public bool RevokeSucceeds { get; set; } = true;
        public Func<string, IReadOnlyList<ParsedMeeting>> Events { get; set; } = _ => [];
        public GoogleCalendarException? Fail { get; set; }

        public Task<GoogleSignIn> SignInAsync(CancellationToken ct)
        {
            SignIns++;
            return Task.FromResult(new GoogleSignIn(Subject, "me@example.com", Token));
        }

        public Task<IReadOnlyList<GoogleCalendarInfo>> ListCalendarsAsync(string refreshToken, CancellationToken ct)
        {
            CalendarCalls++;
            return Fail is null
                ? Task.FromResult<IReadOnlyList<GoogleCalendarInfo>>([new("me@example.com", "me@example.com", "#16a765", true), new("team@example.com", "Team", "#9a9cff", false)])
                : throw Fail;
        }

        public Task<IReadOnlyList<ParsedMeeting>> ListEventsAsync(string refreshToken, string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
        {
            EventCalls.Add(calendarId);
            return Fail is null ? Task.FromResult(Events(calendarId)) : throw Fail;
        }

        public Task<bool> RevokeAsync(string refreshToken, CancellationToken ct)
        {
            Revoked.Add(refreshToken);
            return Task.FromResult(RevokeSucceeds);
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task SetAsync(string key, string value, CancellationToken ct = default) { Values[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task DeleteAsync(string key, CancellationToken ct = default) { Values.Remove(key); return Task.CompletedTask; }
    }

    private sealed class NoFeeds : ICalendarFeedClient
    {
        public Task<FeedFetchResult> FetchAsync(Uri address, string? etag, string? lastModified, CancellationToken ct) => throw new InvalidOperationException("no feeds in this test");
    }

    private sealed class Rig : IDisposable
    {
        public required DataHost Host { get; init; }
        public required FakeGoogle Google { get; init; }
        public required MemorySecrets Secrets { get; init; }
        public required GoogleCalendarCommands Commands { get; init; }
        public required CalendarRefresher Refresher { get; init; }

        public static async Task<Rig> StartAsync()
        {
            var host = await DataHost.StartAsync();
            var google = new FakeGoogle();
            var secrets = new MemorySecrets();
            return new Rig
            {
                Host = host,
                Google = google,
                Secrets = secrets,
                Commands = new GoogleCalendarCommands(host.Store, secrets, google, host.Clock),
                Refresher = new CalendarRefresher(host.Store, secrets, new NoFeeds(), new IcsParser(), host.Clock, google),
            };
        }

        /// <summary>Connects the fake account and chooses the team calendar; returns the account and the source.</summary>
        public async Task<(GoogleAccountView Account, CalendarSourceRecord Source)> ConnectAndChooseAsync()
        {
            var account = await Commands.ConnectAsync();
            var source = (await Commands.ChooseAsync(new ChooseGoogleCalendarCommand(account.Account.Id, "team@example.com", "Team", "#9a9cff", true))).Source;
            return (account, source);
        }

        public void Dispose() => Host.Dispose();
    }

    private static ParsedMeeting Meeting(string id, string title, DateTimeOffset start, bool busy = true) =>
        new(id, start, start.AddHours(1), false, null, null, title, null, null, null, busy);

    private static async Task<IReadOnlyList<CalendarEventRecord>> CachedAsync(Rig rig) =>
        await rig.Host.Store.QueryAsync((session, token) =>
            session.Calendars.ListEventsAsync(DataHost.Start.AddDays(-30), DataHost.Start.AddDays(90), DateOnly.FromDateTime(DataHost.Start.UtcDateTime.AddDays(-30)), DateOnly.FromDateTime(DataHost.Start.UtcDateTime.AddDays(90)), token), CancellationToken.None);

    [Test]
    public async Task NothingConnected_MeansNoRequestsAtAll()
    {
        using var rig = await Rig.StartAsync();

        await Assert.That(await rig.Refresher.HasSourcesAsync()).IsFalse();
        await Assert.That(await rig.Refresher.RefreshAsync()).IsEmpty();
        await Assert.That(await rig.Refresher.RefreshAsync(onlyDue: true)).IsEmpty();
        await Assert.That(rig.Google.SignIns + rig.Google.CalendarCalls + rig.Google.EventCalls.Count).IsEqualTo(0);

        // An account with no chosen calendar also fetches nothing, and a calendar switched off does not either.
        var (account, source) = await rig.ConnectAndChooseAsync();
        await rig.Commands.ChooseAsync(new ChooseGoogleCalendarCommand(account.Account.Id, "team@example.com", "Team", null, false));
        await Assert.That(await rig.Refresher.RefreshAsync()).IsEmpty();
        await Assert.That(rig.Google.EventCalls).IsEmpty();
        await Assert.That(source.Provider).IsEqualTo(CalendarProvider.Google);
    }

    [Test]
    public async Task Connect_StoresTheTokenOnlyInTheSecretStore_AndNeverOnDisk()
    {
        using var rig = await Rig.StartAsync();
        var (account, source) = await rig.ConnectAndChooseAsync();
        rig.Google.Events = _ => [Meeting("e1", "Planning", DataHost.Start.AddDays(1))];
        await rig.Refresher.RefreshAsync();

        await Assert.That(rig.Secrets.Values[GoogleCalendarCommands.SecretKey(account.Account.Id)]).IsEqualTo(Token);
        await Assert.That(account.Account.Email).IsEqualTo("me@example.com");
        await Assert.That(account.SignedInHere).IsTrue();

        // Every byte of every file under the data root, and a JSON export, must be free of the token and the Google subject-free secrets.
        using (var connection = new SqliteConnection($"Data Source={rig.Host.Storage.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }

        var export = await rig.Host.Transfer.ExportAsync(ExportFormat.Json, Path.Combine(rig.Host.Root, "exports"), CancellationToken.None);
        var needles = new[] { Token, "refresh-token-DO-NOT-LEAK" }.Select(Encoding.UTF8.GetBytes).ToList();
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(rig.Host.Root, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".lock", StringComparison.Ordinal)))
        {
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(file);
            }
            catch (IOException)
            {
                continue;
            }

            scanned++;
            foreach (var needle in needles)
            {
                await Assert.That(bytes.AsSpan().IndexOf(needle) >= 0).IsFalse();
            }
        }

        await Assert.That(scanned).IsGreaterThan(0);
        await Assert.That(File.Exists(export.Path)).IsTrue();

        // What the screen reads (accounts and sources) holds no token either.
        var shown = string.Concat((await rig.Commands.ListAsync()).Select(view => view.ToString() + string.Concat(view.Calendars.Select(c => c.ToString()))));
        await Assert.That(shown).DoesNotContain("DO-NOT-LEAK");
        await Assert.That(source.ExternalId).IsEqualTo("team@example.com");
    }

    [Test]
    public async Task Choosing_CreatesAStableSource_AndSwitchingOffKeepsTheColour()
    {
        using var rig = await Rig.StartAsync();
        var account = await rig.Commands.ConnectAsync();
        var first = (await rig.Commands.ChooseAsync(new ChooseGoogleCalendarCommand(account.Account.Id, "team@example.com", "Team", "#9a9cff", true))).Source;
        var again = (await rig.Commands.ChooseAsync(new ChooseGoogleCalendarCommand(account.Account.Id, "team@example.com", "Team", "#000000", true))).Source;
        var off = (await rig.Commands.ChooseAsync(new ChooseGoogleCalendarCommand(account.Account.Id, "team@example.com", "Team", null, false))).Source;
        var on = (await rig.Commands.ChooseAsync(new ChooseGoogleCalendarCommand(account.Account.Id, "team@example.com", "Team", null, true))).Source;

        // The id is derived from the account and calendar, so two devices choosing offline converge on one row.
        await Assert.That(first.Id).IsEqualTo(GoogleCalendarCommands.SourceId(account.Account.Id, "team@example.com"));
        await Assert.That(again.Id).IsEqualTo(first.Id);
        await Assert.That(again.Version).IsEqualTo(first.Version);
        await Assert.That(again.Color).IsEqualTo("#9a9cff");
        await Assert.That(off.Enabled).IsFalse();
        await Assert.That(on.Enabled).IsTrue();
        await Assert.That(on.Color).IsEqualTo("#9a9cff");
        await Assert.That(on.AccountRef).IsEqualTo(account.Account.Id);
        await Assert.That((await rig.Commands.ListAsync()).Single().Calendars.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Refresh_ReplacesTheWindow_AndRecordsTheTime()
    {
        using var rig = await Rig.StartAsync();
        var (account, source) = await rig.ConnectAndChooseAsync();
        rig.Google.Events = _ => [Meeting("e1", "Planning", DataHost.Start.AddDays(1)), Meeting("e2", "Focus", DataHost.Start.AddDays(2), busy: false)];

        var first = (await rig.Refresher.RefreshAsync()).Single();

        await Assert.That(first.Ok).IsTrue();
        await Assert.That(first.EventCount).IsEqualTo(2);
        await Assert.That((await CachedAsync(rig)).Select(e => e.Title).Order().ToArray()).IsEquivalentTo(new[] { "Focus", "Planning" });
        await Assert.That((await CachedAsync(rig)).Single(e => e.Title == "Focus").Busy).IsFalse();

        // Declining one in Google removes it from the next fetch, and the cache follows (the whole window is replaced).
        rig.Host.Clock.Now = rig.Host.Clock.Now.AddMinutes(6);
        rig.Google.Events = _ => [Meeting("e2", "Focus", DataHost.Start.AddDays(2), busy: false)];
        await rig.Refresher.RefreshAsync(onlyDue: true);

        await Assert.That((await CachedAsync(rig)).Select(e => e.Title).ToArray()).IsEquivalentTo(new[] { "Focus" });
        var view = (await rig.Commands.ListAsync()).Single();
        await Assert.That(view.State!.LastError).IsNull();
        await Assert.That(view.State.LastSuccessUtc).IsEqualTo(rig.Host.Clock.Now);
        await Assert.That(view.Calendars.Single().State!.LastSuccessUtc).IsEqualTo(rig.Host.Clock.Now);
        await Assert.That(source.Id).IsEqualTo(view.Calendars.Single().Source.Id);
        await Assert.That(account.Account.Id).IsEqualTo(view.Account.Id);
    }

    [Test]
    public async Task OnlyDue_SkipsACalendarFetchedWithinFiveMinutes()
    {
        using var rig = await Rig.StartAsync();
        await rig.ConnectAndChooseAsync();
        await rig.Refresher.RefreshAsync(onlyDue: true);
        await Assert.That(rig.Google.EventCalls.Count).IsEqualTo(1);

        rig.Host.Clock.Now = rig.Host.Clock.Now.AddMinutes(3);
        await Assert.That(await rig.Refresher.RefreshAsync(onlyDue: true)).IsEmpty();
        await Assert.That(rig.Google.EventCalls.Count).IsEqualTo(1);

        rig.Host.Clock.Now = rig.Host.Clock.Now.AddMinutes(2);
        await rig.Refresher.RefreshAsync(onlyDue: true);
        await Assert.That(rig.Google.EventCalls.Count).IsEqualTo(2);

        // A manual refresh always fetches.
        await rig.Refresher.RefreshAsync();
        await Assert.That(rig.Google.EventCalls.Count).IsEqualTo(3);
    }

    [Test]
    public async Task AnExpiredToken_KeepsTheCache_AndSaysSignInAgain_ThenSignInClearsIt()
    {
        using var rig = await Rig.StartAsync();
        var (account, _) = await rig.ConnectAndChooseAsync();
        rig.Google.Events = _ => [Meeting("e1", "Planning", DataHost.Start.AddDays(1))];
        await rig.Refresher.RefreshAsync();

        rig.Google.Fail = new GoogleCalendarException(GoogleFailure.SignInAgain, "Google Calendar: sign in again.");
        var outcome = (await rig.Refresher.RefreshAsync()).Single();

        await Assert.That(outcome.Ok).IsFalse();
        await Assert.That(outcome.Error).IsEqualTo("Google Calendar: sign in again.");
        await Assert.That((await CachedAsync(rig)).Count).IsEqualTo(1);
        var view = (await rig.Commands.ListAsync()).Single();
        await Assert.That(view.State!.NeedsSignIn).IsTrue();
        await Assert.That(view.State.LastError).IsEqualTo("Google Calendar: sign in again.");
        await Assert.That(view.State.LastSuccessUtc).IsNotNull();
        await Assert.That(view.Calendars.Single().State!.LastError).IsEqualTo("Google Calendar: sign in again.");

        rig.Google.Fail = null;
        rig.Google.Events = _ => [];
        var again = await rig.Commands.ConnectAsync();
        await Assert.That(again.State!.NeedsSignIn).IsFalse();
        await Assert.That(again.State.LastError).IsNull();
        await Assert.That(again.Account.Id).IsEqualTo(account.Account.Id);
        await Assert.That((await rig.Commands.ListAsync()).Count).IsEqualTo(1);
    }

    [Test]
    public async Task ABlockedOrOfflineAccount_IsNotHammered_AndEachStateReadsPlainly()
    {
        using var rig = await Rig.StartAsync();
        var (account, _) = await rig.ConnectAndChooseAsync();
        await rig.Commands.ChooseAsync(new ChooseGoogleCalendarCommand(account.Account.Id, "me@example.com", "Me", null, true));

        rig.Google.Fail = new GoogleCalendarException(GoogleFailure.Offline, "Chaos could not reach Google. It will try again when you are online.");
        var outcomes = await rig.Refresher.RefreshAsync();

        // The first calendar discovers the problem; the second is marked without another request.
        await Assert.That(outcomes.Count).IsEqualTo(2);
        await Assert.That(outcomes.All(o => !o.Ok)).IsTrue();
        await Assert.That(rig.Google.EventCalls.Count).IsEqualTo(1);
        var view = (await rig.Commands.ListAsync()).Single();
        await Assert.That(view.State!.NeedsSignIn).IsFalse();
        await Assert.That(view.State.LastError).Contains("reach Google");

        rig.Google.Fail = new GoogleCalendarException(GoogleFailure.Blocked, "Google Calendar is blocked for this account by its administrator.");
        await rig.Refresher.RefreshAsync();
        await Assert.That((await rig.Commands.ListAsync()).Single().State!.LastError).Contains("administrator");
    }

    [Test]
    public async Task ASyncedAccountWithoutAToken_ShowsSignInOnThisDevice_AndMakesNoRequest()
    {
        using var rig = await Rig.StartAsync();
        var (account, _) = await rig.ConnectAndChooseAsync();
        rig.Secrets.Values.Clear(); // as on a second device: the rows came with sync, the token did not

        var view = (await rig.Commands.ListAsync()).Single();
        await Assert.That(view.SignedInHere).IsFalse();

        var outcome = (await rig.Refresher.RefreshAsync()).Single();
        await Assert.That(outcome.Ok).IsFalse();
        await Assert.That(outcome.Error).IsEqualTo(GoogleCalendarCommands.SignInHereMessage);
        await Assert.That(rig.Google.EventCalls).IsEmpty();

        var listing = await Assert.That(async () => await rig.Commands.CalendarsAsync(account.Account.Id)).Throws<AppException>();
        await Assert.That(listing!.Message).IsEqualTo(GoogleCalendarCommands.SignInHereMessage);
        await Assert.That(rig.Google.CalendarCalls).IsEqualTo(0);
    }

    [Test]
    public async Task Calendars_ListsEveryCalendarWithTheCurrentChoice_AndAFailureIsRecorded()
    {
        using var rig = await Rig.StartAsync();
        var (account, source) = await rig.ConnectAndChooseAsync();

        var choices = await rig.Commands.CalendarsAsync(account.Account.Id);

        await Assert.That(choices.Count).IsEqualTo(2);
        await Assert.That(choices.Single(c => c.Info.Id == "team@example.com").Source!.Id).IsEqualTo(source.Id);
        await Assert.That(choices.Single(c => c.Info.Id == "me@example.com").Source).IsNull();

        rig.Google.Fail = new GoogleCalendarException(GoogleFailure.SignInAgain, "x");
        await Assert.That(async () => await rig.Commands.CalendarsAsync(account.Account.Id)).Throws<AppException>();
        await Assert.That((await rig.Commands.ListAsync()).Single().State!.NeedsSignIn).IsTrue();
    }

    [Test]
    public async Task Disconnect_RevokesAtGoogle_RemovesTheTokenAndTheRows_AndTheCache()
    {
        using var rig = await Rig.StartAsync();
        var (account, source) = await rig.ConnectAndChooseAsync();
        rig.Google.Events = _ => [Meeting("e1", "Planning", DataHost.Start.AddDays(1))];
        await rig.Refresher.RefreshAsync();
        await Assert.That((await CachedAsync(rig)).Count).IsEqualTo(1);

        var revoked = await rig.Commands.DisconnectAsync(account.Account.Id);

        await Assert.That(revoked).IsTrue();
        await Assert.That(rig.Google.Revoked.Single()).IsEqualTo(Token);
        await Assert.That(rig.Secrets.Values).IsEmpty();
        await Assert.That(await rig.Commands.ListAsync()).IsEmpty();
        await Assert.That(await CachedAsync(rig)).IsEmpty();
        await Assert.That(await rig.Host.Store.QueryAsync(async (session, token) => (await session.Calendars.GetSourceAsync(source.Id, token))!.DeletedAtUtc, CancellationToken.None)).IsNotNull();
        await Assert.That(await rig.Refresher.HasSourcesAsync()).IsFalse();

        // Signing in again later brings the account back.
        var back = await rig.Commands.ConnectAsync();
        await Assert.That(back.Account.Id).IsEqualTo(account.Account.Id);
        await Assert.That((await rig.Commands.ListAsync()).Count).IsEqualTo(1);
    }

    [Test]
    public async Task Disconnect_StillDisconnects_WhenGoogleCannotBeReachedToRevoke()
    {
        using var rig = await Rig.StartAsync();
        var (account, _) = await rig.ConnectAndChooseAsync();
        rig.Google.RevokeSucceeds = false;

        var revoked = await rig.Commands.DisconnectAsync(account.Account.Id);

        await Assert.That(revoked).IsFalse();
        await Assert.That(rig.Secrets.Values).IsEmpty();
        await Assert.That(await rig.Commands.ListAsync()).IsEmpty();
    }

    [Test]
    public async Task NotConfigured_ConnectSaysSoAndStoresNothing()
    {
        using var rig = await Rig.StartAsync();
        var google = new NotConfiguredGoogle();
        var commands = new GoogleCalendarCommands(rig.Host.Store, rig.Secrets, google, rig.Host.Clock);

        var error = await Assert.That(async () => await commands.ConnectAsync()).Throws<AppException>();

        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.UnsupportedCapability);
        await Assert.That(commands.IsConfigured).IsFalse();
        await Assert.That(rig.Secrets.Values).IsEmpty();
        await Assert.That(await commands.ListAsync()).IsEmpty();
    }

    private sealed class NotConfiguredGoogle : IGoogleCalendarClient
    {
        public bool IsConfigured => false;
        public Task<GoogleSignIn> SignInAsync(CancellationToken ct) => throw new GoogleCalendarException(GoogleFailure.NotConfigured, "Google sign-in is not set up in this build of Chaos.");
        public Task<IReadOnlyList<GoogleCalendarInfo>> ListCalendarsAsync(string refreshToken, CancellationToken ct) => throw new InvalidOperationException();
        public Task<IReadOnlyList<ParsedMeeting>> ListEventsAsync(string refreshToken, string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) => throw new InvalidOperationException();
        public Task<bool> RevokeAsync(string refreshToken, CancellationToken ct) => throw new InvalidOperationException();
    }
}

internal static class GoogleCalendarTestExtensions
{
    public static Task<GoogleAccountView> ConnectAndSignInAgain(this GoogleCalendarCommands commands) => commands.ConnectAsync();
}
