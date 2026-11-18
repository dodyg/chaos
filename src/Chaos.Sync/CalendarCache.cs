namespace Chaos.Sync;

/// <summary>A read-only cache uploaded by a desktop after a successful calendar fetch.</summary>
public sealed record CalendarCacheEvent(string ExternalId, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    bool AllDay, DateOnly? StartDate, DateOnly? EndDate, string Title, string? Location, string? VideoUrl, string? WebUrl, bool Busy, DateTimeOffset FetchedUtc);

public sealed record CalendarCacheSnapshot(string SourceId, DateTimeOffset FetchedAt, IReadOnlyList<CalendarCacheEvent> Events);
