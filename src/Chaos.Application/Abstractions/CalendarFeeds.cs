namespace Chaos.Application.Abstractions;

/// <summary>
/// Credentials kept outside the database, protected by the operating system (PLAN2 §10.2: a secret ICS
/// address is a credential). Keys are short names such as <c>chaos/calendar-feed/{sourceId}</c>.
/// </summary>
public interface ISecretStore
{
    Task SetAsync(string key, string value, CancellationToken ct = default);
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>Protects bytes for this user on this machine (DPAPI on Windows).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] protectedBytes);
}

public enum FeedFetchStatus
{
    /// <summary>New content was downloaded.</summary>
    Content = 0,
    /// <summary>The server answered 304: the cached copy is current.</summary>
    NotModified = 1,
}

public sealed record FeedFetchResult(FeedFetchStatus Status, string? Text, string? ETag, string? LastModified);

/// <summary>A failure the person can read: the feed refused, timed out, was too big or was not HTTPS.</summary>
public sealed class CalendarFeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Downloads an ICS feed: HTTPS only, size and time limits, redirects capped and kept on HTTPS, conditional GET.</summary>
public interface ICalendarFeedClient
{
    Task<FeedFetchResult> FetchAsync(Uri address, string? etag, string? lastModified, CancellationToken ct);
}

/// <summary>One meeting as parsed from a feed, ready for the cache. All-day meetings use local dates, the end exclusive.</summary>
public sealed record ParsedMeeting(
    string ExternalId,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    bool AllDay,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string Title,
    string? Location,
    string? VideoUrl,
    string? WebUrl,
    bool Busy);

public sealed record IcsParseOptions(DateTimeOffset WindowFromUtc, DateTimeOffset WindowToUtc, TimeZoneInfo FloatingZone, string? OwnerEmail);

/// <summary>Turns ICS text into meetings inside the cache window, applying PLAN2 §10.2's rules.</summary>
public interface IIcsParser
{
    /// <exception cref="CalendarFeedException">The text is not a usable calendar.</exception>
    IReadOnlyList<ParsedMeeting> Parse(string text, IcsParseOptions options);
}
