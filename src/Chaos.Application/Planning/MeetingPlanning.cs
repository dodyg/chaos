using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Planning;

namespace Chaos.Application.Planning;

/// <summary>What the cache holds for a range of local days: meetings per day and the busy intervals planning uses.</summary>
public sealed record MeetingDays(
    IReadOnlyList<CalendarMeeting> Meetings,
    IReadOnlyList<MeetingInterval> Intervals,
    TimeZoneInfo Zone)
{
    public static readonly MeetingDays None = new([], [], TimeZoneInfo.Utc);

    /// <summary>Busy minutes of one local day (overlaps counted once).</summary>
    public int BusyMinutes(DateOnly day) => MeetingTime.BusyMinutes(day, Intervals, Zone);
}

/// <summary>
/// Reads the meeting cache for planning (PLAN2 §10.4): shared by the calendar, My Week and My Day so they
/// agree. A meeting is busy only when it is busy in its feed AND its calendar counts as busy time.
/// </summary>
public static class MeetingPlanning
{
    public static async Task<MeetingDays> LoadAsync(IDataSession session, TimeZoneInfo zone, DateOnly from, DateOnly to, CancellationToken token)
    {
        var sources = (await session.Calendars.ListSourcesAsync(token)).Where(source => source.Enabled).ToDictionary(source => source.Id, StringComparer.Ordinal);
        if (sources.Count == 0)
        {
            return MeetingDays.None with { Zone = zone };
        }

        var fromUtc = MeetingTime.DayBounds(from, zone).Start;
        var toUtc = MeetingTime.DayBounds(to, zone).End;
        var events = await session.Calendars.ListEventsAsync(fromUtc, toUtc, from, to, token);
        var meetings = new List<CalendarMeeting>();
        var intervals = new List<MeetingInterval>();
        foreach (var item in events)
        {
            if (!sources.TryGetValue(item.SourceId, out var source))
            {
                continue;
            }

            var busy = item.Busy && source.CountsAsBusy;
            intervals.Add(item.AllDay
                ? MeetingInterval.AllDayRange(item.StartDate!.Value, item.EndDate!.Value, busy)
                : MeetingInterval.Timed(item.StartUtc, item.EndUtc, busy));
            AddDays(meetings, item, source, busy, zone, from, to);
        }

        meetings.Sort((a, b) =>
        {
            var byDate = a.Date.CompareTo(b.Date);
            if (byDate != 0)
            {
                return byDate;
            }

            // All-day meetings lead the day, then time order.
            var byAllDay = b.AllDay.CompareTo(a.AllDay);
            return byAllDay != 0 ? byAllDay : (a.Start ?? TimeOnly.MinValue).CompareTo(b.Start ?? TimeOnly.MinValue);
        });
        return new MeetingDays(meetings, intervals, zone);
    }

    private static void AddDays(List<CalendarMeeting> into, CalendarEventRecord item, CalendarSourceRecord source, bool busy, TimeZoneInfo zone, DateOnly from, DateOnly to)
    {
        string Id(DateOnly day) => $"{item.SourceId}|{item.ExternalId}|{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

        if (item.AllDay)
        {
            for (var day = item.StartDate!.Value; day < item.EndDate!.Value; day = day.AddDays(1))
            {
                if (day >= from && day <= to)
                {
                    into.Add(new CalendarMeeting(Id(day), source.Id, item.ExternalId, source.Name, item.Title, day, null, null, true, source.Color, busy, item.Location, item.VideoUrl, item.WebUrl));
                }
            }

            return;
        }

        var startLocal = TimeZoneInfo.ConvertTime(item.StartUtc, zone);
        var endLocal = TimeZoneInfo.ConvertTime(item.EndUtc, zone);
        var last = DateOnly.FromDateTime(endLocal.DateTime);
        if (endLocal.TimeOfDay == TimeSpan.Zero && endLocal > startLocal)
        {
            last = last.AddDays(-1);
        }

        for (var day = DateOnly.FromDateTime(startLocal.DateTime); day <= last; day = day.AddDays(1))
        {
            if (day < from || day > to)
            {
                continue;
            }

            var begin = day == DateOnly.FromDateTime(startLocal.DateTime) ? TimeOnly.FromDateTime(startLocal.DateTime) : TimeOnly.MinValue;
            var finish = day == DateOnly.FromDateTime(endLocal.DateTime) && endLocal.TimeOfDay != TimeSpan.Zero
                ? TimeOnly.FromDateTime(endLocal.DateTime)
                : new TimeOnly(23, 59);
            into.Add(new CalendarMeeting(Id(day), source.Id, item.ExternalId, source.Name, item.Title, day, begin, finish, false, source.Color, busy, item.Location, item.VideoUrl, item.WebUrl));
        }
    }
}
