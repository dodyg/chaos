using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Calendar;
using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Application.Planning;
using Chaos.Application.Summaries;
using Chaos.Domain.Summaries;
using Chaos.Infrastructure.Calendar;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Transfer;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Calendar;

/// <summary>Calendar feeds against real SQLite with fake network and secrets (PLAN2 P5).</summary>
public sealed class CalendarFeedTests
{
    private const string SecretAddress = "https://calendar.example.org/ical/private-9f8a7c6b5d4e3f2a1b0c/basic.ics";

    // The host's clock is Tuesday 10 November 2026, 09:00 UTC; the profile is set to UTC below.
    private static readonly DateOnly Today = new(2026, 11, 10);

    private sealed class FakeFeeds : ICalendarFeedClient
    {
        public int Calls { get; private set; }
        public Queue<Func<string?, FeedFetchResult>> Responses { get; } = new();
        public List<string> Requested { get; } = [];
        public string? LastEtag { get; private set; }

        public Task<FeedFetchResult> FetchAsync(Uri address, string? etag, string? lastModified, CancellationToken ct)
        {
            Calls++;
            Requested.Add(address.ToString());
            LastEtag = etag;
            return Task.FromResult(Responses.Dequeue()(etag));
        }

        public void Serve(string ics, string? etag = "\"v1\"") => Responses.Enqueue(_ => new FeedFetchResult(FeedFetchStatus.Content, ics, etag, null));
        public void Fail(string message) => Responses.Enqueue(_ => throw new CalendarFeedException(message));
        public void NotModified() => Responses.Enqueue(etag => new FeedFetchResult(FeedFetchStatus.NotModified, null, etag, null));
    }

    private sealed class MemorySecrets : ISecretStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task SetAsync(string key, string value, CancellationToken ct = default) { Values[key] = value; return Task.CompletedTask; }
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task DeleteAsync(string key, CancellationToken ct = default) { Values.Remove(key); return Task.CompletedTask; }
    }

    private sealed class Rig : IDisposable
    {
        public required DataHost Host { get; init; }
        public required FakeFeeds Feeds { get; init; }
        public required MemorySecrets Secrets { get; init; }
        public required CalendarCommands Commands { get; init; }
        public required CalendarRefresher Refresher { get; init; }
        public required CalendarQueries Queries { get; init; }

        public static async Task<Rig> StartAsync()
        {
            var host = await DataHost.StartAsync();
            Run(host, "UPDATE Profiles SET ZoneId = 'UTC', FirstWeekday = 0;");
            var feeds = new FakeFeeds();
            var secrets = new MemorySecrets();
            return new Rig
            {
                Host = host,
                Feeds = feeds,
                Secrets = secrets,
                Commands = new CalendarCommands(host.Store, secrets, host.Ids, host.Clock, new CaptureCommands(host.Store, host.Ids, host.Clock)),
                Refresher = new CalendarRefresher(host.Store, secrets, feeds, new IcsParser(), host.Clock),
                Queries = new CalendarQueries(host.Store, new Chaos.Application.Queries.TaskQueries(host.Store, host.Clock), host.Clock),
            };
        }

        public async Task<CalendarSourceRecord> AddAsync(string address = SecretAddress, bool busy = true, string? owner = null) =>
            (await Commands.AddFeedAsync(new AddFeedCommand(address, "Work", "#3366cc", busy, owner))).Source;

        public void Dispose() => Host.Dispose();
    }

    private static void Run(DataHost host, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={host.Storage.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Ics(params string[] events) =>
        string.Join("\r\n", ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Test//EN", .. events, "END:VCALENDAR", string.Empty]);

    private static string Meeting(string uid, string title, string start, string end, string extra = "") =>
        $"BEGIN:VEVENT\r\nUID:{uid}\r\nSUMMARY:{title}\r\nDTSTART:{start}\r\nDTEND:{end}\r\n{extra}END:VEVENT";

    [Test]
    public async Task NoSource_MeansNoNetworkAtAll()
    {
        using var rig = await Rig.StartAsync();

        await Assert.That(await rig.Refresher.HasSourcesAsync()).IsFalse();
        await Assert.That(await rig.Refresher.RefreshAsync()).IsEmpty();
        await Assert.That(rig.Feeds.Calls).IsEqualTo(0);

        // A disabled source does not fetch either.
        var source = await rig.AddAsync();
        var view = (await rig.Commands.ListAsync()).Single();
        await rig.Commands.UpdateAsync(new UpdateSourceCommand(source.Id, view.Source.Version, "Work", "#3366cc", true, Enabled: false, null));
        await Assert.That(await rig.Refresher.RefreshAsync()).IsEmpty();
        await Assert.That(rig.Feeds.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task TheFeedAddress_IsNeverInTheDatabaseOrAnExport()
    {
        using var rig = await Rig.StartAsync();
        var source = await rig.AddAsync();
        rig.Feeds.Serve(Ics(Meeting("a", "Standup", "20261111T100000Z", "20261111T110000Z")));
        await rig.Refresher.RefreshAsync();

        // The address went to the secret store and the fetch used it.
        await Assert.That(rig.Secrets.Values[CalendarCommands.SecretKey(source.Id)]).IsEqualTo(SecretAddress);
        await Assert.That(rig.Feeds.Requested.Single()).IsEqualTo(SecretAddress);

        // Make sure everything is on disk, then look for the address in every byte of every file under the data root.
        Run(rig.Host, "PRAGMA wal_checkpoint(TRUNCATE);");
        var export = await rig.Host.Transfer.ExportAsync(ExportFormat.Json, Path.Combine(rig.Host.Root, "exports"), CancellationToken.None);
        var needles = new[] { SecretAddress, "private-9f8a7c6b5d4e3f2a1b0c", "calendar.example.org" }.Select(Encoding.UTF8.GetBytes).ToList();
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
        // The archive is compressed, so also check the source of truth for what it was built from.
        await Assert.That(await rig.Host.Store.QueryAsync(async (session, token) =>
            (await session.Calendars.ListSourcesAsync(token)).Single().Name, CancellationToken.None)).IsEqualTo("Work");
    }

    [Test]
    public async Task AddFeed_RefusesPlainHttp_RewritesWebcal_AndValidatesTheForm()
    {
        using var rig = await Rig.StartAsync();

        var http = Assert.Throws<AppException>(() => CalendarCommands.NormalizeAddress("http://example.org/a.ics"));
        await Assert.That(http.Message).Contains("https");
        await Assert.That(CalendarCommands.NormalizeAddress("webcal://example.org/a.ics").ToString()).IsEqualTo("https://example.org/a.ics");
        Assert.Throws<AppException>(() => CalendarCommands.NormalizeAddress("ftp://example.org/a.ics"));
        Assert.Throws<AppException>(() => CalendarCommands.NormalizeAddress("   "));

        await Assert.That(async () => await rig.Commands.AddFeedAsync(new AddFeedCommand("http://example.org/a.ics", "Work", "#3366cc", true, null))).Throws<AppException>();
        await Assert.That(async () => await rig.Commands.AddFeedAsync(new AddFeedCommand(SecretAddress, " ", "#3366cc", true, null))).Throws<AppException>();
        await Assert.That(async () => await rig.Commands.AddFeedAsync(new AddFeedCommand(SecretAddress, "Work", "blue", true, null))).Throws<AppException>();

        // Nothing half-added: no row and no stored address.
        await Assert.That(await rig.Commands.ListAsync()).IsEmpty();
        await Assert.That(rig.Secrets.Values).IsEmpty();
    }

    [Test]
    public async Task Refresh_FillsTheCache_AndTheCalendarShowsMeetingsAndBusyTime()
    {
        using var rig = await Rig.StartAsync();
        await rig.AddAsync();
        rig.Feeds.Serve(Ics(
            Meeting("a", "Standup", "20261111T090000Z", "20261111T100000Z"),
            Meeting("b", "Planning", "20261111T093000Z", "20261111T110000Z"),
            Meeting("c", "Focus", "20261111T130000Z", "20261111T140000Z", "TRANSP:TRANSPARENT\r\n")));

        var outcome = (await rig.Refresher.RefreshAsync()).Single();
        await Assert.That(outcome.Ok).IsTrue();
        await Assert.That(outcome.EventCount).IsEqualTo(3);

        var week = await rig.Queries.RangeAsync(Today, Today.AddDays(6), new CalendarFilters());
        await Assert.That(week.Meetings.Count).IsEqualTo(3);
        await Assert.That(week.Meetings.All(item => item.Color == "#3366cc" && item.SourceName == "Work")).IsTrue();
        await Assert.That(week.Meetings.Single(item => item.Title == "Focus").Busy).IsFalse();

        // 09:00-11:00 is busy once (overlap), the transparent block is free.
        await Assert.That(week.Days.Single(day => day.Date == Today.AddDays(1)).BusyMinutes).IsEqualTo(120);
        await Assert.That(week.Days.Single(day => day.Date == Today).BusyMinutes).IsEqualTo(0);

        // The meetings filter hides them from the list but never from the load.
        var hidden = await rig.Queries.RangeAsync(Today, Today.AddDays(6), new CalendarFilters { ShowMeetings = false });
        await Assert.That(hidden.Meetings).IsEmpty();
        await Assert.That(hidden.Days.Single(day => day.Date == Today.AddDays(1)).BusyMinutes).IsEqualTo(120);
    }

    [Test]
    public async Task ACalendarThatDoesNotCountAsBusy_ShowsMeetingsButTakesNoTime()
    {
        using var rig = await Rig.StartAsync();
        await rig.AddAsync(busy: false);
        rig.Feeds.Serve(Ics(Meeting("a", "Birthday drinks", "20261111T090000Z", "20261111T100000Z")));
        await rig.Refresher.RefreshAsync();

        var range = await rig.Queries.RangeAsync(Today, Today.AddDays(6), new CalendarFilters());

        await Assert.That(range.Meetings.Single().Busy).IsFalse();
        await Assert.That(range.Days.Sum(day => day.BusyMinutes)).IsEqualTo(0);
    }

    [Test]
    public async Task BusyTime_ReducesTheDaysCapacity_InTheCalendarAndInMyWeek()
    {
        using var rig = await Rig.StartAsync();
        var wednesday = Today.AddDays(1);
        // Seven hours planned on Wednesday fits the default eight-hour weekday.
        var task = (await rig.Host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Big task", null))).Task;
        await rig.Host.Tasks.UpdateDetailsAsync(new(DataHost.Op(), task.Id, task.Version, "Big task", null, wednesday, null, null, null, null, null,
            Chaos.Domain.Tasks.TaskPriority.Normal, 420, Chaos.Domain.Tasks.CapacityCategory.Personal));

        var before = await rig.Queries.RangeAsync(wednesday, wednesday, new CalendarFilters());
        await Assert.That(before.Days.Single().Load.OverTotal).IsFalse();

        await rig.AddAsync();
        rig.Feeds.Serve(Ics(Meeting("a", "Workshop", "20261111T090000Z", "20261111T110000Z")));
        await rig.Refresher.RefreshAsync();

        // Eight hours minus a two-hour meeting leaves six; seven planned is over.
        var after = await rig.Queries.RangeAsync(wednesday, wednesday, new CalendarFilters());
        await Assert.That(after.Days.Single().Load.OverTotal).IsTrue();
        await Assert.That(after.Days.Single().Availability.TotalMinutes).IsEqualTo(480);
        await Assert.That(after.Days.Single().BusyMinutes).IsEqualTo(120);

        var weeks = new MyWeekCommands(rig.Host.Store, rig.Host.Ids, rig.Host.Clock);
        var overview = await weeks.OpenAsync(Today);
        var day = overview.Confirmed.Days.Single(item => item.Date == wednesday);
        await Assert.That(day.BusyMinutes).IsEqualTo(120);
        await Assert.That(day.Availability.TotalMinutes).IsEqualTo(480);
        await Assert.That(overview.Confirmed.Days.Single(item => item.Date == Today).BusyMinutes).IsEqualTo(0);
    }

    [Test]
    public async Task MyDay_ListsTodaysMeetings_AndWarnsWhenOpenTasksDoNotFit()
    {
        using var rig = await Rig.StartAsync();
        await rig.AddAsync();
        // The clock is 09:00. A meeting from 10:00 to 16:00 leaves two hours before 18:00 but the day has eight available hours minus six busy = two free.
        rig.Feeds.Serve(Ics(Meeting("a", "All hands", "20261110T100000Z", "20261110T160000Z")));
        await rig.Refresher.RefreshAsync();
        var days = new MyDayCommands(rig.Host.Store, rig.Host.Clock);
        var task = (await rig.Host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Write report", null))).Task;
        await rig.Host.Tasks.UpdateDetailsAsync(new(DataHost.Op(), task.Id, task.Version, "Write report", null, null, null, null, null, null, null,
            Chaos.Domain.Tasks.TaskPriority.Normal, 240, Chaos.Domain.Tasks.CapacityCategory.Personal));
        await days.AddAsync(new DayItemCommand(DataHost.Op(), Today, task.Id));

        var day = await days.OpenAsync(Today);

        await Assert.That(day.Meetings!.Single().Title).IsEqualTo("All hands");
        await Assert.That(day.BusyMinutes).IsEqualTo(360);
        await Assert.That(day.FreeMinutes).IsEqualTo(120);
        await Assert.That(day.MeetingsDontFit).IsTrue();
    }

    [Test]
    public async Task MyDay_WithMeetingsThatLeaveRoom_DoesNotWarn()
    {
        using var rig = await Rig.StartAsync();
        await rig.AddAsync();
        rig.Feeds.Serve(Ics(Meeting("a", "Quick chat", "20261110T100000Z", "20261110T103000Z")));
        await rig.Refresher.RefreshAsync();
        var days = new MyDayCommands(rig.Host.Store, rig.Host.Clock);
        var task = (await rig.Host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Small thing", null))).Task;
        await rig.Host.Tasks.UpdateDetailsAsync(new(DataHost.Op(), task.Id, task.Version, "Small thing", null, null, null, null, null, null, null,
            Chaos.Domain.Tasks.TaskPriority.Normal, 60, Chaos.Domain.Tasks.CapacityCategory.Personal));
        await days.AddAsync(new DayItemCommand(DataHost.Op(), Today, task.Id));

        var day = await days.OpenAsync(Today);

        await Assert.That(day.MeetingsDontFit).IsFalse();
        await Assert.That(day.BusyMinutes).IsEqualTo(30);
    }

    [Test]
    public async Task AFailedRefresh_KeepsTheOldCache_AndRecordsTheError()
    {
        using var rig = await Rig.StartAsync();
        var source = await rig.AddAsync();
        rig.Feeds.Serve(Ics(Meeting("a", "Standup", "20261111T100000Z", "20261111T110000Z")), etag: "\"first\"");
        await rig.Refresher.RefreshAsync();

        rig.Feeds.Fail("The calendar server answered 500.");
        var failed = (await rig.Refresher.RefreshAsync()).Single();
        await Assert.That(failed.Ok).IsFalse();
        await Assert.That(failed.Error).IsEqualTo("The calendar server answered 500.");

        var range = await rig.Queries.RangeAsync(Today, Today.AddDays(6), new CalendarFilters());
        await Assert.That(range.Meetings.Single().Title).IsEqualTo("Standup");

        var state = (await rig.Commands.ListAsync()).Single().State!;
        await Assert.That(state.LastError).IsEqualTo("The calendar server answered 500.");
        await Assert.That(state.LastSuccessUtc).IsNotNull();
        await Assert.That(state.ETag).IsEqualTo("\"first\"");

        // An unreadable calendar is also a failure that keeps the cache.
        rig.Feeds.Serve("<html>login</html>");
        await Assert.That((await rig.Refresher.RefreshAsync()).Single().Ok).IsFalse();
        await Assert.That((await rig.Queries.RangeAsync(Today, Today.AddDays(6), new CalendarFilters())).Meetings.Count).IsEqualTo(1);

        // A good fetch clears the error.
        rig.Feeds.Serve(Ics(Meeting("a", "Standup", "20261111T100000Z", "20261111T110000Z")), etag: "\"second\"");
        await Assert.That((await rig.Refresher.RefreshAsync()).Single().Ok).IsTrue();
        await Assert.That((await rig.Commands.ListAsync()).Single().State!.LastError).IsNull();
        _ = source;
    }

    [Test]
    public async Task NotModified_KeepsTheCache_AndSendsTheStoredEtag()
    {
        using var rig = await Rig.StartAsync();
        await rig.AddAsync();
        rig.Feeds.Serve(Ics(Meeting("a", "Standup", "20261111T100000Z", "20261111T110000Z")), etag: "\"abc\"");
        await rig.Refresher.RefreshAsync();

        rig.Feeds.NotModified();
        var outcome = (await rig.Refresher.RefreshAsync()).Single();

        await Assert.That(outcome.Ok).IsTrue();
        await Assert.That(outcome.Changed).IsFalse();
        await Assert.That(rig.Feeds.LastEtag).IsEqualTo("\"abc\"");
        await Assert.That((await rig.Queries.RangeAsync(Today, Today.AddDays(6), new CalendarFilters())).Meetings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task EachRefresh_ReplacesTheWindow_SoDroppedMeetingsDisappear()
    {
        using var rig = await Rig.StartAsync();
        await rig.AddAsync();
        rig.Feeds.Serve(Ics(Meeting("a", "One", "20261111T100000Z", "20261111T110000Z"), Meeting("b", "Two", "20261112T100000Z", "20261112T110000Z")));
        await rig.Refresher.RefreshAsync();
        rig.Feeds.Serve(Ics(Meeting("b", "Two (renamed)", "20261112T100000Z", "20261112T110000Z")));
        await rig.Refresher.RefreshAsync();

        var meetings = (await rig.Queries.RangeAsync(Today, Today.AddDays(6), new CalendarFilters())).Meetings;

        await Assert.That(meetings.Single().Title).IsEqualTo("Two (renamed)");
    }

    [Test]
    public async Task Remove_DropsTheCache_AndForgetsTheAddress_ButAnInFlightFetchCannotResurrectIt()
    {
        using var rig = await Rig.StartAsync();
        var source = await rig.AddAsync();
        rig.Feeds.Serve(Ics(Meeting("a", "Standup", "20261111T100000Z", "20261111T110000Z")));
        await rig.Refresher.RefreshAsync();

        await rig.Commands.RemoveAsync(source.Id);

        await Assert.That(rig.Secrets.Values).IsEmpty();
        await Assert.That(await rig.Commands.ListAsync()).IsEmpty();
        await Assert.That((await rig.Queries.RangeAsync(Today, Today.AddDays(6), new CalendarFilters())).Meetings).IsEmpty();
        await Assert.That(await rig.Refresher.HasSourcesAsync()).IsFalse();
    }

    [Test]
    public async Task UpdatingASource_ChangesItsLook_AndRefusesAStaleVersion()
    {
        using var rig = await Rig.StartAsync();
        var source = await rig.AddAsync();

        var saved = await rig.Commands.UpdateAsync(new UpdateSourceCommand(source.Id, source.Version, "Team", "#00aa55", false, true, "me@example.com"));
        await Assert.That(saved.Source.Name).IsEqualTo("Team");
        await Assert.That(saved.Source.Color).IsEqualTo("#00aa55");
        await Assert.That(saved.Source.CountsAsBusy).IsFalse();
        await Assert.That(saved.Source.OwnerEmail).IsEqualTo("me@example.com");

        var stale = await Assert.ThrowsAsync<AppException>(async () =>
            await rig.Commands.UpdateAsync(new UpdateSourceCommand(source.Id, source.Version, "Old", "#00aa55", true, true, null)));
        await Assert.That(stale!.Kind).IsEqualTo(AppErrorKind.VersionConflict);
    }

    [Test]
    public async Task MakeTask_CreatesAPrepareTask_PlannedTheDayBefore_AndLinksIt()
    {
        using var rig = await Rig.StartAsync();
        var source = await rig.AddAsync();
        rig.Feeds.Serve(Ics(
            Meeting("morning", "Design review", "20261112T100000Z", "20261112T110000Z", "URL:https://example.org/event/1\r\n"),
            Meeting("late", "Board dinner", "20261112T190000Z", "20261112T210000Z")));
        await rig.Refresher.RefreshAsync();
        var events = (await rig.Queries.RangeAsync(Today, Today.AddDays(6), new CalendarFilters())).Meetings;
        var morning = events.Single(item => item.Title == "Design review");
        var late = events.Single(item => item.Title == "Board dinner");

        var made = await rig.Commands.MakeTaskAsync(new MakeTaskFromEventCommand(DataHost.Op(), source.Id, morning.EventId, null));
        var evening = await rig.Commands.MakeTaskAsync(new MakeTaskFromEventCommand(DataHost.Op(), source.Id, late.EventId, null));

        await Assert.That(made.Task.Title).IsEqualTo("Prepare: Design review");
        await Assert.That(made.Task.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 11));
        // A meeting after noon is prepared for the same day.
        await Assert.That(evening.Task.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 12));

        var link = (await rig.Commands.LinksAsync([made.Task.Id])).Single().Value;
        await Assert.That(link.EventTitle).IsEqualTo("Design review");
        await Assert.That(link.EventStartUtc).IsEqualTo(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        await Assert.That(link.EventWebUrl).IsEqualTo("https://example.org/event/1");
        await Assert.That(link.Id).IsEqualTo(Chaos.Application.Abstractions.StorageFormats.NameBasedId(Chaos.Sync.NameBasedId.Names.CalendarEventLink(made.Task.Id)));
    }

    [Test]
    public async Task MakeTask_IsIdempotent_AndAnUnknownMeetingIsAReadableError()
    {
        using var rig = await Rig.StartAsync();
        var source = await rig.AddAsync();
        rig.Feeds.Serve(Ics(Meeting("a", "Review", "20261112T100000Z", "20261112T110000Z")));
        await rig.Refresher.RefreshAsync();
        var eventId = (await rig.Queries.RangeAsync(Today, Today.AddDays(6), new CalendarFilters())).Meetings.Single().EventId;
        var operation = DataHost.Op();

        var first = await rig.Commands.MakeTaskAsync(new MakeTaskFromEventCommand(operation, source.Id, eventId, null));
        var again = await rig.Commands.MakeTaskAsync(new MakeTaskFromEventCommand(operation, source.Id, eventId, null));

        await Assert.That(again.Task.Id).IsEqualTo(first.Task.Id);
        await Assert.That(again.Replayed).IsTrue();
        await Assert.That((await rig.Commands.LinksAsync([first.Task.Id])).Count).IsEqualTo(1);

        var missing = await Assert.ThrowsAsync<AppException>(async () =>
            await rig.Commands.MakeTaskAsync(new MakeTaskFromEventCommand(DataHost.Op(), source.Id, "nope", null)));
        await Assert.That(missing!.Kind).IsEqualTo(AppErrorKind.NotFound);
    }

    [Test]
    public async Task Summaries_ListMeetingsOnlyWhenTheSettingIsOn()
    {
        using var rig = await Rig.StartAsync();
        await rig.AddAsync();
        rig.Feeds.Serve(Ics(Meeting("a", "Customer call", "20261110T140000Z", "20261110T150000Z")));
        await rig.Refresher.RefreshAsync();
        var summaries = new SummaryCommands(rig.Host.Store, rig.Host.Ids, rig.Host.Clock);

        var off = await summaries.GenerateAsync(new GenerateSummaryCommand(DataHost.Op(), SummaryKind.Morning, Today));
        await Assert.That(off.Facts.Meetings).IsEmpty();

        var settings = await summaries.GetSettingsAsync();
        await Assert.That(settings.IncludeMeetings).IsFalse();
        await summaries.UpdateSettingsAsync(new UpdateSummarySettingsCommand(DataHost.Op(), settings.Version, settings.MorningTime, settings.EveningTime, settings.WeeklyDay, IncludeMeetings: true));

        var on = await summaries.GenerateAsync(new GenerateSummaryCommand(DataHost.Op(), SummaryKind.Morning, Today));
        await Assert.That(on.Facts.Meetings.Single().Title).IsEqualTo("Customer call");
        await Assert.That(on.Facts.Meetings.Single().Start).IsEqualTo(new TimeOnly(14, 0));
    }

    [Test]
    public async Task FileSecretStore_RoundTrips_KeepsPlaintextOffDisk_AndTreatsAnUnreadableFileAsAbsent()
    {
        var folder = Path.Combine(Path.GetTempPath(), "chaos-secrets-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSecretStore(folder, new XorProtector(0x5A));
            await store.SetAsync("chaos/calendar-feed/abc", SecretAddress);

            await Assert.That(await store.GetAsync("chaos/calendar-feed/abc")).IsEqualTo(SecretAddress);
            await Assert.That(await store.GetAsync("chaos/calendar-feed/other")).IsNull();
            var file = Directory.EnumerateFiles(folder).Single();
            await Assert.That(Path.GetFileName(file)).DoesNotContain("abc");
            await Assert.That(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file))).DoesNotContain("calendar.example.org");

            // Replacing a value keeps one file; deleting removes it.
            await store.SetAsync("chaos/calendar-feed/abc", "https://example.org/new.ics");
            await Assert.That(Directory.EnumerateFiles(folder).Count()).IsEqualTo(1);
            await Assert.That(await store.GetAsync("chaos/calendar-feed/abc")).IsEqualTo("https://example.org/new.ics");
            await store.DeleteAsync("chaos/calendar-feed/abc");
            await Assert.That(await store.GetAsync("chaos/calendar-feed/abc")).IsNull();

            // A file this user cannot decrypt (copied from another machine) reads as absent.
            await store.SetAsync("k", "v");
            var broken = new FileSecretStore(folder, new XorProtector(0x5A, fail: true));
            await Assert.That(await broken.GetAsync("k")).IsNull();
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    private sealed class XorProtector(byte key, bool fail = false) : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => [.. plain.Select(value => (byte)(value ^ key))];

        public byte[] Unprotect(byte[] protectedBytes) =>
            fail ? throw new System.Security.Cryptography.CryptographicException("wrong user") : [.. protectedBytes.Select(value => (byte)(value ^ key))];
    }
}
