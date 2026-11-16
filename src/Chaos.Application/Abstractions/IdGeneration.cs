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

    /// <summary>The stored id of a natural-key row (PLAN2 §7.5): the same name gives the same id on every device.</summary>
    public static string NameBasedId(string name) => Id(Chaos.Sync.NameBasedId.Create(name));

    public static string Instant(DateTimeOffset instant) => instant.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    public static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static string Time(TimeOnly time) => time.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>An instant to bind as a SQL value: null stays null (SQL NULL), not an empty string.</summary>
    public static object? Optional(DateTimeOffset? instant) => instant is { } value ? Instant(value) : null;

    public static DateTimeOffset? ParseInstant(string? text) =>
        text is null ? null : DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);

    public static DateOnly? ParseDate(string? text) =>
        text is null ? null : DateOnly.ParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static TimeOnly? ParseTime(string? text) =>
        text is null ? null : TimeOnly.ParseExact(text, "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
