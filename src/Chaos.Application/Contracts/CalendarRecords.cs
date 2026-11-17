namespace Chaos.Application.Contracts;

public enum CalendarProvider
{
    Feed = 1,
    Google = 2,
}

/// <summary>A calendar whose meetings are shown read-only (PLAN2 §10.2). A feed's address is NOT here: it is in the secret store.</summary>
public sealed record CalendarSourceRecord : AggregateRecord
{
    public required CalendarProvider Provider { get; init; }
    public string? AccountRef { get; init; }
    public required string ExternalId { get; init; }
    public required string Name { get; init; }
    public required string Color { get; init; }
    public required bool CountsAsBusy { get; init; }
    public required bool Enabled { get; init; }
    public string? OwnerEmail { get; init; }
    public required int Position { get; init; }
}

/// <summary>One cached meeting. Timed meetings use the UTC instants; all-day ones use local dates, end exclusive.</summary>
public sealed record CalendarEventRecord(
    string SourceId,
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
    bool Busy,
    DateTimeOffset FetchedUtc);

/// <summary>This device's last fetch of one source.</summary>
public sealed record CalendarSourceState(
    string SourceId,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    string? LastError,
    string? ETag,
    string? LastModified);

/// <summary>A task made from a meeting. Keeps the meeting's title and link so every device can show them.</summary>
public sealed record CalendarEventLinkRecord : AggregateRecord
{
    public required string TaskId { get; init; }
    public required CalendarProvider Provider { get; init; }
    public required string CalendarId { get; init; }
    public required string EventId { get; init; }
    public required DateTimeOffset EventStartUtc { get; init; }
    public required string EventTitle { get; init; }
    public string? EventWebUrl { get; init; }
}

/// <summary>A connected Google account (synced). The refresh token is NOT here: it is in the secret store.</summary>
public sealed record GoogleAccountRecord : AggregateRecord
{
    public required string Email { get; init; }
    public required string Subject { get; init; }
}

/// <summary>This device's view of an account: whether the last call said "sign in again", and when it last worked.</summary>
public sealed record GoogleAccountState(
    string AccountId,
    bool NeedsSignIn,
    DateTimeOffset? LastAttemptUtc,
    DateTimeOffset? LastSuccessUtc,
    string? LastError);
