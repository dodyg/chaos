namespace Chaos.Application.Contracts;

/// <summary>
/// One standup (PLAN3 9.12): a member's Markdown post in a shared space for a day. The author alone
/// edits it; the author or an owner deletes it. A deleted row is a tombstone, so every member's
/// device drops it from the Overview as well.
/// </summary>
public sealed record SpaceUpdateRecord : AggregateRecord
{
    public required string SpaceId { get; init; }
    /// <summary>The author's member ID (their canonical profile ID), as the service checks it.</summary>
    public required string AuthorMemberId { get; init; }
    /// <summary>The local day the standup is about (the day its daily summary ended with).</summary>
    public required DateOnly ForDate { get; init; }
    public required string RawMarkdown { get; init; }
    public DateTimeOffset? EditedAtUtc { get; init; }
}

/// <summary>A standup as the Overview shows it: the row, the author's name in the space, and whether the local person wrote it.</summary>
public sealed record SpaceUpdateItem(SpaceUpdateRecord Update, string AuthorName, bool IsMine);
