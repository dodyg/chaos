using Chaos.Domain.Recurrence;
using Chaos.Domain.Tasks;

namespace Chaos.Domain.Capture;

public enum CaptureTokenKind
{
    PlannedDate,
    DueDate,
    Time,
    Recurrence,
    Reminder,
    Estimate,
    Tag,
    Priority,
    Container,
    Space,
    Waiting,
    FollowUp,
    Delegation,
}

/// <summary>
/// Applied tokens fill a field and leave the title. Kept tokens were recognized but stay in
/// the title as plain text, with a warning saying why (a conflict, an unknown list, a feature
/// that arrives later). Nothing recognized is ever silently discarded.
/// </summary>
public enum CaptureTokenState
{
    Applied,
    Kept,
}

/// <summary>A half-open UTF-16 range [Start, End) of the original input.</summary>
public readonly record struct CaptureSpan(int Start, int End)
{
    public bool Overlaps(int start, int end) => start < End && Start < end;
}

/// <summary>A recognized part of the input, in original UTF-16 offsets, with its language evidence (null for language-neutral forms such as 15:30 or #tag).</summary>
public sealed record CaptureToken(CaptureTokenKind Kind, int Start, int End, string Text, string? Language, CaptureTokenState State, string Summary);

public sealed record CaptureWarning(string Code, string Message, int? Start = null, int? End = null);

public sealed record KnownContainer(string Id, string Name);

public enum CaptureReminderAnchor
{
    Absolute,
    Planned,
    Due,
}

/// <summary>A reminder proposal: an offset from the planned/due anchor (negative = before) or an absolute instant.</summary>
public sealed record CaptureReminder(CaptureReminderAnchor Anchor, int? OffsetMinutes, DateTimeOffset? AtUtc);

/// <summary>A tag path as typed (display spelling kept) and whether the whole path already exists.</summary>
public sealed record CaptureTag(string Path, bool Existing);

/// <summary>
/// Everything the parser needs, captured once by the caller (PLAN §8): no clock access,
/// database reads or tag creation happen during a preview.
/// </summary>
public sealed record QuickAddContext(
    DateTimeOffset NowUtc,
    string ZoneId,
    string DateFormat,
    DayOfWeek FirstWeekday,
    TimeOnly DefaultReminderTime,
    IReadOnlyList<KnownContainer> Containers,
    IReadOnlyList<string> TagPaths,
    IReadOnlyList<CaptureSpan> ExcludedSpans);

/// <summary>The task fields the preview proposes; every field stays editable before saving.</summary>
public sealed record QuickAddDraft
{
    public required string Title { get; init; }
    public DateOnly? PlannedDate { get; init; }
    public TimeOnly? PlannedTime { get; init; }
    public DateOnly? DueDate { get; init; }
    public TimeOnly? DueTime { get; init; }
    /// <summary>The zone that timed values belong to (the profile zone at preview time).</summary>
    public required string ZoneId { get; init; }
    public TaskPriority? Priority { get; init; }
    public int? EstimateMinutes { get; init; }
    public RecurrenceRule? Recurrence { get; init; }
    /// <summary>The first occurrence the rule produces, for the preview.</summary>
    public DateTime? FirstOccurrence { get; init; }
    public IReadOnlyList<CaptureReminder> Reminders { get; init; } = [];
    public IReadOnlyList<CaptureTag> Tags { get; init; } = [];
    public string? ContainerId { get; init; }
    /// <summary>More than one list or project matched the name: the user picks one.</summary>
    public IReadOnlyList<KnownContainer> ContainerCandidates { get; init; } = [];
    public string? WaitingOn { get; init; }
    public DateOnly? FollowUpDate { get; init; }
    public TimeOnly? FollowUpTime { get; init; }
}

public sealed record QuickAddResult(
    string Source,
    QuickAddDraft Draft,
    IReadOnlyList<CaptureToken> Tokens,
    IReadOnlyList<CaptureWarning> Warnings,
    IReadOnlyList<string> Languages,
    bool NeedsTitle);
