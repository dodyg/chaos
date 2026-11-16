using System.Globalization;
using System.Text.RegularExpressions;
using Chaos.Application.Abstractions;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace Chaos.Infrastructure.Calendar;

/// <summary>
/// ICS parsing on Ical.Net (PLAN2 §10.2). Recurrences expand only inside the cache window. Transparent
/// events are free, cancelled ones are hidden, the owner's own declined invitations are hidden when the
/// owner is known, all-day events are busy only when TRANSP:OPAQUE is explicit, time zones come from
/// VTIMEZONE/TZID, and floating times use the profile's zone.
/// </summary>
public sealed partial class IcsParser : IIcsParser
{
    /// <summary>A feed cannot make the cache unbounded.</summary>
    public const int MaxMeetings = 5000;

    public IReadOnlyList<ParsedMeeting> Parse(string text, IcsParseOptions options)
    {
        Ical.Net.Calendar? calendar;
        try
        {
            calendar = Ical.Net.Calendar.Load(text);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new CalendarFeedException("The address did not return a calendar Chaos can read.", error);
        }

        if (calendar is null || !text.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new CalendarFeedException("The address did not return a calendar Chaos can read.");
        }

        var result = new List<ParsedMeeting>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var windowFrom = new CalDateTime(options.WindowFromUtc.UtcDateTime, "UTC");
        IEnumerable<Occurrence> occurrences = calendar.GetOccurrences<CalendarEvent>(windowFrom)
            .TakeWhile(item => ToUtc(item.Period.StartTime, options.FloatingZone) < options.WindowToUtc);
        try
        {
            foreach (var occurrence in occurrences)
            {
                if (result.Count >= MaxMeetings)
                {
                    break;
                }

                if (occurrence.Source is not CalendarEvent evt || Hidden(evt, options.OwnerEmail))
                {
                    continue;
                }

                var meeting = ToMeeting(evt, occurrence, options);
                if (meeting is not null && meeting.EndUtc > options.WindowFromUtc && meeting.StartUtc < options.WindowToUtc && seen.Add(meeting.ExternalId))
                {
                    result.Add(meeting);
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException and not CalendarFeedException)
        {
            throw new CalendarFeedException("The calendar could not be read: it holds an event Chaos cannot expand.", error);
        }

        return [.. result.OrderBy(item => item.StartUtc).ThenBy(item => item.ExternalId, StringComparer.Ordinal)];
    }

    private static bool Hidden(CalendarEvent evt, string? ownerEmail)
    {
        if (string.Equals(evt.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(ownerEmail))
        {
            return false;
        }

        foreach (var attendee in evt.Attendees)
        {
            var address = attendee.Value?.ToString() ?? string.Empty;
            if (address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                && string.Equals(address[7..], ownerEmail.Trim(), StringComparison.OrdinalIgnoreCase)
                && string.Equals(attendee.ParticipationStatus, "DECLINED", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ParsedMeeting? ToMeeting(CalendarEvent evt, Occurrence occurrence, IcsParseOptions options)
    {
        var start = occurrence.Period.StartTime;
        var end = occurrence.Period.EffectiveEndTime ?? occurrence.Period.EndTime ?? start;
        var title = string.IsNullOrWhiteSpace(evt.Summary) ? "(No title)" : evt.Summary.Trim();
        var explicitOpaque = string.Equals(evt.Transparency, "OPAQUE", StringComparison.OrdinalIgnoreCase);
        var transparent = string.Equals(evt.Transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase);
        var uid = string.IsNullOrWhiteSpace(evt.Uid) ? title : evt.Uid;
        var video = FindVideoUrl(evt);
        var web = evt.Url is { IsAbsoluteUri: true } url && url.Scheme is "https" or "http" ? url.ToString() : null;
        var location = string.IsNullOrWhiteSpace(evt.Location) ? null : evt.Location.Trim();

        if (!start.HasTime)
        {
            var first = DateOnly.FromDateTime(start.Value);
            var last = end.HasTime || end.Value <= start.Value ? first.AddDays(1) : DateOnly.FromDateTime(end.Value);
            if (last <= first)
            {
                last = first.AddDays(1);
            }

            return new ParsedMeeting(
                $"{uid}|{first:yyyy-MM-dd}",
                new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                new DateTimeOffset(last.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                true, first, last, title, location, video, web, Busy: explicitOpaque);
        }

        var startUtc = ToUtc(start, options.FloatingZone);
        var endUtc = end.HasTime ? ToUtc(end, options.FloatingZone) : startUtc;
        if (endUtc < startUtc)
        {
            endUtc = startUtc;
        }

        return new ParsedMeeting(
            $"{uid}|{startUtc.UtcDateTime:yyyyMMddTHHmmssZ}", startUtc, endUtc, false, null, null,
            title, location, video, web, Busy: !transparent);
    }

    /// <summary>A zoned time converts by its own zone; a floating one is read in the profile's zone.</summary>
    private static DateTimeOffset ToUtc(CalDateTime value, TimeZoneInfo floatingZone)
    {
        if (value.IsUtc)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
        }

        var local = DateTime.SpecifyKind(value.Value, DateTimeKind.Unspecified);
        var zone = floatingZone;
        if (!string.IsNullOrEmpty(value.TzId) && !value.IsFloating)
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(value.TzId);
            }
            catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return new DateTimeOffset(DateTime.SpecifyKind(value.AsUtc, DateTimeKind.Utc));
            }
        }

        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private static string? FindVideoUrl(CalendarEvent evt)
    {
        foreach (var property in evt.Properties)
        {
            if (property.Name.Equals("X-GOOGLE-CONFERENCE", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("X-MICROSOFT-SKYPETEAMSMEETINGURL", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("CONFERENCE", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value?.ToString() is { } text && Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == "https")
                {
                    return uri.ToString();
                }
            }
        }

        foreach (var text in new[] { evt.Location, evt.Description })
        {
            if (!string.IsNullOrEmpty(text) && VideoLink().Match(text) is { Success: true } match)
            {
                return match.Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"https://[\w.-]*(zoom\.us|meet\.google\.com|teams\.microsoft\.com|teams\.live\.com|webex\.com|whereby\.com)/[^\s<>""')]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VideoLink();
}
