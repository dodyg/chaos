using System.Text.RegularExpressions;
using Chaos.Application.Abstractions;
using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Application.Planning;
using Chaos.Application.Queries;
using Chaos.Domain.Planning;
using Chaos.Sync;

namespace Chaos.Application.Calendar;

/// <summary>A source as Settings shows it: the synced row plus this device's last fetch.</summary>
public sealed record CalendarSourceView(CalendarSourceRecord Source, CalendarSourceState? State, int EventCount);

public sealed record AddFeedCommand(string Address, string Name, string Color, bool CountsAsBusy, string? OwnerEmail);

public sealed record UpdateSourceCommand(string SourceId, long ExpectedVersion, string Name, string Color, bool CountsAsBusy, bool Enabled, string? OwnerEmail);

/// <summary>A task made from a meeting (PLAN2 §10.4).</summary>
public sealed record MakeTaskFromEventCommand(string OperationId, string SourceId, string EventId, string? ContainerId);

public sealed record MeetingTaskResult(TaskRecord Task, bool Replayed);

/// <summary>
/// Adds, changes and removes calendar sources, and turns a meeting into a task (PLAN2 §10.2, §10.4).
/// A feed's address goes into <see cref="ISecretStore"/> and nowhere else: the database keeps a name,
/// a colour, the busy flag and the last fetch state.
/// </summary>
public sealed partial class CalendarCommands(IApplicationStore store, ISecretStore secrets, IIdGenerator ids, TimeProvider clock, CaptureCommands capture)
{
    public const int MaxSources = 30;
    public const int MaxAddressLength = 2000;

    public static string SecretKey(string sourceId) => $"chaos/calendar-feed/{sourceId}";

    public async Task<IReadOnlyList<CalendarSourceView>> ListAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var views = new List<CalendarSourceView>();
            foreach (var source in await session.Calendars.ListSourcesAsync(token))
            {
                views.Add(new CalendarSourceView(source, await session.Calendars.GetStateAsync(source.Id, token), 0));
            }

            return (IReadOnlyList<CalendarSourceView>)views;
        }, ct);

    /// <summary>Validates and normalizes a pasted address: webcal:// becomes https://, plain http is refused.</summary>
    public static Uri NormalizeAddress(string? address)
    {
        var text = (address ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            throw AppException.Validation("address", "Paste the calendar's ICS address.");
        }

        if (text.Length > MaxAddressLength)
        {
            throw AppException.Validation("address", "That address is too long.");
        }

        if (text.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            text = "https://" + text[9..];
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            throw AppException.Validation("address", "That does not look like a web address.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            throw AppException.Validation("address", "Calendar feeds must use https://. Plain http:// is not allowed.");
        }

        return uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : throw AppException.Validation("address", "Use an https:// or webcal:// address.");
    }

    public async Task<CalendarSourceView> AddFeedAsync(AddFeedCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var address = NormalizeAddress(command.Address);
        var name = NormalizeName(command.Name);
        var color = NormalizeColor(command.Color);
        var owner = NormalizeEmail(command.OwnerEmail);
        var id = StorageFormats.Id(ids.NewId());
        var now = clock.GetUtcNow();

        // Secret first: a source row without its address would be useless, while an address without a row is removed below.
        await secrets.SetAsync(SecretKey(id), address.ToString(), ct);
        try
        {
            var source = await store.ExecuteAsync(async (session, token) =>
            {
                var existing = await session.Calendars.ListSourcesAsync(token);
                if (existing.Count >= MaxSources)
                {
                    throw AppException.Validation("address", $"Chaos shows at most {MaxSources} calendars. Remove one first.");
                }

                var row = new CalendarSourceRecord
                {
                    Id = id,
                    Provider = CalendarProvider.Feed,
                    AccountRef = null,
                    ExternalId = id,
                    Name = name,
                    Color = color,
                    CountsAsBusy = command.CountsAsBusy,
                    Enabled = true,
                    OwnerEmail = owner,
                    Position = existing.Count == 0 ? 0 : existing.Max(item => item.Position) + 1,
                    Version = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await session.Calendars.AddSourceAsync(row, token);
                return row;
            }, ct);
            return new CalendarSourceView(source, null, 0);
        }
        catch
        {
            await secrets.DeleteAsync(SecretKey(id), CancellationToken.None);
            throw;
        }
    }

    public async Task<CalendarSourceView> UpdateAsync(UpdateSourceCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var name = NormalizeName(command.Name);
        var color = NormalizeColor(command.Color);
        var owner = NormalizeEmail(command.OwnerEmail);
        return await store.ExecuteAsync(async (session, token) =>
        {
            var existing = await session.Calendars.GetSourceAsync(command.SourceId, token);
            if (existing is null || existing.DeletedAtUtc is not null)
            {
                throw AppException.NotFound("That calendar was removed. Refresh and try again.");
            }

            if (existing.Version != command.ExpectedVersion)
            {
                throw AppException.VersionConflict("That calendar changed elsewhere. Refresh and try again.");
            }

            var saved = await session.Calendars.UpdateSourceAsync(existing with
            {
                Name = name,
                Color = color,
                CountsAsBusy = command.CountsAsBusy,
                Enabled = command.Enabled,
                OwnerEmail = owner,
                Version = existing.Version + 1,
                UpdatedAtUtc = clock.GetUtcNow(),
            }, existing.Version, token) ?? throw AppException.VersionConflict("That calendar changed elsewhere. Refresh and try again.");
            return new CalendarSourceView(saved, await session.Calendars.GetStateAsync(saved.Id, token), 0);
        }, ct);
    }

    /// <summary>Removes the calendar everywhere (the row is tombstoned and synced), drops the cache and forgets the address.</summary>
    public async Task RemoveAsync(string sourceId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await store.ExecuteAsync(async (session, token) =>
        {
            var existing = await session.Calendars.GetSourceAsync(sourceId, token);
            if (existing is null || existing.DeletedAtUtc is not null)
            {
                return 0;
            }

            var now = clock.GetUtcNow();
            await session.Calendars.UpdateSourceAsync(existing with { DeletedAtUtc = now, UpdatedAtUtc = now, Version = existing.Version + 1 }, existing.Version, token);
            await session.Calendars.DeleteEventsAsync(sourceId, token);
            return 0;
        }, ct);
        await secrets.DeleteAsync(SecretKey(sourceId), ct);
    }

    /// <summary>
    /// "Prepare: &lt;title&gt;", planned the day before the meeting (or the same day when it starts after noon),
    /// linked to the meeting. Safe to repeat: the same operation returns the same task and a task keeps one link.
    /// </summary>
    public async Task<MeetingTaskResult> MakeTaskAsync(MakeTaskFromEventCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (meeting, source, zone) = await store.QueryAsync(async (session, token) =>
        {
            var item = await session.Calendars.GetEventAsync(command.SourceId, command.EventId, token)
                ?? throw AppException.NotFound("That meeting is no longer on the calendar. Refresh and try again.");
            var origin = await session.Calendars.GetSourceAsync(command.SourceId, token)
                ?? throw AppException.NotFound("That calendar was removed.");
            var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
            return (item, origin, context.Zone);
        }, ct);

        var (day, startsAfterNoon) = MeetingStart(meeting, zone);
        var planned = startsAfterNoon ? day : day.AddDays(-1);
        var title = $"Prepare: {meeting.Title}";
        var created = await capture.CreateAsync(new CreateCapturedTasksCommand(command.OperationId,
        [
            new CaptureTaskDraft
            {
                Title = title.Length > 200 ? title[..200] : title,
                ContainerId = command.ContainerId,
                PlannedDate = planned,
            },
        ]), ct);
        var task = created.Tasks[0];

        await store.ExecuteAsync(async (session, token) =>
        {
            if (await session.Calendars.FindLinkAsync(task.Id, token) is not null)
            {
                return 0;
            }

            var now = clock.GetUtcNow();
            await session.Calendars.AddLinkAsync(new CalendarEventLinkRecord
            {
                Id = StorageFormats.NameBasedId(NameBasedId.Names.CalendarEventLink(task.Id)),
                TaskId = task.Id,
                Provider = source.Provider,
                CalendarId = source.ExternalId,
                EventId = meeting.ExternalId,
                EventStartUtc = meeting.StartUtc,
                EventTitle = meeting.Title,
                EventWebUrl = meeting.WebUrl ?? meeting.VideoUrl,
                Version = 1,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            }, token);
            return 0;
        }, ct);
        return new MeetingTaskResult(task, created.Replayed);
    }

    /// <summary>The meeting a task came from, for the "From meeting …" line; null when the task has no link.</summary>
    public async Task<IReadOnlyDictionary<string, CalendarEventLinkRecord>> LinksAsync(IReadOnlyList<string> taskIds, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
            (await session.Calendars.ListLinksAsync(taskIds, token)).ToDictionary(link => link.TaskId, StringComparer.Ordinal), ct);

    private static (DateOnly Day, bool StartsAfterNoon) MeetingStart(CalendarEventRecord meeting, TimeZoneInfo zone)
    {
        if (meeting.AllDay)
        {
            return (meeting.StartDate!.Value, false);
        }

        var local = TimeZoneInfo.ConvertTime(meeting.StartUtc, zone);
        return (DateOnly.FromDateTime(local.DateTime), local.TimeOfDay > TimeSpan.FromHours(12));
    }

    private static string NormalizeName(string? name)
    {
        var text = (name ?? string.Empty).Trim();
        return text.Length is >= 1 and <= 120 ? text : throw AppException.Validation("name", "Give the calendar a name of up to 120 characters.");
    }

    private static string NormalizeColor(string? color)
    {
        var text = (color ?? string.Empty).Trim();
        return HexColor().IsMatch(text) ? text.ToLowerInvariant() : throw AppException.Validation("color", "Choose a colour.");
    }

    private static string? NormalizeEmail(string? email)
    {
        var text = (email ?? string.Empty).Trim();
        return text.Length == 0 ? null
            : text.Length <= 254 && text.Contains('@') && !text.Contains(' ') ? text
            : throw AppException.Validation("ownerEmail", "Enter an email address, or leave it empty.");
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();
}
