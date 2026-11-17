using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Application.Reminders;

namespace Chaos.Application.Calendar;

public sealed record RefreshOutcome(string SourceId, bool Ok, bool Changed, int EventCount, string? Error);

/// <summary>
/// Fetches the feeds and keeps the device-local meeting cache (PLAN2 §10.2). Network and parsing happen
/// outside any transaction; the cache window of a source is then replaced in one transaction. A failed
/// fetch keeps the old cache and records the error, shown in Settings. With no source at all nothing is
/// fetched and no request is made.
/// </summary>
public sealed class CalendarRefresher(IApplicationStore store, ISecretStore secrets, ICalendarFeedClient feeds, IIcsParser parser, TimeProvider clock, IGoogleCalendarClient? google = null)
{
    public const int DaysBack = 7;
    public const int DaysAhead = 56;
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    /// <summary>Google calendars are fetched every 5 minutes and on window focus (PLAN2 §10.3); the timer ticks at this pace.</summary>
    public static readonly TimeSpan GoogleInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DueSlack = TimeSpan.FromSeconds(30);

    private bool Fetchable(CalendarSourceRecord source) =>
        source.Enabled && (source.Provider == CalendarProvider.Feed || (source.Provider == CalendarProvider.Google && google is not null));

    private static bool IsDue(CalendarSourceRecord source, CalendarSourceState? state, DateTimeOffset now)
    {
        var interval = source.Provider == CalendarProvider.Google ? GoogleInterval : Interval;
        return state?.LastAttemptUtc is not { } last || now - last >= interval - DueSlack;
    }

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _googleActive;
    private int _feedActive;

    /// <summary>What is being fetched right now, whoever started it (the timer, window focus or a button). The sidebar logo shows it.</summary>
    public (bool Google, bool Feed) Activity => (Volatile.Read(ref _googleActive) > 0, Volatile.Read(ref _feedActive) > 0);

    /// <summary>True when at least one feed or Google source is enabled; the host fetches only then.</summary>
    public async Task<bool> HasSourcesAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) => (await session.Calendars.ListSourcesAsync(token)).Any(Fetchable), ct);

    /// <summary>Runs <paramref name="work"/> while no refresh is writing (restore and reset replace the database).</summary>
    public async Task RunPausedAsync(Func<Task> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await work();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Refreshes one source, or every enabled source when <paramref name="sourceId"/> is null. With <paramref name="onlyDue"/>
    /// a source fetched recently enough (5 minutes for Google, 15 for a feed) is skipped, so the timer and window focus can
    /// both call this without doubling the requests.
    /// </summary>
    public async Task<IReadOnlyList<RefreshOutcome>> RefreshAsync(string? sourceId = null, CancellationToken ct = default, bool onlyDue = false)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var plan = await store.QueryAsync(async (session, token) =>
            {
                var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
                var sources = (await session.Calendars.ListSourcesAsync(token))
                    .Where(source => Fetchable(source) && (sourceId is null || source.Id == sourceId))
                    .ToList();
                var states = new Dictionary<string, CalendarSourceState?>();
                foreach (var source in sources)
                {
                    states[source.Id] = await session.Calendars.GetStateAsync(source.Id, token);
                }

                if (onlyDue)
                {
                    var moment = clock.GetUtcNow();
                    sources = [.. sources.Where(source => IsDue(source, states[source.Id], moment))];
                }

                return (sources, states, context.Zone);
            }, ct);

            var outcomes = new List<RefreshOutcome>();
            var blockedAccounts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in plan.sources)
            {
                var isGoogle = source.Provider == CalendarProvider.Google;
                Interlocked.Increment(ref isGoogle ? ref _googleActive : ref _feedActive);
                try
                {
                    outcomes.Add(isGoogle
                        ? await RefreshGoogleAsync(source, plan.states[source.Id], blockedAccounts, ct)
                        : await RefreshOneAsync(source, plan.states[source.Id], plan.Zone, ct));
                }
                finally
                {
                    Interlocked.Decrement(ref isGoogle ? ref _googleActive : ref _feedActive);
                }
            }

            return outcomes;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// One Google calendar: the whole window is fetched (a sync token cannot be combined with a time window, so every refresh
    /// replaces the cache). When an account needs a new sign-in, is blocked or is offline, its other calendars in this run are
    /// not even tried.
    /// </summary>
    private async Task<RefreshOutcome> RefreshGoogleAsync(CalendarSourceRecord source, CalendarSourceState? state, Dictionary<string, string> blockedAccounts, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var accountId = source.AccountRef ?? string.Empty;
        if (blockedAccounts.TryGetValue(accountId, out var blocked))
        {
            await SaveFailureAsync(source, state, now, blocked, ct);
            return new RefreshOutcome(source.Id, false, false, 0, blocked);
        }

        try
        {
            var token = await secrets.GetAsync(GoogleCalendarCommands.SecretKey(accountId), ct)
                ?? throw new GoogleCalendarException(GoogleFailure.SignInAgain, GoogleCalendarCommands.SignInHereMessage);
            var fetched = await google!.ListEventsAsync(token, source.ExternalId, now.AddDays(-DaysBack), now.AddDays(DaysAhead), ct);
            var events = fetched
                .Select(item => new CalendarEventRecord(source.Id, item.ExternalId, item.StartUtc, item.EndUtc, item.AllDay, item.StartDate, item.EndDate,
                    item.Title, item.Location, item.VideoUrl, item.WebUrl, item.Busy, now))
                .ToList();
            await SaveAsync(new CalendarSourceState(source.Id, now, now, null, null, null), events, ct, accountId);
            return new RefreshOutcome(source.Id, true, true, events.Count, null);
        }
        catch (GoogleCalendarException error)
        {
            var message = error.Message == GoogleCalendarCommands.SignInHereMessage ? error.Message : GoogleCalendarCommands.MessageOf(error);
            if (error.Kind is GoogleFailure.SignInAgain or GoogleFailure.Blocked or GoogleFailure.Offline)
            {
                blockedAccounts[accountId] = message;
            }

            await SaveFailureAsync(source, state, now, message, ct, accountId, error.Kind == GoogleFailure.SignInAgain);
            return new RefreshOutcome(source.Id, false, false, 0, message);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            const string message = "Something went wrong while reading Google Calendar.";
            await SaveFailureAsync(source, state, now, message, ct, accountId);
            return new RefreshOutcome(source.Id, false, false, 0, message);
        }
    }

    private async Task<RefreshOutcome> RefreshOneAsync(CalendarSourceRecord source, CalendarSourceState? state, TimeZoneInfo zone, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        try
        {
            var address = await secrets.GetAsync(CalendarCommands.SecretKey(source.Id), ct)
                ?? throw new CalendarFeedException("This device does not have the calendar's address. Remove the calendar and add it again.");
            var fetched = await feeds.FetchAsync(new Uri(address), state?.ETag, state?.LastModified, ct);
            if (fetched.Status == FeedFetchStatus.NotModified)
            {
                await SaveAsync(new CalendarSourceState(source.Id, now, now, null, state?.ETag, state?.LastModified), null, ct);
                return new RefreshOutcome(source.Id, true, false, 0, null);
            }

            var events = parser.Parse(fetched.Text ?? string.Empty, new IcsParseOptions(now.AddDays(-DaysBack), now.AddDays(DaysAhead), zone, source.OwnerEmail))
                .Select(item => new CalendarEventRecord(source.Id, item.ExternalId, item.StartUtc, item.EndUtc, item.AllDay, item.StartDate, item.EndDate,
                    item.Title, item.Location, item.VideoUrl, item.WebUrl, item.Busy, now))
                .ToList();
            await SaveAsync(new CalendarSourceState(source.Id, now, now, null, fetched.ETag, fetched.LastModified), events, ct);
            return new RefreshOutcome(source.Id, true, true, events.Count, null);
        }
        catch (CalendarFeedException error)
        {
            await SaveFailureAsync(source, state, now, error.Message, ct);
            return new RefreshOutcome(source.Id, false, false, 0, error.Message);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            const string message = "Something went wrong while reading the calendar.";
            await SaveFailureAsync(source, state, now, message, ct);
            return new RefreshOutcome(source.Id, false, false, 0, message);
        }
    }

    /// <summary>One transaction: the whole window is replaced and the state recorded together.</summary>
    private async Task SaveAsync(CalendarSourceState state, IReadOnlyList<CalendarEventRecord>? events, CancellationToken ct, string? googleAccountId = null) =>
        await store.ExecuteAsync(async (session, token) =>
        {
            // The source may have been removed while the fetch was running; do not resurrect its cache.
            if (await session.Calendars.GetSourceAsync(state.SourceId, token) is not { DeletedAtUtc: null })
            {
                return 0;
            }

            if (events is not null)
            {
                await session.Calendars.ReplaceEventsAsync(state.SourceId, events, token);
            }

            await session.Calendars.SaveStateAsync(state, token);
            if (googleAccountId is not null && await session.Calendars.GetAccountAsync(googleAccountId, token) is { DeletedAtUtc: null })
            {
                await session.Calendars.SaveAccountStateAsync(new GoogleAccountState(googleAccountId, false, state.LastAttemptUtc, state.LastSuccessUtc, null), token);
            }

            return 0;
        }, ct);

    private async Task SaveFailureAsync(CalendarSourceRecord source, CalendarSourceState? state, DateTimeOffset now, string error, CancellationToken ct, string? googleAccountId = null, bool needsSignIn = false) =>
        await store.ExecuteAsync(async (session, token) =>
        {
            if (await session.Calendars.GetSourceAsync(source.Id, token) is not { DeletedAtUtc: null })
            {
                return 0;
            }

            // The cache stays as it was; only the error and the attempt time change.
            await session.Calendars.SaveStateAsync(new CalendarSourceState(source.Id, now, state?.LastSuccessUtc, error, state?.ETag, state?.LastModified), token);
            if (googleAccountId is not null && await session.Calendars.GetAccountAsync(googleAccountId, token) is { DeletedAtUtc: null })
            {
                var previous = await session.Calendars.GetAccountStateAsync(googleAccountId, token);
                await session.Calendars.SaveAccountStateAsync(new GoogleAccountState(googleAccountId, needsSignIn, now, previous?.LastSuccessUtc, error), token);
            }

            return 0;
        }, ct);
}
