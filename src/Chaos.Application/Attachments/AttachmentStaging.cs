namespace Chaos.Application.Attachments;

/// <summary>Where the chunks of an upload session wait until the file is committed. The handle is opaque to callers.</summary>
public interface IUploadStaging
{
    /// <summary>Makes an empty staging area for one upload and returns its handle.</summary>
    Task<string> CreateAsync(string uploadId, CancellationToken ct);

    Task AppendAsync(string handle, ReadOnlyMemory<byte> bytes, CancellationToken ct);

    Task<Stream> OpenReadAsync(string handle, CancellationToken ct);

    /// <summary>Removes the staged bytes; a failure is not an error (it is only leftover temp data).</summary>
    void Discard(string handle);
}

/// <summary>A cache of generated thumbnails by file hash and size. A miss or a failure is never an error: the thumbnail is rebuilt.</summary>
public interface IThumbnailCache
{
    Task<byte[]?> TryGetAsync(string sha256, int side, CancellationToken ct);

    Task PutAsync(string sha256, int side, byte[] bytes, CancellationToken ct);
}
