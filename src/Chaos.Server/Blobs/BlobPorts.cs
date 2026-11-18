namespace Chaos.Server.Blobs;

/// <summary>
/// Where encrypted blob bytes and upload parts live (PLAN2 §8.5). Keys are opaque, slash-separated names the service
/// builds; a provider maps them onto files or onto an S3-compatible bucket's PUT, ranged GET, HEAD, DELETE and prefix delete.
/// </summary>
public interface IBlobStore
{
    /// <summary>Stores the stream under <paramref name="key"/>, replacing any earlier object; the object is never visible half written.</summary>
    Task PutAsync(string key, Stream content, CancellationToken ct);

    Task<bool> ExistsAsync(string key, CancellationToken ct);

    /// <summary>Opens the object from <paramref name="offset"/>, or returns null when it is missing.</summary>
    Task<Stream?> OpenReadAsync(string key, long offset, CancellationToken ct);

    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>Deletes every object whose key starts with <paramref name="prefix"/> (which ends in a slash).</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken ct);

    /// <summary>For the readiness check: the store can be written and read.</summary>
    Task CheckAsync(CancellationToken ct);
}

/// <summary>What a scan found. <paramref name="Signature"/> names the detection and is safe to log; it holds nothing of the file.</summary>
public sealed record ScanResult(bool Clean, string? Signature = null)
{
    public static ScanResult Ok { get; } = new(true);
}

/// <summary>Checks bytes for known malware before the service marks a blob clean (PLAN2 §9.2).</summary>
public interface IMalwareScanner
{
    Task<ScanResult> ScanAsync(Stream content, CancellationToken ct);

    /// <summary>For the readiness check: the scanner answers.</summary>
    Task CheckAsync(CancellationToken ct);
}

/// <summary>Wraps each blob's data key with the deployment's master key, which lives in the host's key vault (PLAN2 §8.5).</summary>
public interface IKeyWrapper
{
    byte[] Wrap(byte[] dataKey);

    byte[] Unwrap(byte[] wrappedKey);
}
