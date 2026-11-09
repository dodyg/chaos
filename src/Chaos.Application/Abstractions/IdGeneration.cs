namespace Chaos.Application.Abstractions;

/// <summary>Production GUIDs; tests inject deterministic sequences (PLAN §4).</summary>
public interface IIdGenerator
{
    Guid NewId();
}

public sealed class GuidIdGenerator : IIdGenerator
{
    public Guid NewId() => Guid.NewGuid();
}

/// <summary>
/// Formats stable storage/RPC strings. IDs are canonical GUID strings in both SQL and RPC;
/// instants are fixed-width ISO 8601 UTC; dates yyyy-MM-dd; times HH:mm:ss (PLAN §5).
/// </summary>
public static class StorageFormats
{
    public static string Id(Guid id) => id.ToString("D");

    public static string Instant(DateTimeOffset instant) => instant.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static string Time(TimeOnly time) => time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    public static DateTimeOffset? ParseInstant(string? text) =>
        text is null ? null : DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    public static DateOnly? ParseDate(string? text) =>
        text is null ? null : DateOnly.ParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static TimeOnly? ParseTime(string? text) =>
        text is null ? null : TimeOnly.ParseExact(text, "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
