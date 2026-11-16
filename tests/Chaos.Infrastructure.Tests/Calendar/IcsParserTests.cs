using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Calendar;

namespace Chaos.Infrastructure.Tests.Calendar;

/// <summary>ICS rules of PLAN2 §10.2 against exports shaped like Google's, Outlook's and Apple's.</summary>
public sealed class IcsParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private static IReadOnlyList<ParsedMeeting> Parse(string text, string? owner = null, TimeZoneInfo? zone = null) =>
        new IcsParser().Parse(text, new IcsParseOptions(Now.AddDays(-7), Now.AddDays(56), zone ?? TimeZoneInfo.Utc, owner));

    [Test]
    public async Task Google_BusyTransparentAndAllDay_FollowTheRules()
    {
        var meetings = Parse(IcsFixtures.Google);

        var standup = meetings.Single(item => item.Title == "Standup");
        await Assert.That(standup.StartUtc).IsEqualTo(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero));
        await Assert.That(standup.EndUtc).IsEqualTo(new DateTimeOffset(2026, 11, 17, 11, 0, 0, TimeSpan.Zero));
        await Assert.That(standup.Busy).IsTrue();
        await Assert.That(standup.Location).IsEqualTo("Room 4");
        await Assert.That(standup.VideoUrl).IsEqualTo("https://meet.google.com/abc-defg-hij");

        // TRANSP:TRANSPARENT is free.
        await Assert.That(meetings.Single(item => item.Title == "Focus time").Busy).IsFalse();

        // All-day: shown, free unless OPAQUE is explicit.
        var holiday = meetings.Single(item => item.Title == "Public holiday");
        await Assert.That(holiday.AllDay).IsTrue();
        await Assert.That(holiday.StartDate).IsEqualTo(new DateOnly(2026, 11, 18));
        await Assert.That(holiday.EndDate).IsEqualTo(new DateOnly(2026, 11, 20));
        await Assert.That(holiday.Busy).IsFalse();
        await Assert.That(meetings.Single(item => item.Title == "Offsite").Busy).IsTrue();
    }

    [Test]
    public async Task Outlook_WindowsZoneNamesAndAVtimezone_ConvertToTheRightInstant()
    {
        var meeting = Parse(IcsFixtures.Outlook).Single();

        // Central Europe is UTC+1 in November.
        await Assert.That(meeting.StartUtc).IsEqualTo(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        await Assert.That(meeting.EndUtc).IsEqualTo(new DateTimeOffset(2026, 11, 17, 8, 30, 0, TimeSpan.Zero));
        await Assert.That(meeting.VideoUrl).StartsWith("https://teams.microsoft.com/");
    }

    [Test]
    public async Task Apple_IanaZoneNames_ConvertToTheRightInstant()
    {
        var meeting = Parse(IcsFixtures.Apple).Single();

        await Assert.That(meeting.StartUtc).IsEqualTo(new DateTimeOffset(2026, 11, 17, 14, 0, 0, TimeSpan.Zero));
        await Assert.That(meeting.Busy).IsTrue();
    }

    [Test]
    public async Task Recurring_ExpandsInsideTheWindow_AppliesExceptionsMovesAndCancellations()
    {
        var meetings = Parse(IcsFixtures.Recurring);
        var starts = meetings.Select(item => item.StartUtc).ToList();

        // Window is 9 Nov to 11 Jan. 10 Nov stays; the cancelled 17 Nov and the removed 24 Nov are gone.
        await Assert.That(starts.Contains(new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero))).IsTrue();
        await Assert.That(starts.Contains(new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero))).IsFalse();
        await Assert.That(starts.Contains(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero))).IsFalse();

        // The moved instance appears once, at its new time and title, not also at the old one.
        await Assert.That(starts.Contains(new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.Zero))).IsFalse();
        await Assert.That(meetings.Single(item => item.StartUtc == new DateTimeOffset(2026, 12, 1, 14, 0, 0, TimeSpan.Zero)).Title).IsEqualTo("Weekly sync (moved)");

        // Occurrences before the window are not produced; those inside are, one per week, with distinct ids.
        await Assert.That(starts.Any(start => start < Now.AddDays(-7))).IsFalse();
        await Assert.That(starts.Any(start => start > Now.AddDays(56))).IsFalse();
        await Assert.That(meetings.Count).IsEqualTo(7);
        await Assert.That(meetings.Select(item => item.ExternalId).Distinct().Count()).IsEqualTo(meetings.Count);
    }

    [Test]
    public async Task ADeclinedInvitation_IsHiddenWhenTheOwnerIsKnown_AndShownOtherwise()
    {
        await Assert.That(Parse(IcsFixtures.Declined, owner: "me@example.com")).IsEmpty();
        await Assert.That(Parse(IcsFixtures.Declined, owner: "ME@example.com")).IsEmpty();
        await Assert.That(Parse(IcsFixtures.Declined, owner: "them@example.com").Count).IsEqualTo(1);
        await Assert.That(Parse(IcsFixtures.Declined).Count).IsEqualTo(1);
    }

    [Test]
    public async Task FloatingTimes_UseTheProfileZone()
    {
        var meeting = Parse(IcsFixtures.Floating, zone: NewYork).Single();

        // 13:00 in New York on 17 November is 18:00 UTC.
        await Assert.That(meeting.StartUtc).IsEqualTo(new DateTimeOffset(2026, 11, 17, 18, 0, 0, TimeSpan.Zero));
        await Assert.That(Parse(IcsFixtures.Floating).Single().StartUtc).IsEqualTo(new DateTimeOffset(2026, 11, 17, 13, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task EventsOutsideTheWindow_AreDropped()
    {
        var meetings = new IcsParser().Parse(IcsFixtures.Google, new IcsParseOptions(Now.AddDays(30), Now.AddDays(60), TimeZoneInfo.Utc, null));

        await Assert.That(meetings).IsEmpty();
    }

    [Test]
    public async Task Malformed_ThrowsAReadableError()
    {
        var html = Assert.Throws<CalendarFeedException>(() => Parse(IcsFixtures.Malformed));
        await Assert.That(html.Message).Contains("calendar");
        await Assert.That(() => Parse(string.Empty)).Throws<CalendarFeedException>();
    }

    [Test]
    public async Task ATruncatedFile_NeverThrowsAnythingButAFeedError()
    {
        Exception? caught = null;
        try
        {
            _ = Parse(IcsFixtures.Truncated);
        }
        catch (Exception error)
        {
            caught = error;
        }

        await Assert.That(caught is null or CalendarFeedException).IsTrue();
    }
}
