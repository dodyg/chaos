namespace Chaos.Application.Abstractions;

/// <summary>Why the service refused or could not finish a blob transfer (PLAN2 §9.2). Offline and sign-in problems are <see cref="ChaosServiceException"/> instead.</summary>
public enum BlobFailureKind
{
    Other = 0,
    NotFound = 1,
    Blocked = 2,
    Quota = 3,
    TooLarge = 4,
    HashMismatch = 5,
    ScannerUnavailable = 6,
}

public sealed class BlobTransferException(BlobFailureKind kind, string message) : Exception(message)
{
    public BlobFailureKind Kind { get; } = kind;
}

/// <summary>A blob the service holds: <c>Clean</c> or <c>Blocked</c>, and its plaintext size.</summary>
public sealed record RemoteBlob(string State, long Size)
{
    public bool IsBlocked => State == "Blocked";
}

/// <summary>The answer to starting an upload: the finished blob's state (nothing to send), or the part layout and the parts the service already holds.</summary>
public sealed record RemoteUpload(string? BlobState, int PartSize, int PartCount, IReadOnlyList<int> Received);

/// <summary>The service's blob endpoints (PLAN2 §9.2): resumable parts up, ranged bytes down. Implemented by the service client.</summary>
public interface IBlobTransport
{
    /// <summary>The blob's state, or null when the account does not have it.</summary>
    Task<RemoteBlob?> HeadAsync(string sha256, CancellationToken ct);

    Task<RemoteUpload> StartUploadAsync(string sha256, long size, CancellationToken ct);

    Task PutPartAsync(string sha256, int index, ReadOnlyMemory<byte> bytes, CancellationToken ct);

    /// <summary>Asks the service to verify, scan and store. Returns the blob's state; throws <see cref="BlobTransferException"/> on a hash mismatch or quota.</summary>
    Task<RemoteBlob> CompleteUploadAsync(string sha256, CancellationToken ct);

    /// <summary>Opens the bytes from <paramref name="offset"/>. Throws <see cref="BlobTransferException"/> with <see cref="BlobFailureKind.NotFound"/> or <see cref="BlobFailureKind.Blocked"/>.</summary>
    Task<Stream> OpenDownloadAsync(string sha256, long offset, CancellationToken ct);
}

/// <summary>How an attachment's bytes stand on this device, beyond present or missing (PLAN2 §9.2).</summary>
public enum AttachmentSyncState
{
    /// <summary>Nothing to say: bytes here and on the service, or sync is off.</summary>
    None = 0,
    NotDownloaded = 1,
    Downloading = 2,
    DownloadFailed = 3,
    Blocked = 4,
    UploadPending = 5,
    Uploading = 6,
    UploadFailed = 7,
    OverQuota = 8,
}

/// <summary>The sync state with a progress percentage while moving, and the words the UI shows.</summary>
public sealed record AttachmentTransferInfo(AttachmentSyncState State, int? Percent, string? Message)
{
    public static AttachmentTransferInfo Idle { get; } = new(AttachmentSyncState.None, null, null);
}

/// <summary>What the attachment commands ask when bytes may be on the service rather than here.</summary>
public interface IAttachmentAvailability
{
    Task<AttachmentTransferInfo> DescribeAsync(string sha256, bool bytesPresent, CancellationToken ct);

    /// <summary>Makes the bytes available now when they are missing (a download that the person asked for by opening the file). Returns whether they are present afterwards.</summary>
    Task<bool> EnsureAvailableAsync(string sha256, long size, CancellationToken ct);
}
