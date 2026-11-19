using System.Security.Cryptography;
using System.Text;

namespace Chaos.Sync;

/// <summary>
/// Name-based UUIDs, version 5 (RFC 9562, SHA-1): two devices that create the same real thing
/// while offline derive the same ID (PLAN2 §7.5). Verified against the RFC test vectors.
/// </summary>
public static class NameBasedId
{
    /// <summary>RFC 9562's pre-registered namespace for fully-qualified domain names.</summary>
    public static readonly Guid DnsNamespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    /// <summary>
    /// The namespace Chaos hashes its natural keys in (PLAN2 §7.5). Fixed forever: changing it
    /// would give every deterministic row a different ID on each device.
    /// </summary>
    public static readonly Guid ChaosNamespace = new("9a5f9d0e-6d64-4f0f-9b1a-3f2c05d17e42");

    /// <summary>Hashes <paramref name="name"/> in <paramref name="namespaceId"/> to a version 5 UUID.</summary>
    public static Guid Create(Guid namespaceId, string name)
    {
        var hash = SHA1.HashData(Concat(namespaceId, name));

        // Version 5 in the high nibble of octet 6, variant 10xx in octet 8 (RFC 9562 §4.1.7/§4.1.8).
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        // Octets in network order: the int/short constructor keeps the canonical UUID rendering
        // independent of the machine's endianness.
        return new Guid(
            (hash[0] << 24) | (hash[1] << 16) | (hash[2] << 8) | hash[3],
            (short)((hash[4] << 8) | hash[5]),
            (short)((hash[6] << 8) | hash[7]),
            hash[8], hash[9], hash[10], hash[11], hash[12], hash[13], hash[14], hash[15]);
    }

    /// <summary>Hashes a Chaos natural key (PLAN2 §7.5 table) in <see cref="ChaosNamespace"/>.</summary>
    public static Guid Create(string name) => Create(ChaosNamespace, name);

    /// <summary>The natural-key names of every deterministic ID kind (PLAN2 §7.5). One place,
    /// used by the commands that create these rows and by the tests that prove two fresh data
    /// roots derive the same IDs.</summary>
    public static class Names
    {
        public static string DayPlan(string profileId, DateOnly localDate) => $"dayplan:{profileId}:{StorageDate(localDate)}";
        public static string DayPlanItem(string dayPlanId, string taskId) => $"dayplanitem:{dayPlanId}:{taskId}";
        public static string WeekPlan(string profileId, DateOnly weekStart) => $"weekplan:{profileId}:{StorageDate(weekStart)}";
        public static string WeekAssignment(string weekPlanId, string taskId) => $"weekassign:{weekPlanId}:{taskId}";
        public static string DailyPage(string spaceId, DateOnly date) => $"dailypage:{spaceId}:{StorageDate(date)}";
        public static string Occurrence(string seriesId, string nominalKey) => $"occurrence:{seriesId}:{nominalKey}";
        public static string OccurrenceTask(string seriesId, string nominalKey) => $"occurrencetask:{seriesId}:{nominalKey}";
        public static string OccurrenceSubtask(string occurrenceTaskId, int templateIndex) => $"occurrencesubtask:{occurrenceTaskId}:{templateIndex}";
        public static string Summary(string profileId, string kind, DateOnly periodStart, int revision) => $"summary:{profileId}:{kind}:{StorageDate(periodStart)}:{revision}";
        public static string Availability(string profileId, int weekday) => $"availability:{profileId}:{weekday}";
        public static string AvailabilityOverride(string profileId, DateOnly date) => $"availabilityoverride:{profileId}:{StorageDate(date)}";
        public static string ReminderAcknowledgement(int sourceKind, string sourceId, DateTimeOffset target) =>
            $"reminderack:{sourceKind}:{sourceId}:{target.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture)}";
        public static string GitHubTask(string connectionId, long repositoryId, int issueNumber) => $"githubtask:{connectionId}:{repositoryId}:{issueNumber}";
        public static string BoardSetting(string targetKind, string targetId) => $"boardsetting:{targetKind}:{targetId}";
        public static string CalendarEventLink(string taskId) => $"calendareventlink:{taskId}";
        public static string GoogleAccount(string subject) => $"googleaccount:{subject}";
        public static string GoogleCalendarSource(string accountId, string calendarId) => $"calendarsource:google:{accountId}:{calendarId}";

        private static string StorageDate(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Concat(Guid namespaceId, string name)
    {
        var result = new byte[16 + Encoding.UTF8.GetByteCount(name)];
        WriteNetworkOrder(namespaceId, result);
        Encoding.UTF8.GetBytes(name, result.AsSpan(16));
        return result;
    }

    /// <summary>Writes the UUID as 16 octets in network order (RFC 9562 §4), the opposite of
    /// <see cref="Guid.ToByteArray"/>, which follows the mixed-endian field layout.</summary>
    private static void WriteNetworkOrder(Guid value, byte[] destination)
    {
        var mixed = value.ToByteArray();
        destination[0] = mixed[3];
        destination[1] = mixed[2];
        destination[2] = mixed[1];
        destination[3] = mixed[0];
        destination[4] = mixed[5];
        destination[5] = mixed[4];
        destination[6] = mixed[7];
        destination[7] = mixed[6];
        mixed.AsSpan(8, 8).CopyTo(destination.AsSpan(8));
    }
}
