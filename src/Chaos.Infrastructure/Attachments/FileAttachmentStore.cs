using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Chaos.Application.Abstractions;
using Chaos.Application.Attachments;

namespace Chaos.Infrastructure.Attachments;

/// <summary>
/// The local-folder <see cref="IAttachmentFileStore"/> (PLAN2 §9.1): blobs live at
/// <c>&lt;root&gt;/&lt;first 2 hex&gt;/&lt;sha256&gt;</c>. Bytes are written to a <c>.partial</c> file under
/// <c>&lt;root&gt;/.partial</c> while they are hashed, flushed to disk, and only then renamed into
/// place, so a crash never leaves a half-written blob under a real name. The same class backs
/// the shared backup store, pointed at a different root.
/// </summary>
public sealed class FileAttachmentStore(string root) : IAttachmentFileStore
{
    private const string PartialFolder = ".partial";
    private const string PartialSuffix = ".partial";

    public string Provider => "local";

    public string Root => root;

    public async Task<StoredBlob> PutAsync(Stream content, long maxBytes, string? expectedSha256, CancellationToken ct)
    {
        if (expectedSha256 is not null && !AttachmentHash.IsValid(expectedSha256))
        {
            throw AppException.Validation("sha256", "That is not a valid file hash.");
        }

        var partialDirectory = Path.Combine(root, PartialFolder);
        Directory.CreateDirectory(partialDirectory);
        var partialPath = Path.Combine(partialDirectory, Guid.NewGuid().ToString("N") + PartialSuffix);
        string hash;
        long size = 0;
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(partialPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
            }))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > maxBytes)
                    {
                        throw AppException.Validation("file", $"This file is larger than the {maxBytes / (1024 * 1024)} MB a file may be.");
                    }

                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }

            hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (expectedSha256 is not null && !string.Equals(hash, expectedSha256, StringComparison.Ordinal))
            {
                throw AppException.Validation("file", "The file's contents do not match its checksum, so it was not stored.");
            }

            var finalPath = PathOf(hash);
            if (File.Exists(finalPath))
            {
                return new StoredBlob(hash, size, AlreadyPresent: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            try
            {
                File.Move(partialPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Another writer stored the same bytes first; identical content, so that copy is the one.
                return new StoredBlob(hash, size, AlreadyPresent: true);
            }

            return new StoredBlob(hash, size, AlreadyPresent: false);
        }
        catch (IOException error) when (Chaos.Infrastructure.DataProtection.BackupManager.IsDiskFull(error))
        {
            throw new AppException(AppErrorKind.StorageFull, "There is not enough free disk space to store this file.", innerException: error);
        }
        finally
        {
            TryDelete(partialPath);
        }
    }

    public Task<bool> ExistsAsync(string sha256, CancellationToken ct) =>
        Task.FromResult(AttachmentHash.IsValid(sha256) && File.Exists(PathOf(sha256)));

    public Task<BlobInfo?> GetInfoAsync(string sha256, CancellationToken ct)
    {
        if (!AttachmentHash.IsValid(sha256) || new FileInfo(PathOf(sha256)) is not { Exists: true } info)
        {
            return Task.FromResult<BlobInfo?>(null);
        }

        return Task.FromResult<BlobInfo?>(new BlobInfo(sha256, info.Length, info.LastWriteTimeUtc));
    }

    public Task<Stream?> OpenReadAsync(string sha256, long offset, CancellationToken ct)
    {
        if (!AttachmentHash.IsValid(sha256))
        {
            return Task.FromResult<Stream?>(null);
        }

        try
        {
            var stream = new FileStream(PathOf(sha256), new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            });
            if (offset > 0)
            {
                stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
            }

            return Task.FromResult<Stream?>(stream);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
        catch (DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    public Task DeleteAsync(string sha256, CancellationToken ct)
    {
        if (AttachmentHash.IsValid(sha256))
        {
            TryDelete(PathOf(sha256));
        }

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<BlobInfo> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            ct.ThrowIfCancellationRequested();
            var prefix = Path.GetFileName(directory);
            if (prefix.Length != 2)
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (AttachmentHash.IsValid(name) && name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var info = new FileInfo(file);
                    yield return new BlobInfo(name, info.Length, info.LastWriteTimeUtc);
                }
            }
        }

        await Task.CompletedTask;
    }

    private string PathOf(string sha256) => Path.Combine(root, sha256[..2], sha256);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Chaos could not delete {0}: {1}", path, error.Message);
        }
    }
}
