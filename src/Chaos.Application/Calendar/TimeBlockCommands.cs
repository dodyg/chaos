using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Calendar;

public sealed record TimeBlockSpaceOption(string Id, string Name, bool Personal, bool Chosen);
public sealed record TimeBlockAccountOption(string Id, string Email, bool SignedInHere);

/// <summary>Time blocking as Settings shows it. <paramref name="IsRunner"/> says this device pushes; the spaces are every live space with the person's choice.</summary>
public sealed record TimeBlockStatus(
    bool Enabled,
    string? AccountId,
    string? AccountEmail,
    bool ShowTitles,
    IReadOnlyList<TimeBlockSpaceOption> Spaces,
    string? CalendarId,
    string? RunnerDeviceId,
    bool IsRunner,
    DateTimeOffset? LastPassUtc,
    string? LastError,
    bool NeedsSignIn,
    int EventCount,
    long ProfileVersion,
    IReadOnlyList<TimeBlockAccountOption> Accounts);

public sealed record EnableTimeBlockCommand(string AccountId, bool ShowTitles, IReadOnlyList<string>? SpaceIds);
/// <summary>SpaceIds null or empty means every space.</summary>
public sealed record UpdateTimeBlockCommand(long ExpectedVersion, bool ShowTitles, IReadOnlyList<string>? SpaceIds);
public sealed record TimeBlockPassResult(bool Ran, string? Error, bool NeedsSignIn, int Pushed, int Updated, int Removed, int Moved, int Conflicts);
public sealed record TimeBlockDisableResult(TimeBlockStatus Status, bool CalendarDeleted);

/// <summary>
/// The client event id of a task's block (PLAN3 §11): <c>cb</c> plus the base32hex encoding of 16 bytes, which is the
/// shape Google requires (5–1024 characters, a–v and 0–9). A GUID task id gives its own bytes; any other id (a recurrence
/// occurrence, a GitHub issue) gives the first 16 bytes of its SHA-256. Two devices therefore derive the same id, and
/// Google refuses a second insert with 409.
/// </summary>
public static class TimeBlockEventId
{
    public const string Prefix = "cb";
    /// <summary>The length of every derived id: the prefix and 26 base32hex characters.</summary>
    public const int Length = 28;
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuv";

    public static string Of(string taskId)
    {
        var bytes = Guid.TryParseExact(taskId, "D", out var guid) ? guid.ToByteArray() : SHA256.HashData(Encoding.UTF8.GetBytes(taskId))[..16];
        var text = new char[26];
        var buffer = 0L;
        var bits = 0;
        var index = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                text[index++] = Alphabet[(int)((buffer >> bits) & 31)];
            }
        }

        text[index] = Alphabet[(int)((buffer << (5 - bits)) & 31)];
        return Prefix + new string(text);
    }
}

/// <summary>The one scope time blocking needs beyond the read-only ones (PLAN3 §11).</summary>
public static class GoogleCalendarScopes
{
    public const string AppCreated = "https://www.googleapis.com/auth/calendar.app.created";
}

/// <summary>
/// Google Calendar time blocking (PLAN3 §11). The settings are the profile's <c>timeblock</c> group; the one event per
/// task is a <c>TimeBlockEvents</c> row; the change-feed position is this device's. Exactly one device, the runner,
/// talks to Google, and only ever to the calendar the app created. Every Google call happens outside a write
/// transaction: a pass reads a plan, calls Google, and writes each row's result in its own short transaction. With the
/// feature off, or on a device that is not the runner, a pass makes no request at all.
/// </summary>
public sealed class TimeBlockCommands(IApplicationStore store, ISecretStore secrets, IGoogleCalendarClient google, IIdGenerator ids, TimeProvider clock)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    public const string CalendarName = "Chaos";
    /// <summary>The history actor of a change Google made (PLAN3 §11: "a normal task edit with Source=sync and actor google").</summary>
    public const string Actor = "google";
    public const string MovedEvent = "google.moved";
    public const string ClearedEvent = "google.cleared";
    public const string ConflictEvent = "google.conflict";
    private const int MaxPages = 20;

    public bool IsConfigured => google.IsConfigured;

    /// <summary>The block a task should be: its title or Busy, start and end from the planned date, time and zone, and the hash of all of it.</summary>
    public sealed record Block(string? Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string TimeZoneId, string Hash);

    private sealed record Plan(string AccountId, string CalendarId, bool ShowTitles, IReadOnlyList<(TaskRecord Task, Block Block)> Eligible,
        Dictionary<string, TimeBlockEventRecord> Events, TimeBlockState? State, TimeZoneInfo Zone);

    public Task<TimeBlockStatus> StatusAsync(CancellationToken ct = default) =>
        store.QueryAsync((session, token) => StatusOfAsync(session, token), ct);

    /// <summary>The sign-in for a device whose token expired or was revoked ("sign in again"): the write scope only, then a pass.</summary>
    /// <remarks>With <paramref name="runPass"/> false the caller runs the pass itself (the desktop runs it through the scheduler's gate, so it never overlaps a scheduled pass or a restore).</remarks>
    public async Task<TimeBlockStatus> SignInAgainAsync(CancellationToken ct = default, bool runPass = true)
    {
        var account = await store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token);
            return profile is { TimeBlockEnabled: true, TimeBlockAccountId: { } id } ? await session.Calendars.GetAccountAsync(id, token) : null;
        }, ct) ?? throw AppException.Validation("timeBlock", "Turn on time blocking first.");
        await SignInForAsync(account, ct);
        if (runPass)
        {
            await SyncAsync(ct);
        }

        return await StatusAsync(ct);
    }

    private async Task<TimeBlockStatus> StatusOfAsync(IDataSession session, CancellationToken token)
    {
        var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
        var chosen = ParseSpaces(profile.TimeBlockSpacesJson);
        var options = new List<TimeBlockSpaceOption>();
        foreach (var space in (await session.Spaces.ListAsync(token)).Where(space => space.DeletedAtUtc is null)
                     .OrderBy(space => space.Kind == SpaceKind.Personal ? 0 : 1).ThenBy(space => space.Name, StringComparer.CurrentCulture))
        {
            options.Add(new TimeBlockSpaceOption(space.Id, space.Name, space.Kind == SpaceKind.Personal, chosen.Count == 0 || chosen.Contains(space.Id)));
        }

        var accounts = new List<TimeBlockAccountOption>();
        foreach (var account in await session.Calendars.ListAccountsAsync(token))
        {
            accounts.Add(new TimeBlockAccountOption(account.Id, account.Email, await secrets.GetAsync(GoogleCalendarCommands.SecretKey(account.Id), token) is not null));
        }

        var deviceId = await session.TimeBlocks.GetDeviceIdAsync(token);
        var state = await session.TimeBlocks.GetStateAsync(token);
        var accountRow = profile.TimeBlockAccountId is { } accountId ? await session.Calendars.GetAccountAsync(accountId, token) : null;
        return new TimeBlockStatus(profile.TimeBlockEnabled, profile.TimeBlockAccountId, accountRow?.Email, profile.TimeBlockShowTitles, options,
            profile.TimeBlockCalendarId, profile.TimeBlockRunnerDeviceId, profile.TimeBlockEnabled && profile.TimeBlockRunnerDeviceId == deviceId,
            state?.LastPassUtc, state?.LastError, state?.NeedsSignIn ?? false, (await session.TimeBlocks.ListEventsAsync(token)).Count, profile.Version, accounts);
    }

    /// <summary>
    /// Turns time blocking on for an account. The person signs in to that account on this device if the token is missing
    /// (incremental consent: Google asks for the new permission only). The app calendar is created when the account has
    /// none yet, this device becomes the runner, and a first pass runs at once.
    /// </summary>
    public async Task<TimeBlockStatus> EnableAsync(EnableTimeBlockCommand command, CancellationToken ct = default, bool runPass = true)
    {
        var account = string.IsNullOrWhiteSpace(command.AccountId)
            ? null
            : await store.QueryAsync((session, token) => session.Calendars.GetAccountAsync(command.AccountId, token), ct) is { DeletedAtUtc: null } row ? row : null;
        if (account is null)
        {
            throw AppException.Validation("accountId", "Choose a Google account that is connected on this device.");
        }

        var stored = await secrets.GetAsync(GoogleCalendarCommands.SecretKey(account.Id), ct);
        var refreshToken = stored ?? await SignInForAsync(account, ct);
        var spaceIds = await ValidatedSpacesAsync(command.SpaceIds, null, ct);
        var previous = await store.QueryAsync(async (session, token) => await session.Profiles.GetPrimaryAsync(token), ct);
        string calendarId;
        try
        {
            // The calendar of the same account is reused (turned off without deleting it); another account gets its own.
            if (previous is { TimeBlockAccountId: { } owner, TimeBlockCalendarId: { } kept } && owner == account.Id)
            {
                calendarId = kept;
            }
            else
            {
                try
                {
                    calendarId = (await google.CreateCalendarAsync(refreshToken, CalendarName, ct)).Id;
                }
                catch (GoogleCalendarException error) when (error.Kind == GoogleFailure.SignInAgain && stored is not null)
                {
                    // The token on this device came from connecting the account, which asks for the read-only scopes: Google
                    // refuses the write. Sign in once for the time blocking permission (incremental consent) and try again.
                    refreshToken = await SignInForAsync(account, ct);
                    calendarId = (await google.CreateCalendarAsync(refreshToken, CalendarName, ct)).Id;
                }
            }
        }
        catch (GoogleCalendarException error)
        {
            throw Translate(error);
        }

        await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
            var deviceId = await session.TimeBlocks.GetDeviceIdAsync(token);
            var updated = profile with
            {
                TimeBlockEnabled = true, TimeBlockAccountId = account.Id, TimeBlockShowTitles = command.ShowTitles, TimeBlockRunnerDeviceId = deviceId,
                TimeBlockCalendarId = calendarId, TimeBlockSpacesJson = SpacesJson(spaceIds), Version = profile.Version + 1, UpdatedAtUtc = now,
            };
            if (await session.Profiles.UpdateTimeBlockSettingsAsync(updated, profile.Version, token) is null)
            {
                throw Conflict();
            }

            // Whatever sync token this device kept belonged to an earlier setup: the first pass reads the whole calendar.
            await session.TimeBlocks.SaveStateAsync(new TimeBlockState(calendarId, null, null, null, null, false), token);
            return 0;
        }, ct);
        if (runPass)
        {
            await SyncAsync(ct);
        }

        return await StatusAsync(ct);
    }

    public Task<TimeBlockStatus> UpdateAsync(UpdateTimeBlockCommand command, CancellationToken ct = default) =>
        store.ExecuteAsync(async (session, token) =>
        {
            var spaceIds = await ValidatedSpacesAsync(command.SpaceIds, session, token);
            var now = clock.GetUtcNow();
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
            if (!profile.TimeBlockEnabled)
            {
                throw AppException.Validation("timeBlock", "Turn on time blocking first.");
            }

            var updated = profile with { TimeBlockShowTitles = command.ShowTitles, TimeBlockSpacesJson = SpacesJson(spaceIds), Version = profile.Version + 1, UpdatedAtUtc = now };
            if (await session.Profiles.UpdateTimeBlockSettingsAsync(updated, command.ExpectedVersion, token) is null)
            {
                throw Conflict();
            }

            return await StatusOfAsync(session, token);
        }, ct);

    /// <summary>Moves the runner to this device. The token must exist here, because only the runner talks to Google.</summary>
    public Task<TimeBlockStatus> RunOnThisDeviceAsync(long expectedVersion, CancellationToken ct = default) =>
        store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
            if (!profile.TimeBlockEnabled)
            {
                throw AppException.Validation("timeBlock", "Turn on time blocking first.");
            }

            var deviceId = await session.TimeBlocks.GetDeviceIdAsync(token);
            if (profile.TimeBlockAccountId is not { } accountId || await secrets.GetAsync(GoogleCalendarCommands.SecretKey(accountId), token) is null)
            {
                throw new AppException(AppErrorKind.UnsupportedCapability, GoogleCalendarCommands.SignInHereMessage);
            }

            var updated = profile with { TimeBlockRunnerDeviceId = deviceId, Version = profile.Version + 1, UpdatedAtUtc = now };
            if (await session.Profiles.UpdateTimeBlockSettingsAsync(updated, expectedVersion, token) is null)
            {
                throw Conflict();
            }

            // This device's token position is void: it reads the whole calendar on its first pass.
            await session.TimeBlocks.SaveStateAsync(new TimeBlockState(profile.TimeBlockCalendarId, null, null, null, null, false), token);
            return await StatusOfAsync(session, token);
        }, ct);

    /// <summary>
    /// Turns time blocking off on every device: the settings clear and every event row goes. With <paramref name="deleteCalendar"/>
    /// the app calendar is deleted at Google first, so a Google that cannot be reached leaves the settings as they were and
    /// the person can try again. The calendar id is kept when the calendar is kept, for a later turn-on.
    /// </summary>
    public async Task<TimeBlockDisableResult> DisableAsync(long expectedVersion, bool deleteCalendar, CancellationToken ct = default)
    {
        var (calendarId, refreshToken, version) = await store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
            return (profile.TimeBlockCalendarId, await secrets.GetAsync(GoogleCalendarCommands.SecretKey(profile.TimeBlockAccountId ?? string.Empty), token), profile.Version);
        }, ct);
        // A stale version fails before Google is touched, so a refused turn-off never leaves the setting on with a deleted calendar.
        if (version != expectedVersion)
        {
            throw Conflict();
        }

        if (deleteCalendar && calendarId is not null && refreshToken is null)
        {
            // Without the token here the calendar cannot be deleted; clearing its id would orphan it in Google.
            throw new AppException(AppErrorKind.UnsupportedCapability,
                "Sign in to Google on this device to delete the Chaos calendar, or turn off and keep the calendar.");
        }

        if (deleteCalendar && calendarId is not null)
        {
            try
            {
                await google.DeleteCalendarAsync(refreshToken!, calendarId, ct);
            }
            catch (GoogleCalendarException error)
            {
                throw Translate(error);
            }
        }

        var status = await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
            var updated = profile with
            {
                TimeBlockEnabled = false, TimeBlockRunnerDeviceId = null, TimeBlockSpacesJson = null,
                // A kept calendar is remembered with its account, so turning on again for that account reuses it instead of making a second one.
                TimeBlockAccountId = deleteCalendar ? null : profile.TimeBlockAccountId, TimeBlockCalendarId = deleteCalendar ? null : profile.TimeBlockCalendarId,
                Version = profile.Version + 1, UpdatedAtUtc = now,
            };
            if (await session.Profiles.UpdateTimeBlockSettingsAsync(updated, expectedVersion, token) is null)
            {
                throw Conflict();
            }

            foreach (var row in await session.TimeBlocks.ListEventsAsync(token))
            {
                await session.TimeBlocks.DeleteEventAsync(row.Id, token);
            }

            await session.TimeBlocks.SaveStateAsync(new TimeBlockState(updated.TimeBlockCalendarId, null, null, null, null, false), token);
            return await StatusOfAsync(session, token);
        }, ct);
        return new TimeBlockDisableResult(status, deleteCalendar && calendarId is not null);
    }

    /// <summary>
    /// True when a pass is due on this device: the feature is on, this device is the runner, and a task changed since the last
    /// pass (by the person or by sync, not by the inbound pass itself) or the interval has passed.
    /// </summary>
    public async Task<bool> IsDueAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return await store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token);
            if (profile is null || !profile.TimeBlockEnabled || profile.TimeBlockRunnerDeviceId != await session.TimeBlocks.GetDeviceIdAsync(token))
            {
                return false;
            }

            var state = await session.TimeBlocks.GetStateAsync(token);
            return await session.TimeBlocks.TaskActivityHeadAsync(state?.LastPassUtc, token) is not null
                || state?.LastPassUtc is not { } last || now - last >= Interval - TimeSpan.FromSeconds(30);
        }, ct);
    }

    /// <summary>
    /// One pass: inbound first (what changed in Google since last time is applied to the tasks under the conflict rule), then
    /// outbound (every eligible task has its event with matching title, start, end and visibility; a task that is no longer
    /// eligible loses its event).
    /// </summary>
    public async Task<TimeBlockPassResult> SyncAsync(CancellationToken ct = default)
    {
        var plan = await store.QueryAsync(PlanAsync, ct);
        if (plan is null)
        {
            return new TimeBlockPassResult(false, null, false, 0, 0, 0, 0, 0);
        }

        var refreshToken = await secrets.GetAsync(GoogleCalendarCommands.SecretKey(plan.AccountId), ct);
        if (refreshToken is null)
        {
            await SaveStateAsync(plan, state => state with { NeedsSignIn = true, LastError = GoogleCalendarCommands.SignInHereMessage }, ct);
            return new TimeBlockPassResult(true, GoogleCalendarCommands.SignInHereMessage, true, 0, 0, 0, 0, 0);
        }

        int pushed = 0, updated = 0, removed = 0;
        var now = clock.GetUtcNow();
        try
        {
            // Inbound first: a change already in Google is applied to its task, or loses to a newer local change under the
            // conflict rule, before the outbound pass would overwrite it with what the task held before that change.
            var inbound = await InboundAsync(plan, refreshToken, ct);
            if (inbound.Error is not null)
            {
                // Google's changes could not be read: pushing now could overwrite a move made in Google with no conflict
                // check. Nothing is written; the next pass tries both halves again.
                await SaveStateAsync(plan, state => state with { LastPassUtc = now, LastError = inbound.Error, NeedsSignIn = inbound.NeedsSignIn }, ct);
                return new TimeBlockPassResult(true, inbound.Error, inbound.NeedsSignIn, 0, 0, 0, inbound.Moved, inbound.Conflicts);
            }

            // The inbound pass may have moved tasks: plan the outbound half again on the tasks as they are now, so nothing
            // pushes a time the conflict rule just replaced.
            var outbound = await store.QueryAsync(PlanAsync, ct) ?? plan;

            foreach (var (task, block) in outbound.Eligible)
            {
                var row = outbound.Events.GetValueOrDefault(task.Id);
                if (row is not null && row.PushedHash == block.Hash && row.ETag is not null)
                {
                    continue;
                }

                var eventId = row?.EventId ?? TimeBlockEventId.Of(task.Id);
                GoogleEventWrite write;
                if (row is null)
                {
                    write = await google.InsertEventAsync(refreshToken, plan.CalendarId, Body(eventId, block), ct);
                    if (write.Conflict)
                    {
                        // Another device made the event before this one (a moved runner, a race): one event per task, so patch it.
                        write = await google.PatchEventAsync(refreshToken, plan.CalendarId, eventId, Body(null, block), ct);
                    }

                    pushed++;
                    var created = new TimeBlockEventRecord
                    {
                        Id = task.Id, EventId = eventId, ETag = write.ETag, PushedHash = block.Hash, LastSyncedAtUtc = now, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now,
                    };
                    await store.ExecuteAsync(async (session, token) => { await session.TimeBlocks.AddEventAsync(created, token); return 0; }, ct);
                }
                else
                {
                    write = await google.PatchEventAsync(refreshToken, plan.CalendarId, eventId, Body(null, block), ct);
                    if (write.NotFound)
                    {
                        write = await google.InsertEventAsync(refreshToken, plan.CalendarId, Body(eventId, block), ct);
                        if (write.Conflict)
                        {
                            write = await google.PatchEventAsync(refreshToken, plan.CalendarId, eventId, Body(null, block), ct);
                        }
                    }

                    updated++;
                    var previous = row;
                    await store.ExecuteAsync((session, token) => session.TimeBlocks.UpdateEventAsync(previous with
                    {
                        ETag = write.ETag ?? previous.ETag, PushedHash = block.Hash, LastSyncedAtUtc = now, Version = previous.Version + 1, UpdatedAtUtc = now,
                    }, previous.Version, token), ct);
                }
            }

            var eligibleIds = outbound.Eligible.Select(entry => entry.Task.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var row in outbound.Events.Values.Where(row => !eligibleIds.Contains(row.Id)))
            {
                await google.DeleteEventAsync(refreshToken, outbound.CalendarId, row.EventId, ct);
                removed++;
                await store.ExecuteAsync(async (session, token) => { await session.TimeBlocks.DeleteEventAsync(row.Id, token); return 0; }, ct);
            }

            // App events with no row (a calendar kept when time blocking was turned off, or a write whose row was never
            // saved) that belong to no eligible task are leftovers: nothing else would ever remove them.
            var wanted = outbound.Eligible.Select(entry => outbound.Events.GetValueOrDefault(entry.Task.Id)?.EventId ?? TimeBlockEventId.Of(entry.Task.Id))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var eventId in inbound.Untracked.Where(id => !wanted.Contains(id)))
            {
                await google.DeleteEventAsync(refreshToken, outbound.CalendarId, eventId, ct);
                removed++;
            }

            await SaveStateAsync(plan, state => state with { LastPassUtc = now, LastError = null, NeedsSignIn = false }, ct);
            return new TimeBlockPassResult(true, null, false, pushed, updated, removed, inbound.Moved, inbound.Conflicts);
        }
        catch (GoogleCalendarException error)
        {
            var message = GoogleCalendarCommands.MessageOf(error);
            await SaveStateAsync(plan, state => state with { LastPassUtc = now, LastError = message, NeedsSignIn = error.Kind == GoogleFailure.SignInAgain }, ct);
            return new TimeBlockPassResult(true, message, error.Kind == GoogleFailure.SignInAgain, pushed, updated, removed, 0, 0);
        }
    }

    /// <summary>What this device has to do, or null when time blocking is off here or another device is the runner: no request either way.</summary>
    private async Task<Plan?> PlanAsync(IDataSession session, CancellationToken token)
    {
        var profile = await session.Profiles.GetPrimaryAsync(token);
        if (profile is null || !profile.TimeBlockEnabled || profile.TimeBlockCalendarId is null || profile.TimeBlockAccountId is null
            || profile.TimeBlockRunnerDeviceId != await session.TimeBlocks.GetDeviceIdAsync(token))
        {
            return null;
        }

        var spaces = (await session.Spaces.ListAsync(token)).Where(space => space.DeletedAtUtc is null).ToDictionary(space => space.Id, StringComparer.Ordinal);
        var chosen = ParseSpaces(profile.TimeBlockSpacesJson);
        var zone = Zone(profile.ZoneId) ?? TimeZoneInfo.Utc;
        var eligible = new List<(TaskRecord, Block)>();
        foreach (var task in await session.Tasks.ListPlannedAsync(chosen.Count == 0 ? null : chosen, token))
        {
            if (!spaces.TryGetValue(task.SpaceId, out var space))
            {
                continue;
            }

            var mine = space.Kind == SpaceKind.Personal
                || (await session.Tasks.ListAssigneesAsync(task.Id, token)).Any(assignee => assignee.DeletedAtUtc is null && string.Equals(assignee.MemberId, profile.Id, StringComparison.OrdinalIgnoreCase));
            if (IsEligible(task, mine))
            {
                eligible.Add((task, Describe(task, profile.TimeBlockShowTitles, zone)));
            }
        }

        return new Plan(profile.TimeBlockAccountId, profile.TimeBlockCalendarId, profile.TimeBlockShowTitles, eligible,
            (await session.TimeBlocks.ListEventsAsync(token)).ToDictionary(row => row.Id, StringComparer.Ordinal), await session.TimeBlocks.GetStateAsync(token), zone);
    }

    /// <summary>Only open, not cancelled tasks with a planned date, a time and an estimate; a shared task only when it is assigned to this person (PLAN3 §11).</summary>
    internal static bool IsEligible(TaskRecord task, bool mine) =>
        !task.IsTerminal && task.PlannedDate is not null && task.PlannedTime is not null && task.EstimateMinutes is > 0 && mine;

    public static Block Describe(TaskRecord task, bool showTitles, TimeZoneInfo fallbackZone)
    {
        var zone = Zone(task.PlannedZoneId) ?? fallbackZone;
        var local = DateTime.SpecifyKind(task.PlannedDate!.Value.ToDateTime(task.PlannedTime!.Value), DateTimeKind.Unspecified);
        // A time inside a spring-forward gap does not exist (ConvertTimeToUtc throws): the offset in force before the gap
        // places the block just after it, as reminders do.
        var start = zone.IsInvalidTime(local)
            ? new DateTimeOffset(local, zone.GetUtcOffset(local.AddHours(-12))).ToUniversalTime()
            : new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
        var end = start.AddMinutes(task.EstimateMinutes!.Value);
        var title = showTitles ? task.Title : null;
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{title}\n{start:o}\n{end:o}\n{showTitles}")));
        return new Block(title, start, end, zone.Id, hash);
    }

    private static GoogleEventBody Body(string? eventId, Block block) => new(eventId, block.Title, block.StartUtc, block.EndUtc, block.TimeZoneId);

    private async Task<(int Moved, int Conflicts, string? Error, bool NeedsSignIn, IReadOnlySet<string> Untracked)> InboundAsync(Plan plan, string refreshToken, CancellationToken ct)
    {
        // Live app events this device has no row for, by the shape of the derived id (the prefix and its 28 characters).
        var untracked = new HashSet<string>(StringComparer.Ordinal);
        var state = plan.State ?? new TimeBlockState(null, null, null, null, null, false);
        if (!string.Equals(state.CalendarId, plan.CalendarId, StringComparison.Ordinal))
        {
            state = state with { CalendarId = plan.CalendarId, SyncToken = null, PageToken = null };
        }

        int moved = 0, conflicts = 0;
        var byEvent = plan.Events.Values.ToDictionary(row => row.EventId, row => row.Id, StringComparer.Ordinal);
        var syncToken = state.SyncToken;
        var pageToken = state.PageToken;
        for (var pages = 0; pages < MaxPages; pages++)
        {
            GoogleSyncPage page;
            try
            {
                page = await google.ListEventChangesAsync(refreshToken, plan.CalendarId, syncToken, pageToken, ct);
                if (page.TokenExpired)
                {
                    // Google no longer knows the token (90 days, or the calendar changed): read everything once more. The
                    // app's own events are recognised by their etag, so nothing it wrote is applied back to a task.
                    syncToken = null;
                    pageToken = null;
                    page = await google.ListEventChangesAsync(refreshToken, plan.CalendarId, null, null, ct);
                }
            }
            catch (GoogleCalendarException error)
            {
                return (moved, conflicts, GoogleCalendarCommands.MessageOf(error), error.Kind == GoogleFailure.SignInAgain, untracked);
            }

            foreach (var change in page.Items)
            {
                if (!change.Id.StartsWith(TimeBlockEventId.Prefix, StringComparison.Ordinal))
                {
                    continue; // not the app's event
                }

                if (!byEvent.TryGetValue(change.Id, out var taskId))
                {
                    if (change.Cancelled)
                    {
                        untracked.Remove(change.Id);
                    }
                    else if (change.Id.Length == TimeBlockEventId.Length)
                    {
                        untracked.Add(change.Id);
                    }

                    continue; // one this device does not track: the outbound pass adopts it or removes it
                }

                var outcome = await ApplyAsync(plan, plan.Events[taskId], change, ct);
                moved += outcome.Moved;
                conflicts += outcome.Conflicts;
            }

            pageToken = page.NextPageToken;
            if (pageToken is null)
            {
                syncToken = page.NextSyncToken ?? syncToken;
                break;
            }
        }

        await store.ExecuteAsync(async (session, token) => { await session.TimeBlocks.SaveStateAsync(state with { SyncToken = syncToken, PageToken = pageToken }, token); return 0; }, ct);
        return (moved, conflicts, null, false, untracked);
    }

    /// <summary>One change from Google applied to its task, under the conflict rule of PLAN3 §11: the most recent of Google's move and the task's own change wins, the loser goes to history.</summary>
    private async Task<(int Moved, int Conflicts)> ApplyAsync(Plan plan, TimeBlockEventRecord row, GoogleEventChange change, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var current = await store.QueryAsync(async (session, token) =>
        {
            var task = await session.Tasks.GetIncludingDeletedAsync(row.Id, token);
            return (Task: task, PlannedChange: await session.TimeBlocks.PlannedGroupTimeAsync(row.Id, token));
        }, ct);
        var task = current.Task;

        if (change.Cancelled)
        {
            // The block was deleted in Google: the task keeps its date and loses its time, with history. A task that is
            // gone, or already without a time, only loses its row.
            if (task is null || task.DeletedAtUtc is not null || task.PlannedTime is null)
            {
                await store.ExecuteAsync(async (session, token) => { await session.TimeBlocks.DeleteEventAsync(row.Id, token); return 0; }, ct);
                return (0, 0);
            }

            // The scope wraps the whole transaction: the capture reads it when the write commits.
            using var clearedScope = ActivityScope.Open(ActivitySources.Sync, ActivityActions.TimeBlockApply, actorId: Actor);
            await store.ExecuteAsync(async (session, token) =>
            {
                var before = await Snapshot(session, task, token);
                var updated = task with { PlannedTime = null, PlannedZoneId = null, Version = task.Version + 1, UpdatedAtUtc = now };
                if (await session.Tasks.UpdateAsync(updated, task.Version, token) is null)
                {
                    return 0; // the task changed meanwhile; the feed shows this change again on the next pass
                }

                await session.History.AddAsync(History(task, now, ClearedEvent, ["PlannedTime", "PlannedZoneId"], before, before with { Task = updated }), token);
                await session.TimeBlocks.DeleteEventAsync(row.Id, token);
                return 0;
            }, ct);
            return (1, 0);
        }

        if ((row.ETag is not null && row.ETag == change.ETag) || change.StartUtc is not { } start || change.EndUtc is not { } end || end <= start
            || task is null || task.DeletedAtUtc is not null
            || (change.UpdatedUtc is { } changedAt && row.LastSyncedAtUtc is { } syncedAt && changedAt <= syncedAt))
        {
            return (0, 0); // the app's own write (its etag, or older than the last sync), no usable times, or a task that is gone (the outbound pass removes its event)
        }

        var zone = Zone(change.TimeZoneId) ?? Zone(task.PlannedZoneId) ?? plan.Zone;
        var local = TimeZoneInfo.ConvertTime(start, zone);
        var minutes = (int)Math.Clamp(Math.Round((end - start).TotalMinutes), 1, 1440);
        var proposed = task with { PlannedDate = DateOnly.FromDateTime(local.DateTime), PlannedTime = TimeOnly.FromDateTime(local.DateTime), PlannedZoneId = zone.Id, EstimateMinutes = minutes };
        var localChanged = row.PushedHash is not null && row.PushedHash != Describe(task, plan.ShowTitles, plan.Zone).Hash;
        if (localChanged && (change.UpdatedUtc ?? now) < (current.PlannedChange ?? task.UpdatedAtUtc))
        {
            // The task changed here after the last push, more recently than Google's move: the task wins and Google's move
            // is the loser. The next outbound pass pushes the task's time back; the etag is kept so this change is not read twice.
            using var keptScope = ActivityScope.Open(ActivitySources.Sync, ActivityActions.TimeBlockApply, actorId: Actor);
            await store.ExecuteAsync(async (session, token) =>
            {
                var kept = await Snapshot(session, task, token);
                await session.History.AddAsync(History(task, now, ConflictEvent, TaskMutationSupport.ChangedTaskFields(task, proposed), kept with { Task = proposed }, kept), token);
                await SaveRowAsync(session, row with { ETag = change.ETag, RemoteMovedAtUtc = change.UpdatedUtc ?? now, LastSyncedAtUtc = now }, token);
                return 0;
            }, ct);
            return (0, 1);
        }

        using var scope = ActivityScope.Open(ActivitySources.Sync, ActivityActions.TimeBlockApply, actorId: Actor);
        var applied = await store.ExecuteAsync(async (session, token) =>
        {
            var before = await Snapshot(session, task, token);
            var updated = proposed with { Version = task.Version + 1, UpdatedAtUtc = now };
            if (await session.Tasks.UpdateAsync(updated, task.Version, token) is null)
            {
                return false;
            }

            await session.History.AddAsync(History(task, now, localChanged ? ConflictEvent : MovedEvent, TaskMutationSupport.ChangedTaskFields(task, updated), before, before with { Task = updated }), token);
            await SaveRowAsync(session, row with
            {
                ETag = change.ETag, PushedHash = Describe(updated, plan.ShowTitles, plan.Zone).Hash, RemoteMovedAtUtc = change.UpdatedUtc ?? now, LastSyncedAtUtc = now,
            }, token);
            return true;
        }, ct);
        return applied ? (1, localChanged ? 1 : 0) : (0, 0);
    }

    private TaskHistoryRecord History(TaskRecord task, DateTimeOffset now, string kind, IReadOnlyList<string> fields, TaskAggregateSnapshot before, TaskAggregateSnapshot after) => new()
    {
        Id = ids.NewId().ToString(), TaskId = task.Id, OperationId = ids.NewId().ToString(), EffectiveAtUtc = now, RecordedAtUtc = now, Actor = Actor,
        EventKind = kind, ChangedFields = fields.Count == 0 ? ["PlannedDate", "PlannedTime", "EstimateMinutes"] : fields,
        SnapshotBefore = StoredJsonFormats.Snapshot(before), SnapshotAfter = StoredJsonFormats.Snapshot(after), AggregateVersion = after.Task.Version,
    };

    private static async Task SaveRowAsync(IDataSession session, TimeBlockEventRecord row, CancellationToken token)
    {
        var stored = await session.TimeBlocks.UpdateEventAsync(row with { Version = row.Version + 1, UpdatedAtUtc = row.LastSyncedAtUtc!.Value }, row.Version, token);
        if (stored is null)
        {
            throw new InvalidOperationException("The time block row changed during the pass.");
        }
    }

    private Task SaveStateAsync(Plan plan, Func<TimeBlockState, TimeBlockState> change, CancellationToken ct) =>
        store.ExecuteAsync(async (session, token) =>
        {
            var state = await session.TimeBlocks.GetStateAsync(token) ?? new TimeBlockState(plan.CalendarId, null, null, null, null, false);
            await session.TimeBlocks.SaveStateAsync(change(state), token);
            return 0;
        }, ct);

    /// <summary>The sign-in that asks for the write scope; the token goes to this device's secret store under the signed-in account.</summary>
    private async Task<string> SignInForAsync(GoogleAccountRecord account, CancellationToken ct)
    {
        GoogleSignIn signIn;
        try
        {
            signIn = await google.SignInAsync([GoogleCalendarScopes.AppCreated], ct);
        }
        catch (GoogleCalendarException error)
        {
            throw Translate(error);
        }

        var id = GoogleCalendarCommands.AccountId(signIn.Subject);
        await secrets.SetAsync(GoogleCalendarCommands.SecretKey(id), signIn.RefreshToken, ct);
        if (id != account.Id)
        {
            throw AppException.Validation("accountId", $"Sign in to Google as {account.Email}, the account you chose, and try again.");
        }

        return signIn.RefreshToken;
    }

    private async Task<IReadOnlyList<string>?> ValidatedSpacesAsync(IReadOnlyList<string>? spaceIds, IDataSession? session, CancellationToken ct)
    {
        if (spaceIds is null || spaceIds.Count == 0)
        {
            return null;
        }

        var spaces = session is null ? await store.QueryAsync((s, token) => s.Spaces.ListAsync(token), ct) : await session.Spaces.ListAsync(ct);
        var live = spaces.Where(space => space.DeletedAtUtc is null).Select(space => space.Id).ToHashSet(StringComparer.Ordinal);
        if (spaceIds.Count > 100 || spaceIds.Any(id => !live.Contains(id)))
        {
            throw AppException.Validation("spaceIds", "Choose spaces that still exist.");
        }

        return spaceIds.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string? SpacesJson(IReadOnlyList<string>? spaceIds) => spaceIds is null || spaceIds.Count == 0 ? null : JsonSerializer.Serialize(spaceIds);

    internal static IReadOnlyList<string> ParseSpaces(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(json) is { Length: > 0 } ids ? [.. ids.Where(id => !string.IsNullOrWhiteSpace(id))] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static TimeZoneInfo? Zone(string? id) => !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;

    private static async Task<TaskAggregateSnapshot> Snapshot(IDataSession session, TaskRecord task, CancellationToken token) =>
        new(task, await session.Subtasks.ListByTaskAsync(task.Id, token), await session.Tags.ListTaskTagIdsAsync(task.Id, token));

    private static AppException Conflict() => AppException.VersionConflict("That setting changed elsewhere. Refresh and try again.");

    private static AppException Translate(GoogleCalendarException error) => error.Kind switch
    {
        GoogleFailure.NotConfigured => new AppException(AppErrorKind.UnsupportedCapability, error.Message),
        _ => AppException.Validation("google", GoogleCalendarCommands.MessageOf(error)),
    };
}
