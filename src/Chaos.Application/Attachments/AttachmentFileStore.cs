using System.Text.RegularExpressions;

namespace Chaos.Application.Attachments;

/// <summary>A blob as the store holds it.</summary>
public sealed record BlobInfo(string Sha256, long Size, DateTimeOffset ModifiedUtc);

/// <summary>The result of storing bytes: their hash and size, and whether the store already had them.</summary>
public sealed record StoredBlob(string Sha256, long Size, bool AlreadyPresent);

/// <summary>
/// Where attachment bytes live (PLAN2 §9.1). The contract is deliberately provider-neutral so
/// the local folder can be swapped for an S3-compatible bucket (or any object store) without
/// touching commands, backup, export or purge:
/// <list type="bullet">
/// <item>A blob is addressed only by the lowercase hex SHA-256 of its bytes. No path, file name
/// or URL ever crosses this interface, so there is nothing provider-specific to leak.</item>
/// <item>Blobs are immutable. Storing identical bytes twice keeps one copy.</item>
/// <item>Every operation is async and stream-based; nothing needs a local file system. A
/// remote implementation maps these onto PUT, GET with a Range header, HEAD, DELETE and a
/// paged list.</item>
/// <item><see cref="PutAsync"/> hashes while it writes and only makes the blob visible once it
/// is complete and verified, so a reader never sees partial bytes.</item>
/// </list>
/// To add a provider: implement this interface, run <c>AttachmentFileStoreContract</c> from the
/// test project against it, register it in <c>AttachmentFileStoreFactory</c>, and move existing
/// blobs with <see cref="AttachmentStoreMigrator"/>. See docs/attachment-storage.md.
/// </summary>
public interface IAttachmentFileStore
{
    /// <summary>A short stable name of the provider, such as <c>local</c> or <c>s3</c>.</summary>
    string Provider { get; }

    /// <summary>
    /// Stores the stream's bytes. When <paramref name="expectedSha256"/> is given and the bytes
    /// hash differently, nothing is stored and a validation error is thrown. Throws a validation
    /// error when the stream holds more than <paramref name="maxBytes"/>.
    /// </summary>
    Task<StoredBlob> PutAsync(Stream content, long maxBytes, string? expectedSha256, CancellationToken ct);

    Task<bool> ExistsAsync(string sha256, CancellationToken ct);

    /// <summary>Size and time of one blob, or null when the store does not have it.</summary>
    Task<BlobInfo?> GetInfoAsync(string sha256, CancellationToken ct);

    /// <summary>Opens a blob for reading from <paramref name="offset"/>, or returns null when it is missing.</summary>
    Task<Stream?> OpenReadAsync(string sha256, long offset, CancellationToken ct);

    /// <summary>Removes a blob; removing a blob that is not there is not an error.</summary>
    Task DeleteAsync(string sha256, CancellationToken ct);

    /// <summary>Every blob in the store, in no particular order.</summary>
    IAsyncEnumerable<BlobInfo> ListAsync(CancellationToken ct);
}

/// <summary>The one place that decides whether a string is a blob address.</summary>
public static partial class AttachmentHash
{
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);

    public static string Require(string? value) =>
        IsValid(value) ? value! : throw new ArgumentException("A blob address is 64 lowercase hex characters.", nameof(value));

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Pattern();
}

/// <summary>The sizes attachments are held to locally (PLAN2 §9.1, D5).</summary>
public sealed record AttachmentLimits(long MaxFileBytes)
{
    public const long DefaultMaxFileBytes = 100L * 1024 * 1024;
    public const int UploadChunkBytes = 1024 * 1024;
    public const int MaxConcurrentUploads = 4;
    public static readonly TimeSpan UploadLifetime = TimeSpan.FromHours(1);

    public static AttachmentLimits Default { get; } = new(DefaultMaxFileBytes);
}
