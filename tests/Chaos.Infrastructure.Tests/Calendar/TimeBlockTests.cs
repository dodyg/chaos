using Chaos.Application.Abstractions;
using Chaos.Application.Calendar;
using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Sync;

namespace Chaos.Infrastructure.Tests.Calendar;

/// <summary>Google time blocking (PLAN3 §11) against real SQLite and the fake Google transport of §13, extended for Q16.</summary>
public sealed class TimeBlockTests
{
    private const string Subject = "109876543210987654321";
    private const string Token = "1//refresh-token-TIME-BLOCK-9c1d";

    /// <summary>
    /// The app calendar as Google keeps it: client event ids, 409 on a duplicate insert, a new etag and <c>updated</c> per write,
    /// and a change feed with sync tokens (a token nobody knows answers 410 once). Every write is recorded for the
    /// "only the app calendar is ever written" rule.
    /// </summary>
    private sealed class FakeGoogleCalendar(TimeProvider clock) : IGoogleCalendarClient
    {
        public sealed record Stored(string Id, string? Summary, DateTimeOffset Start, DateTimeOffset End, string ETag, DateTimeOffset Updated, bool Cancelled);

        public bool IsConfigured => true;
        public List<string> SignInScopes { get; } = [];
        public Dictionary<string, string> Calendars { get; } = [];
        public Dictionary<string, List<Stored>> Log { get; } = [];
        public Dictionary<string, string> Tokens { get; } = [];
        public HashSet<string> ExpiredTokens { get; } = [];
        public List<(string Method, string Url, string? Body)> Writes { get; } = [];
        public GoogleCalendarException? Fail { get; set; }
        public int Requests { get; private set; }
        public int TokenExpirations { get; private set; }
        private long _etag;
        private long _token;

        private string NewEtag() => $"\"e{Interlocked.Increment(ref _etag)}\"";

        private void Check()
        {
            Requests++;
            if (Fail is { } error)
            {
                throw error;
            }
        }

        private void Require(string calendarId)
        {
            if (!Calendars.ContainsKey(calendarId))
            {
                throw new GoogleCalendarException(GoogleFailure.Other, "Google no longer shows that calendar to this account.");
            }
        }

        /// <summary>The current events of a calendar: the newest version of every id that is not cancelled.</summary>
        public Dictionary<string, Stored> Live(string calendarId) => Log[calendarId]
            .GroupBy(entry => entry.Id)
            .Select(group => group.Last())
            .Where(entry => !entry.Cancelled)
            .ToDictionary(entry => entry.Id, StringComparer.Ordinal);

        /// <summary>The person moves the block in Google's own UI.</summary>
        public void MoveInGoogle(string calendarId, string eventId, DateTimeOffset start, DateTimeOffset end, string? summary = null)
        {
            var current = Live(calendarId)[eventId];
            Log[calendarId].Add(current with { Start = start, End = end, Summary = summary ?? current.Summary, ETag = NewEtag(), Updated = clock.GetUtcNow() });
        }

        /// <summary>The person deletes the block in Google's own UI.</summary>
        public void DeleteInGoogle(string calendarId, string eventId)
        {
            var current = Live(calendarId)[eventId];
            Log[calendarId].Add(current with { Cancelled = true, ETag = NewEtag(), Updated = clock.GetUtcNow() });
        }

        public Task<GoogleSignIn> SignInAsync(CancellationToken ct) => SignInAsync([], ct);

        public Task<GoogleSignIn> SignInAsync(IReadOnlyList<string> extraScopes, CancellationToken ct)
        {
            SignInScopes.Add(string.Join(" ", extraScopes));
            return Task.FromResult(new GoogleSignIn(Subject, "me@example.com", Token));
        }

        public Task<IReadOnlyList<GoogleCalendarInfo>> ListCalendarsAsync(string refreshToken, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GoogleCalendarInfo>>([.. Calendars.Select(pair => new GoogleCalendarInfo(pair.Key, pair.Value, null, false))]);

        public Task<IReadOnlyList<ParsedMeeting>> ListEventsAsync(string refreshToken, string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
            throw new InvalidOperationException("the meeting cache is not part of time blocking");

        public Task<bool> RevokeAsync(string refreshToken, CancellationToken ct) => Task.FromResult(true);

        public Task<GoogleCalendarInfo> CreateCalendarAsync(string refreshToken, string name, CancellationToken ct)
        {
            Check();
            var id = $"calendar_{Calendars.Count + 1}@group.v.calendar.google.com";
            Calendars[id] = name;
            Log[id] = [];
            Writes.Add(("POST", "calendars", $"{{\"summary\":\"{name}\"}}"));
            return Task.FromResult(new GoogleCalendarInfo(id, name, null, false));
        }

        public Task DeleteCalendarAsync(string refreshToken, string calendarId, CancellationToken ct)
        {
            Check();
            Writes.Add(("DELETE", $"calendars/{calendarId}", null));
            Calendars.Remove(calendarId);
            Log.Remove(calendarId);
            return Task.CompletedTask;
        }

        public Task<GoogleEventWrite> InsertEventAsync(string refreshToken, string calendarId, GoogleEventBody body, CancellationToken ct)
        {
            Check();
            Require(calendarId);
            Writes.Add(("POST", $"calendars/{calendarId}/events", body.Id));
            if (body.Id is not null && Live(calendarId).TryGetValue(body.Id, out var existing))
            {
                return Task.FromResult(new GoogleEventWrite(true, false, existing.ETag, existing.Updated));
            }

            var stored = new Stored(body.Id!, body.Summary, body.StartUtc, body.EndUtc, NewEtag(), clock.GetUtcNow(), false);
            Log[calendarId].Add(stored);
            return Task.FromResult(new GoogleEventWrite(false, false, stored.ETag, stored.Updated));
        }

        public Task<GoogleEventWrite> PatchEventAsync(string refreshToken, string calendarId, string eventId, GoogleEventBody body, CancellationToken ct)
        {
            Check();
            Require(calendarId);
            Writes.Add(("PATCH", $"calendars/{calendarId}/events/{eventId}", null));
            if (!Live(calendarId).TryGetValue(eventId, out var current))
            {
                return Task.FromResult(new GoogleEventWrite(false, true, null, null));
            }

            var stored = current with { Summary = body.Summary, Start = body.StartUtc, End = body.EndUtc, ETag = NewEtag(), Updated = clock.GetUtcNow() };
            Log[calendarId].Add(stored);
            return Task.FromResult(new GoogleEventWrite(false, false, stored.ETag, stored.Updated));
        }

        public Task DeleteEventAsync(string refreshToken, string calendarId, string eventId, CancellationToken ct)
        {
            Check();
            Require(calendarId);
            Writes.Add(("DELETE", $"calendars/{calendarId}/events/{eventId}", null));
            if (Live(calendarId).TryGetValue(eventId, out var current))
            {
                Log[calendarId].Add(current with { Cancelled = true, ETag = NewEtag(), Updated = clock.GetUtcNow() });
            }

            return Task.CompletedTask;
        }

        public Task<GoogleSyncPage> ListEventChangesAsync(string refreshToken, string calendarId, string? syncToken, string? pageToken, CancellationToken ct)
        {
            Check();
            Require(calendarId);
            Writes.Add(("GET", $"calendars/{calendarId}/events?syncToken={syncToken}", null));
            if (syncToken is not null && ExpiredTokens.Remove(syncToken))
            {
                TokenExpirations++;
                return Task.FromResult(new GoogleSyncPage([], null, null, true));
            }

            List<Stored> entries;
            var position = Log[calendarId].Count;
            if (syncToken is not null)
            {
                var known = Tokens[syncToken];
                position = int.Parse(known[(known.IndexOf('|') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                entries = [.. Log[calendarId].Skip(position)];
            }
            else
            {
                entries = [.. Live(calendarId).Values];
            }

            var token = $"sync-{Interlocked.Increment(ref _token)}";
            Tokens[token] = calendarId + "|" + position;
            return Task.FromResult(new GoogleSyncPage([.. entries.Select(Change)], null, token, false));
        }

        private static GoogleEventChange Change(Stored stored) => new(stored.Id, stored.ETag, stored.Updated, stored.Cancelled, stored.Summary,
            stored.Cancelled ? null : stored.Start, stored.Cancelled ? null : stored.End, "UTC");
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
        public required FakeGoogleCalendar Google { get; init; }
        public required MemorySecrets Secrets { get; init; }
        public required GoogleCalendarCommands GoogleCommands { get; init; }
        public required TimeBlockCommands Blocks { get; init; }
        public required CaptureCommands Capture { get; init; }
        public required TaskCommands Tasks { get; init; }

        public static Rig Start(DataHost host)
        {
            var google = new FakeGoogleCalendar(host.Clock);
            var secrets = new MemorySecrets();
            return new Rig
            {
                Host = host, Google = google, Secrets = secrets,
                GoogleCommands = new GoogleCalendarCommands(host.Store, secrets, google, host.Clock),
                Blocks = new TimeBlockCommands(host.Store, secrets, google, host.Ids, host.Clock),
                Capture = new CaptureCommands(host.Store, host.Ids, host.Clock),
                Tasks = new TaskCommands(host.Store, host.Ids, host.Clock),
            };
        }

        public async Task<(string AccountId, string CalendarId)> EnableAsync(bool showTitles = true, IReadOnlyList<string>? spaces = null)
        {
            var account = await GoogleCommands.ConnectAsync();
            var status = await Blocks.EnableAsync(new EnableTimeBlockCommand(account.Account.Id, showTitles, spaces));
            return (account.Account.Id, status.CalendarId!);
        }

        public Task<CapturedTasksResult> CaptureAsync(string title, DateOnly day, TimeOnly time, int? minutes, Func<CaptureTaskDraft, CaptureTaskDraft>? customize = null)
        {
            var draft = new CaptureTaskDraft { Title = title, PlannedDate = day, PlannedTime = time, ZoneId = "UTC", EstimateMinutes = minutes };
            draft = customize?.Invoke(draft) ?? draft;
            return Capture.CreateAsync(new CreateCapturedTasksCommand(DataHost.Op(), [draft]));
        }

        public Task<TimeBlockPassResult> SyncAsync() => Blocks.SyncAsync();

        public async Task<TaskRecord> TaskAsync(string taskId) =>
            (await Host.Store.QueryAsync((session, token) => session.Tasks.GetAsync(taskId, token)))!;

        public async Task<IReadOnlyList<TaskHistoryRecord>> HistoryAsync(string taskId) =>
            await Host.Store.QueryAsync((session, token) => session.History.ListByTaskAsync(taskId, token));

        /// <summary>A shared space with this person as the only member; the task moves into it.</summary>
        public async Task<string> ShareAsync(TaskRecord task, bool assignToMe)
        {
            var spaceId = Guid.NewGuid().ToString("D");
            await Host.Store.ExecuteAsync(async (session, token) =>
            {
                var profile = (await session.Profiles.GetPrimaryAsync(token))!;
                var now = Host.Clock.GetUtcNow();
                await session.Spaces.AddAsync(new SpaceRecord { Id = spaceId, OwnerProfileId = profile.Id, Kind = SpaceKind.Shared, Name = "Team", Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now }, token);
                // A task's space is immutable through the store (PLAN3 5.5: a move is a server operation); a shared task here is seeded directly.
                using var command = session.Connection().CreateCommand();
                command.CommandText = "UPDATE Tasks SET SpaceId = $space WHERE Id = $task;";
                command.Parameters.AddWithValue("$space", spaceId);
                command.Parameters.AddWithValue("$task", task.Id);
                await command.ExecuteNonQueryAsync(token);
                if (assignToMe)
                {
                    await session.Tasks.SaveAssigneeAsync(new TaskAssigneeRecord(task.Id, profile.Id, now, null, null), token);
                }

                return 0;
            });
            return spaceId;
        }

        public void Dispose() => Host.Dispose();
    }

    private static Rig RigOf(DataHost host) => Rig.Start(host);

    private static readonly DateOnly Day = new(2026, 11, 20);

    [Test]
    public async Task TheEventId_IsDeterministicBase32Hex_AndDifferentPerTask()
    {
        var guid = "7f3c2a1e-9b4d-4e6f-8a2b-1c3d5e7f9a0b";
        var id = TimeBlockEventId.Of(guid);
        await Assert.That(id.StartsWith("cb", StringComparison.Ordinal)).IsTrue();
        await Assert.That(id.Length).IsEqualTo(28);
        await Assert.That(id[2..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'v')).IsTrue();
        await Assert.That(TimeBlockEventId.Of(guid)).IsEqualTo(id);
        await Assert.That(TimeBlockEventId.Of("githubtask:1:2:3")).IsNotEqualTo(id);
        await Assert.That(TimeBlockEventId.Of("githubtask:1:2:3").Length).IsEqualTo(28);
    }

    [Test]
    public async Task NothingOn_MakesNoRequestsAtAll()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        await rig.CaptureAsync("Planned", Day, new TimeOnly(9, 0), 60);

        var status = await rig.Blocks.StatusAsync();
        await Assert.That(status.Enabled).IsFalse();
        await Assert.That(status.IsRunner).IsFalse();
        await Assert.That(status.Spaces.Select(space => space.Name)).IsEquivalentTo(["Personal"]);
        await Assert.That(await rig.Blocks.IsDueAsync()).IsFalse();
        await Assert.That((await rig.SyncAsync()).Ran).IsFalse();
        await Assert.That(rig.Google.Requests).IsEqualTo(0);
        await Assert.That(rig.Google.Calendars).IsEmpty();
    }

    [Test]
    public async Task Enable_CreatesTheCalendar_PushesEligibleTasksOnly_AndWritesOnlyThatCalendar()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        var eligible = (await rig.CaptureAsync("Eligible", Day, new TimeOnly(9, 0), 60)).Tasks[0];
        var noEstimate = (await rig.CaptureAsync("No estimate", Day, new TimeOnly(10, 0), null)).Tasks[0];
        var noTime = (await rig.CaptureAsync("Date only", Day, new TimeOnly(11, 0), 30, draft => draft with { PlannedTime = null })).Tasks[0];
        var done = (await rig.CaptureAsync("Done", Day, new TimeOnly(12, 0), 30)).Tasks[0];
        var cancelled = (await rig.CaptureAsync("Cancelled", Day, new TimeOnly(13, 0), 30)).Tasks[0];
        await rig.Host.Store.ExecuteAsync(async (session, token) =>
        {
            var now = host.Clock.GetUtcNow();
            await session.Tasks.UpdateAsync(done with { Status = TaskItemStatus.Done, CompletedAtUtc = now, Version = done.Version + 1, UpdatedAtUtc = now }, done.Version, token);
            await session.Tasks.UpdateAsync(cancelled with { Status = TaskItemStatus.Cancelled, CancelledAtUtc = now, Version = cancelled.Version + 1, UpdatedAtUtc = now }, cancelled.Version, token);
            return 0;
        });
        var mine = (await rig.CaptureAsync("Shared, assigned to me", Day, new TimeOnly(14, 0), 45)).Tasks[0];
        var theirs = (await rig.CaptureAsync("Shared, not mine", Day, new TimeOnly(15, 0), 45)).Tasks[0];
        await rig.ShareAsync(mine, assignToMe: true);
        await rig.ShareAsync(theirs, assignToMe: false);

        var (accountId, calendarId) = await rig.EnableAsync();
        var status = await rig.Blocks.StatusAsync();

        await Assert.That(status.Enabled).IsTrue();
        await Assert.That(status.AccountId).IsEqualTo(accountId);
        await Assert.That(status.AccountEmail).IsEqualTo("me@example.com");
        await Assert.That(status.IsRunner).IsTrue();
        await Assert.That(status.CalendarId).IsEqualTo(calendarId);
        await Assert.That(status.LastPassUtc).IsEqualTo(host.Clock.GetUtcNow());
        await Assert.That(status.LastError).IsNull();
        await Assert.That(status.EventCount).IsEqualTo(2);
        await Assert.That(rig.Google.Calendars.Values).IsEquivalentTo(["Chaos"]);

        var live = rig.Google.Live(calendarId);
        await Assert.That(live.Keys.Order()).IsEquivalentTo(new[] { TimeBlockEventId.Of(eligible.Id), TimeBlockEventId.Of(mine.Id) }.Order());
        var block = live[TimeBlockEventId.Of(eligible.Id)];
        await Assert.That(block.Summary).IsEqualTo("Eligible");
        await Assert.That(block.Start).IsEqualTo(new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero));
        await Assert.That(block.End).IsEqualTo(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));
        await Assert.That(new[] { noEstimate.Id, noTime.Id, done.Id, cancelled.Id, theirs.Id }.Any(id => live.ContainsKey(TimeBlockEventId.Of(id)))).IsFalse();
        await Assert.That(rig.Google.Requests).IsGreaterThan(0);

        // Every event write names the stored calendar; nothing else is ever touched.
        await Assert.That(rig.Google.Writes.Where(write => write.Url.Contains("/events", StringComparison.Ordinal)).All(write => write.Url.Contains(calendarId, StringComparison.Ordinal))).IsTrue();
        await Assert.That(rig.Google.Writes.Any(write => write.Url.Contains("primary", StringComparison.Ordinal))).IsFalse();

        // The rows carry what was pushed; a second pass inserts nothing more.
        var rows = await host.Store.QueryAsync((session, token) => session.TimeBlocks.ListEventsAsync(token));
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows.All(row => row.ETag is not null && row.PushedHash is not null && row.LastSyncedAtUtc == host.Clock.GetUtcNow())).IsTrue();
        var inserts = rig.Google.Writes.Count(write => write.Method == "POST" && write.Url.EndsWith("/events", StringComparison.Ordinal));
        await rig.SyncAsync();
        await Assert.That(rig.Google.Writes.Count(write => write.Method == "POST" && write.Url.EndsWith("/events", StringComparison.Ordinal))).IsEqualTo(inserts);
        await Assert.That(rig.Google.Requests).IsGreaterThan(inserts + 1); // each pass reads the change feed too

        // The settings travel in the profile's timeblock group; the token stays in the secret store.
        var profile = await host.Store.QueryAsync((session, token) => session.Profiles.GetPrimaryAsync(token));
        await Assert.That(profile!.TimeBlockEnabled).IsTrue();
        await Assert.That(profile.TimeBlockAccountId).IsEqualTo(accountId);
        await Assert.That(profile.TimeBlockCalendarId).IsEqualTo(calendarId);
        await Assert.That(profile.TimeBlockSpacesJson).IsNull();
        await Assert.That(rig.Secrets.Values[GoogleCalendarCommands.SecretKey(accountId)]).IsEqualTo(Token);
        await Assert.That(await rig.Blocks.IsDueAsync()).IsFalse(); // nothing changed since the pass
    }

    [Test]
    public async Task TurningOffAndOnAgain_ReusesTheKeptCalendar_AndDeletingRemovesIt()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        await rig.CaptureAsync("Eligible", Day, new TimeOnly(9, 0), 60);
        var (accountId, calendarId) = await rig.EnableAsync();

        var version = (await rig.Blocks.StatusAsync()).ProfileVersion;
        var kept = await rig.Blocks.DisableAsync(version, deleteCalendar: false);
        await Assert.That(kept.Status.Enabled).IsFalse();
        await Assert.That(kept.CalendarDeleted).IsFalse();
        await Assert.That(kept.Status.EventCount).IsEqualTo(0);
        await Assert.That(kept.Status.AccountId).IsEqualTo(accountId); // the kept calendar is remembered with its account
        await Assert.That(kept.Status.CalendarId).IsEqualTo(calendarId);
        await Assert.That(rig.Google.Calendars).ContainsKey(calendarId);
        await Assert.That((await rig.SyncAsync()).Ran).IsFalse(); // off: nothing runs

        var again = await rig.Blocks.EnableAsync(new EnableTimeBlockCommand(accountId, true, null));
        await Assert.That(again.CalendarId).IsEqualTo(calendarId);
        await Assert.That(rig.Google.Calendars.Count).IsEqualTo(1);
        await Assert.That(rig.Google.Live(calendarId).Count).IsEqualTo(1); // the kept event is recognised: the refused insert is settled by a patch, nothing is duplicated
        await Assert.That(rig.Google.Writes.Count(write => write.Method == "POST" && write.Url.EndsWith("/events", StringComparison.Ordinal))).IsEqualTo(2); // the first enable, and the refused second insert
        await Assert.That(rig.Google.Writes.Last(write => write.Url.Contains("/events/", StringComparison.Ordinal) && write.Method != "GET").Method).IsEqualTo("PATCH");

        var deleted = await rig.Blocks.DisableAsync(again.ProfileVersion, deleteCalendar: true);
        await Assert.That(deleted.CalendarDeleted).IsTrue();
        await Assert.That(rig.Google.Calendars).IsEmpty();
        await Assert.That(rig.Google.Writes[^1].Method).IsEqualTo("DELETE");
        await Assert.That(rig.Google.Writes[^1].Url).IsEqualTo($"calendars/{calendarId}");
    }

    [Test]
    public async Task TwoDevices_OneEventPerTask_BecauseADuplicateInsertIsPatched()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        var task = (await rig.CaptureAsync("Eligible", Day, new TimeOnly(9, 0), 60)).Tasks[0];
        var (accountId, calendarId) = await rig.EnableAsync();
        await rig.Blocks.DisableAsync((await rig.Blocks.StatusAsync()).ProfileVersion, deleteCalendar: false);

        // The other device (same account, same calendar) made the event; this device knows nothing about the row.
        await rig.Blocks.EnableAsync(new EnableTimeBlockCommand(accountId, true, null));
        await host.Store.ExecuteAsync(async (session, token) => { await session.TimeBlocks.DeleteEventAsync(task.Id, token); return 0; });
        await rig.SyncAsync();

        await Assert.That(rig.Google.Live(calendarId).Count).IsEqualTo(1);
        var writes = rig.Google.Writes.Where(write => write.Url.EndsWith($"/events/{TimeBlockEventId.Of(task.Id)}", StringComparison.Ordinal) || write.Url.EndsWith("/events", StringComparison.Ordinal)).ToList();
        await Assert.That(writes[^2].Method).IsEqualTo("POST"); // the insert Google refused with 409
        await Assert.That(writes[^1].Method).IsEqualTo("PATCH"); // so the patch settled it
        await Assert.That(rig.Google.Writes.Count(write => write.Url.Contains("/events", StringComparison.Ordinal) && !write.Url.Contains("syncToken", StringComparison.Ordinal))).IsEqualTo(writes.Count);
    }

    [Test]
    public async Task BusyMode_SendsNoTitle()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        await rig.CaptureAsync("Secret project", Day, new TimeOnly(9, 0), 60);
        var (_, calendarId) = await rig.EnableAsync(showTitles: false);

        var live = rig.Google.Live(calendarId);
        await Assert.That(live.Count).IsEqualTo(1);
        await Assert.That(live.Values.Single().Summary).IsNull();
        await Assert.That(rig.Google.Writes.Single(write => write.Method == "POST" && write.Url.EndsWith("/events", StringComparison.Ordinal)).Body).DoesNotContain("Secret");

        // Switching to titles patches the existing event; switching back to Busy takes the title off again.
        await rig.Blocks.UpdateAsync(new UpdateTimeBlockCommand((await rig.Blocks.StatusAsync()).ProfileVersion, true, null));
        await rig.SyncAsync();
        await Assert.That(rig.Google.Live(calendarId).Values.Single().Summary).IsEqualTo("Secret project");
        await rig.Blocks.UpdateAsync(new UpdateTimeBlockCommand((await rig.Blocks.StatusAsync()).ProfileVersion, false, null));
        await rig.SyncAsync();
        await Assert.That(rig.Google.Live(calendarId).Values.Single().Summary).IsNull();
        await Assert.That(rig.Google.Writes.Count(write => write.Method == "POST" && write.Url.EndsWith("/events", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task OnlyChosenSpaces_AreBlocked()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        await rig.CaptureAsync("Personal", Day, new TimeOnly(9, 0), 60);
        var shared = (await rig.CaptureAsync("Shared", Day, new TimeOnly(10, 0), 60)).Tasks[0];
        var spaceId = await rig.ShareAsync(shared, assignToMe: true);

        var (_, calendarId) = await rig.EnableAsync(spaces: [spaceId]);
        await Assert.That(rig.Google.Live(calendarId).Count).IsEqualTo(1);
        await Assert.That(rig.Google.Live(calendarId).Values.Single().Summary).IsEqualTo("Shared");
        var status = await rig.Blocks.StatusAsync();
        await Assert.That(status.Spaces.Single(space => space.Id == spaceId).Chosen).IsTrue();
        await Assert.That(status.Spaces.Single(space => space.Personal).Chosen).IsFalse();

        // Choosing a space that does not exist is refused; an empty list means every space.
        await Assert.That(async () => await rig.Blocks.UpdateAsync(new UpdateTimeBlockCommand(status.ProfileVersion, true, ["no-such-space"]))).Throws<AppException>();
        status = await rig.Blocks.UpdateAsync(new UpdateTimeBlockCommand(status.ProfileVersion, true, []));
        await Assert.That(status.Spaces.All(space => space.Chosen)).IsTrue();
        await rig.SyncAsync();
        await Assert.That(rig.Google.Live(calendarId).Count).IsEqualTo(2);
    }

    [Test]
    public async Task ATaskEditedHere_IsPushedOnTheNextPass_AndADeletedTaskLosesItsBlock()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        var task = (await rig.CaptureAsync("Eligible", Day, new TimeOnly(9, 0), 60)).Tasks[0];
        var (_, calendarId) = await rig.EnableAsync();
        var eventId = TimeBlockEventId.Of(task.Id);
        await Assert.That(await rig.Blocks.IsDueAsync()).IsFalse();

        host.Clock.Now = host.Clock.Now.AddMinutes(1);
        task = (await rig.Tasks.UpdateDetailsAsync(new UpdateTaskDetailsCommand(DataHost.Op(), task.Id, task.Version, task.Title, task.Notes, Day, new TimeOnly(11, 30), "UTC",
            null, null, null, task.Priority, 90, task.CapacityCategory))).Task;
        await Assert.That(await rig.Blocks.IsDueAsync()).IsTrue(); // the edit reached the activity log

        var pass = await rig.SyncAsync();
        await Assert.That(pass.Updated).IsEqualTo(1);
        var block = rig.Google.Live(calendarId)[eventId];
        await Assert.That(block.Start).IsEqualTo(new DateTimeOffset(2026, 11, 20, 11, 30, 0, TimeSpan.Zero));
        await Assert.That(block.End).IsEqualTo(new DateTimeOffset(2026, 11, 20, 13, 0, 0, TimeSpan.Zero));
        await Assert.That(await rig.Blocks.IsDueAsync()).IsFalse(); // the pass's own writes do not count as a change

        host.Clock.Now = host.Clock.Now.AddMinutes(1);
        await rig.Tasks.SetStatusAsync(new SetTaskStatusCommand(DataHost.Op(), task.Id, (await rig.TaskAsync(task.Id)).Version, TaskItemStatus.Done, null));
        pass = await rig.SyncAsync();
        await Assert.That(pass.Removed).IsEqualTo(1);
        await Assert.That(rig.Google.Live(calendarId)).IsEmpty();
        await Assert.That(await host.Store.QueryAsync((session, token) => session.TimeBlocks.ListEventsAsync(token))).IsEmpty();
    }

    [Test]
    public async Task MovingAnEventInGoogle_MovesTheTask_WithHistory_AsASyncEditByGoogle()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        var task = (await rig.CaptureAsync("Eligible", Day, new TimeOnly(9, 0), 60)).Tasks[0];
        var (_, calendarId) = await rig.EnableAsync();
        var eventId = TimeBlockEventId.Of(task.Id);

        host.Clock.Now = host.Clock.Now.AddMinutes(2);
        rig.Google.MoveInGoogle(calendarId, eventId, new DateTimeOffset(2026, 11, 23, 14, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 23, 15, 30, 0, TimeSpan.Zero));
        var pass = await rig.SyncAsync();

        await Assert.That(pass.Moved).IsEqualTo(1);
        await Assert.That(pass.Pushed + pass.Updated).IsEqualTo(0); // nothing to push back: the task now matches the event
        task = await rig.TaskAsync(task.Id);
        await Assert.That(task.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 23));
        await Assert.That(task.PlannedTime).IsEqualTo(new TimeOnly(14, 0));
        await Assert.That(task.EstimateMinutes).IsEqualTo(90);
        await Assert.That(task.PlannedZoneId).IsEqualTo("UTC");
        var history = await rig.HistoryAsync(task.Id);
        var entry = history[^1];
        await Assert.That(entry.Actor).IsEqualTo("google");
        await Assert.That(entry.EventKind).IsEqualTo("google.moved");
        await Assert.That(entry.ChangedFields).IsEquivalentTo(["PlannedDate", "PlannedTime", "EstimateMinutes"]);
        var activity = await host.Store.QueryAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "SELECT Source || '/' || Action FROM ActivityLog WHERE Action LIKE 'timeblock.%' ORDER BY Seq DESC LIMIT 1;";
            return (string)command.ExecuteScalar()!;
        });
        await Assert.That(activity).IsEqualTo("sync/timeblock.apply");

        var row = (await host.Store.QueryAsync((session, token) => session.TimeBlocks.ListEventsAsync(token))).Single();
        await Assert.That(row.ETag).IsEqualTo(rig.Google.Live(calendarId)[eventId].ETag);
        await Assert.That(row.PushedHash).IsEqualTo(TimeBlockCommands.Describe(task, true, TimeZoneInfo.Utc).Hash);
        await Assert.That(row.RemoteMovedAtUtc).IsEqualTo(host.Clock.GetUtcNow());

        // Reading the same change again (a new pass, the same feed position) applies nothing.
        await rig.SyncAsync();
        await Assert.That((await rig.HistoryAsync(task.Id)).Count).IsEqualTo(history.Count);
    }

    [Test]
    public async Task DeletingTheEventInGoogle_ClearsThePlannedTime_KeepsTheDate_AndRecordsHistory()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        var task = (await rig.CaptureAsync("Eligible", Day, new TimeOnly(9, 0), 60)).Tasks[0];
        var (_, calendarId) = await rig.EnableAsync();
        var eventId = TimeBlockEventId.Of(task.Id);

        host.Clock.Now = host.Clock.Now.AddMinutes(2);
        rig.Google.DeleteInGoogle(calendarId, eventId);
        var pass = await rig.SyncAsync();

        await Assert.That(pass.Moved).IsEqualTo(1);
        task = await rig.TaskAsync(task.Id);
        await Assert.That(task.PlannedDate).IsEqualTo(Day);
        await Assert.That(task.PlannedTime).IsNull();
        await Assert.That(task.PlannedZoneId).IsNull();
        await Assert.That(task.EstimateMinutes).IsEqualTo(60);
        var entry = (await rig.HistoryAsync(task.Id))[^1];
        await Assert.That(entry.Actor).IsEqualTo("google");
        await Assert.That(entry.EventKind).IsEqualTo("google.cleared");
        await Assert.That(entry.ChangedFields).IsEquivalentTo(["PlannedTime", "PlannedZoneId"]);
        await Assert.That(await host.Store.QueryAsync((session, token) => session.TimeBlocks.ListEventsAsync(token))).IsEmpty();
        await Assert.That(rig.Google.Live(calendarId)).IsEmpty(); // and the pass did not put the block back
    }

    [Test]
    public async Task AConflict_ResolvesToTheNewest_WithTheLoserInHistory()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        var task = (await rig.CaptureAsync("Eligible", Day, new TimeOnly(9, 0), 60)).Tasks[0];
        var (_, calendarId) = await rig.EnableAsync();
        var eventId = TimeBlockEventId.Of(task.Id);

        // Google's move is newer than the edit made here: Google wins, and the local time is the loser in history.
        host.Clock.Now = host.Clock.Now.AddMinutes(1);
        task = (await rig.Tasks.UpdateDetailsAsync(new UpdateTaskDetailsCommand(DataHost.Op(), task.Id, task.Version, task.Title, task.Notes, Day, new TimeOnly(16, 0), "UTC",
            null, null, null, task.Priority, 60, task.CapacityCategory))).Task;
        var localTime = host.Clock.GetUtcNow();
        host.Clock.Now = localTime.AddMinutes(5);
        rig.Google.MoveInGoogle(calendarId, eventId, new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 21, 8, 45, 0, TimeSpan.Zero));
        var pass = await rig.SyncAsync();
        task = await rig.TaskAsync(task.Id);
        await Assert.That(pass.Conflicts).IsEqualTo(1);
        await Assert.That(task.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 21));
        await Assert.That(task.PlannedTime).IsEqualTo(new TimeOnly(8, 0));
        await Assert.That(task.EstimateMinutes).IsEqualTo(45);
        var conflict = (await rig.HistoryAsync(task.Id))[^1];
        await Assert.That(conflict.EventKind).IsEqualTo("google.conflict");
        var loser = StoredJsonFormats.ReadSnapshot<TaskAggregateSnapshot>(conflict.SnapshotBefore!)!;
        await Assert.That(loser.Task.PlannedTime).IsEqualTo(new TimeOnly(16, 0));
        await Assert.That(rig.Google.Live(calendarId)[eventId].Start).IsEqualTo(new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero));

        // An edit made here after Google's move is the newer one: the task keeps its time, Google's move is the loser in
        // history, and the pass pushes the task's time back to Google.
        host.Clock.Now = host.Clock.Now.AddMinutes(1);
        rig.Google.MoveInGoogle(calendarId, eventId, new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 21, 12, 45, 0, TimeSpan.Zero));
        host.Clock.Now = host.Clock.Now.AddMinutes(10);
        task = (await rig.Tasks.UpdateDetailsAsync(new UpdateTaskDetailsCommand(DataHost.Op(), task.Id, task.Version, task.Title, task.Notes, task.PlannedDate, new TimeOnly(15, 0), "UTC",
            null, null, null, task.Priority, 45, task.CapacityCategory))).Task;
        pass = await rig.SyncAsync();
        task = await rig.TaskAsync(task.Id);
        await Assert.That(pass.Conflicts).IsEqualTo(1);
        await Assert.That(task.PlannedTime).IsEqualTo(new TimeOnly(15, 0));
        conflict = (await rig.HistoryAsync(task.Id))[^1];
        await Assert.That(conflict.EventKind).IsEqualTo("google.conflict");
        loser = StoredJsonFormats.ReadSnapshot<TaskAggregateSnapshot>(conflict.SnapshotBefore!)!;
        await Assert.That(loser.Task.PlannedTime).IsEqualTo(new TimeOnly(12, 0));
        await Assert.That(rig.Google.Live(calendarId)[eventId].Start).IsEqualTo(new DateTimeOffset(2026, 11, 21, 15, 0, 0, TimeSpan.Zero));
        var row = (await host.Store.QueryAsync((session, token) => session.TimeBlocks.ListEventsAsync(token))).Single();
        await Assert.That(row.PushedHash).IsEqualTo(TimeBlockCommands.Describe(task, true, TimeZoneInfo.Utc).Hash);
        await Assert.That(row.ETag).IsEqualTo(rig.Google.Live(calendarId)[eventId].ETag);
    }

    [Test]
    public async Task AnExpiredSyncToken_IsReplacedByAFullRead_ThatAppliesNothingOfTheAppOwn()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        var task = (await rig.CaptureAsync("Eligible", Day, new TimeOnly(9, 0), 60)).Tasks[0];
        var (_, calendarId) = await rig.EnableAsync();
        var state = await host.Store.QueryAsync((session, token) => session.TimeBlocks.GetStateAsync(token));
        await Assert.That(state!.SyncToken).IsNotNull();
        rig.Google.ExpiredTokens.Add(state.SyncToken!);

        host.Clock.Now = host.Clock.Now.AddMinutes(1);
        var pass = await rig.SyncAsync();
        var after = await host.Store.QueryAsync((session, token) => session.TimeBlocks.GetStateAsync(token));

        await Assert.That(rig.Google.TokenExpirations).IsEqualTo(1);
        await Assert.That(pass.Moved).IsEqualTo(0);
        await Assert.That(pass.Error).IsNull();
        await Assert.That(after!.SyncToken).IsNotEqualTo(state.SyncToken);
        await Assert.That((await rig.HistoryAsync(task.Id)).Any(entry => entry.Actor == "google")).IsFalse();
        await Assert.That((await rig.TaskAsync(task.Id)).Version).IsEqualTo(task.Version);
    }

    [Test]
    public async Task ANonRunnerDevice_MakesNoRequests_AndSigningInThereMovesNothing()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        await rig.CaptureAsync("Eligible", Day, new TimeOnly(9, 0), 60);
        await rig.EnableAsync();
        var requests = rig.Google.Requests;
        var status = await rig.Blocks.StatusAsync();

        // Another device became the runner (the setting syncs here): this device stops entirely.
        await host.Store.ExecuteAsync(async (session, token) =>
        {
            var profile = (await session.Profiles.GetPrimaryAsync(token))!;
            await session.Profiles.UpdateTimeBlockSettingsAsync(profile with { TimeBlockRunnerDeviceId = "another-device", Version = profile.Version + 1 }, profile.Version, token);
            return 0;
        });
        var other = await rig.Blocks.StatusAsync();
        await Assert.That(other.Enabled).IsTrue();
        await Assert.That(other.IsRunner).IsFalse();
        await Assert.That(await rig.Blocks.IsDueAsync()).IsFalse();
        await Assert.That((await rig.SyncAsync()).Ran).IsFalse();
        await Assert.That(rig.Google.Requests).IsEqualTo(requests);

        // "Run on this device" works because the token is here; then this device runs again and reads the whole calendar.
        var here = await rig.Blocks.RunOnThisDeviceAsync(other.ProfileVersion);
        await Assert.That(here.IsRunner).IsTrue();
        await rig.SyncAsync();
        await Assert.That(rig.Google.Requests).IsGreaterThan(requests);
        await Assert.That(status.ProfileVersion).IsLessThan(here.ProfileVersion);
    }

    [Test]
    public async Task AMissingTokenOnThisDevice_SignsInForTheWriteScope_AndStoresIt()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        var account = await rig.GoogleCommands.ConnectAsync();
        rig.Secrets.Values.Clear(); // as on a second device: the account came with sync, the token did not

        var passBefore = await rig.Blocks.SyncAsync();
        await Assert.That((await rig.Blocks.StatusAsync()).Enabled).IsFalse();

        var status = await rig.Blocks.EnableAsync(new EnableTimeBlockCommand(account.Account.Id, true, null));
        await Assert.That(rig.Google.SignInScopes[^1]).IsEqualTo("https://www.googleapis.com/auth/calendar.app.created"); // the write scope, nothing wider
        await Assert.That(rig.Secrets.Values[GoogleCalendarCommands.SecretKey(account.Account.Id)]).IsEqualTo(Token);
        await Assert.That(status.Enabled).IsTrue();
        await Assert.That(passBefore.Ran).IsFalse();

        // A runner without a token (it was removed since) says so and makes no request.
        rig.Secrets.Values.Clear();
        var requests = rig.Google.Requests;
        var pass = await rig.SyncAsync();
        await Assert.That(pass.Ran).IsTrue();
        await Assert.That(pass.NeedsSignIn).IsTrue();
        await Assert.That(pass.Error).IsEqualTo(GoogleCalendarCommands.SignInHereMessage);
        await Assert.That(rig.Google.Requests).IsEqualTo(requests);
        await Assert.That((await rig.Blocks.StatusAsync()).NeedsSignIn).IsTrue();
    }

    [Test]
    public async Task AFailedPass_KeepsTheRows_RecordsTheError_AndTheNextPassRecovers()
    {
        using var host = await DataHost.StartAsync();
        var rig = RigOf(host);
        await rig.CaptureAsync("Eligible", Day, new TimeOnly(9, 0), 60);
        var (_, calendarId) = await rig.EnableAsync();
        var state = await host.Store.QueryAsync((session, token) => session.TimeBlocks.GetStateAsync(token));

        rig.Google.Fail = new GoogleCalendarException(GoogleFailure.Offline, "Chaos could not reach Google. It will try again when you are online.");
        var pass = await rig.SyncAsync();
        await Assert.That(pass.Error).Contains("reach Google");
        var failed = await rig.Blocks.StatusAsync();
        await Assert.That(failed.LastError).Contains("reach Google");
        await Assert.That(failed.NeedsSignIn).IsFalse();
        await Assert.That(failed.EventCount).IsEqualTo(1);

        rig.Google.Fail = new GoogleCalendarException(GoogleFailure.SignInAgain, "Google Calendar: sign in again.");
        pass = await rig.SyncAsync();
        await Assert.That(pass.NeedsSignIn).IsTrue();
        await Assert.That((await rig.Blocks.StatusAsync()).LastError).IsEqualTo("Google Calendar: sign in again.");

        rig.Google.Fail = null;
        pass = await rig.SyncAsync();
        await Assert.That(pass.Error).IsNull();
        await Assert.That((await rig.Blocks.StatusAsync()).NeedsSignIn).IsFalse();
        await Assert.That(rig.Google.Live(calendarId).Count).IsEqualTo(1);
        await Assert.That(state!.SyncToken).IsNotNull();
    }
}
