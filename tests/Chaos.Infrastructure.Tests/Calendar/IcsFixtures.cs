namespace Chaos.Infrastructure.Tests.Calendar;

/// <summary>Calendar exports in the shapes the real products produce (PLAN2 P5), as raw ICS text.</summary>
internal static class IcsFixtures
{
    private static string Lines(string text) => text.ReplaceLineEndings("\r\n").TrimStart();

    /// <summary>Google Calendar's export: UTC times, a transparent focus block, all-day events and a Meet link.</summary>
    public static readonly string Google = Lines("""
        BEGIN:VCALENDAR
        PRODID:-//Google Inc//Google Calendar 70.9054//EN
        VERSION:2.0
        CALSCALE:GREGORIAN
        X-WR-CALNAME:Work
        BEGIN:VEVENT
        DTSTART:20261117T100000Z
        DTEND:20261117T110000Z
        UID:standup-1@google.com
        STATUS:CONFIRMED
        SUMMARY:Standup
        TRANSP:OPAQUE
        LOCATION:Room 4
        X-GOOGLE-CONFERENCE:https://meet.google.com/abc-defg-hij
        END:VEVENT
        BEGIN:VEVENT
        DTSTART:20261117T130000Z
        DTEND:20261117T140000Z
        UID:focus-1@google.com
        STATUS:CONFIRMED
        SUMMARY:Focus time
        TRANSP:TRANSPARENT
        END:VEVENT
        BEGIN:VEVENT
        DTSTART;VALUE=DATE:20261118
        DTEND;VALUE=DATE:20261120
        UID:holiday-1@google.com
        SUMMARY:Public holiday
        END:VEVENT
        BEGIN:VEVENT
        DTSTART;VALUE=DATE:20261119
        DTEND;VALUE=DATE:20261120
        UID:offsite-1@google.com
        SUMMARY:Offsite
        TRANSP:OPAQUE
        END:VEVENT
        END:VCALENDAR
        """);

    /// <summary>Outlook's export names zones the Windows way, with a VTIMEZONE block.</summary>
    public static readonly string Outlook = Lines("""
        BEGIN:VCALENDAR
        PRODID:Microsoft Exchange Server 2010
        VERSION:2.0
        BEGIN:VTIMEZONE
        TZID:W. Europe Standard Time
        BEGIN:STANDARD
        DTSTART:16010101T030000
        TZOFFSETFROM:+0200
        TZOFFSETTO:+0100
        RRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=-1SU;BYMONTH=10
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:16010101T020000
        TZOFFSETFROM:+0100
        TZOFFSETTO:+0200
        RRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=-1SU;BYMONTH=3
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VEVENT
        DTSTART;TZID=W. Europe Standard Time:20261117T090000
        DTEND;TZID=W. Europe Standard Time:20261117T093000
        UID:040000008200E00074C5B7101A82E00800000000
        SUMMARY:Budget review
        LOCATION:https://teams.microsoft.com/l/meetup-join/19%3ameeting_abc
        TRANSP:OPAQUE
        END:VEVENT
        END:VCALENDAR
        """);

    /// <summary>Apple Calendar uses IANA zone names.</summary>
    public static readonly string Apple = Lines("""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Apple Inc.//macOS 15.1//EN
        BEGIN:VEVENT
        DTSTART;TZID=America/New_York:20261117T090000
        DTEND;TZID=America/New_York:20261117T100000
        UID:apple-1
        SUMMARY:Dentist
        END:VEVENT
        END:VCALENDAR
        """);

    /// <summary>A weekly meeting with one date removed, one instance moved and retitled, and one cancelled.</summary>
    public static readonly string Recurring = Lines("""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VEVENT
        UID:weekly-1
        DTSTART:20261103T100000Z
        DTEND:20261103T103000Z
        RRULE:FREQ=WEEKLY;BYDAY=TU
        EXDATE:20261124T100000Z
        SUMMARY:Weekly sync
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly-1
        RECURRENCE-ID:20261201T100000Z
        DTSTART:20261201T140000Z
        DTEND:20261201T143000Z
        SUMMARY:Weekly sync (moved)
        END:VEVENT
        BEGIN:VEVENT
        UID:weekly-1
        RECURRENCE-ID:20261117T100000Z
        DTSTART:20261117T100000Z
        DTEND:20261117T103000Z
        STATUS:CANCELLED
        SUMMARY:Weekly sync
        END:VEVENT
        END:VCALENDAR
        """);

    /// <summary>The owner declined this one.</summary>
    public static readonly string Declined = Lines("""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VEVENT
        UID:declined-1
        DTSTART:20261117T150000Z
        DTEND:20261117T160000Z
        SUMMARY:Optional workshop
        ATTENDEE;PARTSTAT=DECLINED;CN=Me:mailto:me@example.com
        ATTENDEE;PARTSTAT=ACCEPTED;CN=Them:mailto:them@example.com
        END:VEVENT
        END:VCALENDAR
        """);

    /// <summary>No zone and no Z: it means local time in whatever zone the person is in.</summary>
    public static readonly string Floating = Lines("""
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Test//EN
        BEGIN:VEVENT
        UID:floating-1
        DTSTART:20261117T130000
        DTEND:20261117T140000
        SUMMARY:Lunch
        END:VEVENT
        END:VCALENDAR
        """);

    public const string Malformed = "<html><body>Sign in to see this calendar</body></html>";

    public static readonly string Truncated = Lines("""
        BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:cut-1
        DTSTART:2026
        """);
}
