using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Blobs;

/// <summary>Connection settings for an S3-compatible object store (<c>Blob:S3</c> section, <c>Blob__S3__*</c> in the container host).</summary>
public sealed class S3Options
{
    /// <summary>The store's endpoint, for example Less3 at <c>http://localhost:8000</c>. When unset the folder store is used.</summary>
    public string? ServiceUrl { get; set; }

    public string Bucket { get; set; } = "default";

    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>The signing region. Less3 answers to <c>us-west-1</c> unless its <c>RegionString</c> says otherwise.</summary>
    public string Region { get; set; } = "us-west-1";

    /// <summary>Path-style addressing (<c>host/bucket/key</c>); self-hosted stores have no wildcard DNS for virtual-host style.</summary>
    public bool ForcePathStyle { get; set; } = true;

    /// <summary>A key prefix inside the bucket, so one bucket can serve several deployments. Empty or ending in a slash.</summary>
    public string Prefix { get; set; } = "";
}

/// <summary>
/// An object store on an S3-compatible bucket (Less3 in development, any S3 API in production). Keys are the service's own
/// slash-separated names under <see cref="S3Options.Prefix"/>. A PUT is atomic on S3, so an object is never visible half written.
/// </summary>
public sealed class S3BlobStore : IBlobStore, IDisposable
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;

    public S3BlobStore(IOptions<BlobOptions> options)
    {
        var s3 = options.Value.S3;
        _bucket = s3.Bucket;
        _prefix = s3.Prefix.Length == 0 || s3.Prefix.EndsWith('/') ? s3.Prefix : s3.Prefix + "/";
        var config = new AmazonS3Config
        {
            ServiceURL = s3.ServiceUrl ?? throw new InvalidOperationException("Blob:S3:ServiceUrl is not configured."),
            AuthenticationRegion = s3.Region,
            ForcePathStyle = s3.ForcePathStyle,
            // S3-compatible stores generally do not understand the SDK's default trailing CRC32 and aws-chunked bodies.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        _client = new AmazonS3Client(new BasicAWSCredentials(s3.AccessKey ?? "", s3.SecretKey ?? ""), config);
    }

    public void Dispose() => _client.Dispose();

    private string KeyOf(string key)
    {
        foreach (var part in key.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..")
            {
                throw new ArgumentException("Not a blob key.", nameof(key));
            }
        }

        return _prefix + key;
    }

    private static bool IsMissing(AmazonS3Exception error) => error.StatusCode == HttpStatusCode.NotFound;

    public async Task PutAsync(string key, Stream content, CancellationToken ct)
    {
        // A request body must have a known length; the service only passes files and memory, but never assume.
        Stream body = content;
        MemoryStream? copy = null;
        if (!content.CanSeek)
        {
            copy = new MemoryStream();
            await content.CopyToAsync(copy, ct);
            copy.Position = 0;
            body = copy;
        }

        using (copy)
        {
            await _client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucket,
                Key = KeyOf(key),
                InputStream = body,
                AutoCloseStream = false,
                AutoResetStreamPosition = false,
            }, ct);
        }
    }

    public async Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        try
        {
            await _client.GetObjectMetadataAsync(_bucket, KeyOf(key), ct);
            return true;
        }
        catch (AmazonS3Exception error) when (IsMissing(error))
        {
            return false;
        }
    }

    public async Task<Stream?> OpenReadAsync(string key, long offset, CancellationToken ct)
    {
        var request = new GetObjectRequest { BucketName = _bucket, Key = KeyOf(key) };
        if (offset > 0)
        {
            request.ByteRange = new ByteRange($"bytes={offset}-");
        }

        try
        {
            return new ResponseStream(await _client.GetObjectAsync(request, ct));
        }
        catch (AmazonS3Exception error) when (IsMissing(error))
        {
            return null;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The offset is at or past the end: an empty read, as a file stream positioned at the end gives.
            return new MemoryStream([], writable: false);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        // S3 reports success for a key that is not there.
        await _client.DeleteObjectAsync(_bucket, KeyOf(key), ct);
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = KeyOf(prefix) };
        ListObjectsV2Response page;
        do
        {
            page = await _client.ListObjectsV2Async(request, ct);
            // One request per key keeps this working on stores that lack multi-object delete.
            foreach (var item in page.S3Objects ?? [])
            {
                await _client.DeleteObjectAsync(_bucket, item.Key, ct);
            }

            request.ContinuationToken = page.NextContinuationToken;
        }
        while (page.IsTruncated == true);
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        var key = "health/" + Guid.NewGuid().ToString("N");
        await PutAsync(key, new MemoryStream([1]), ct);
        try
        {
            await using var read = await OpenReadAsync(key, 0, ct) ?? throw new IOException("The blob store did not return what was written.");
        }
        finally
        {
            await DeleteAsync(key, ct);
        }
    }

    /// <summary>Hands the object's bytes on and releases the HTTP response with them.</summary>
    private sealed class ResponseStream(GetObjectResponse response) : Stream
    {
        private readonly Stream _inner = response.ResponseStream;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
