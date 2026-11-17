namespace Chaos.Application.Contracts;

/// <summary>What an attachment hangs off. The numbers are stored (migration 031).</summary>
public enum AttachmentOwnerKind
{
    Task = 1,
    Diary = 2,
    Pad = 3,
    Page = 4,
}

/// <summary>
/// One attached file (PLAN2 §9.1). The bytes live in the attachment file store under
/// <see cref="Sha256"/>; this row is the metadata, which syncs. <see cref="FileName"/> is the
/// sanitized display name and <see cref="OriginalFileName"/> what the person's file was called.
/// </summary>
public sealed record AttachmentRecord : AggregateRecord
{
    public required string SpaceId { get; init; }
    public required AttachmentOwnerKind OwnerKind { get; init; }
    public required string OwnerId { get; init; }
    public required string FileName { get; init; }
    public required string OriginalFileName { get; init; }
    public required string MediaType { get; init; }
    public required long ByteSize { get; init; }
    public required string Sha256 { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public long? DurationMs { get; init; }
}

/// <summary>Whether this device holds the bytes of a hash. Device-local: it differs per computer.</summary>
public enum AttachmentBytesState
{
    Present = 0,
    Missing = 1,
    Downloading = 2,
    Failed = 3,
}

public sealed record AttachmentLocalStateRecord(string Sha256, AttachmentBytesState State, string? PinnedReason, DateTimeOffset UpdatedAtUtc);
