using Chaos.Application.Attachments;

namespace Chaos.Infrastructure.Attachments;

/// <summary>Thumbnails cached as JPEG files in one folder, written through a temp file so a reader never sees half of one.</summary>
public sealed class DirectoryThumbnailCache(string directory) : IThumbnailCache
{
    private string PathFor(string sha256, int side) => Path.Combine(directory, $"{sha256}-{side}.jpg");

    public async Task<byte[]?> TryGetAsync(string sha256, int side, CancellationToken ct)
    {
        var path = PathFor(sha256, side);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(path, ct);
        }
        catch (IOException error)
        {
            // Treated as a cache miss: the thumbnail is rebuilt and the cache file rewritten.
            System.Diagnostics.Trace.TraceWarning("Chaos could not read a cached thumbnail: {0}", error.GetType().Name);
            return null;
        }
    }

    public async Task PutAsync(string sha256, int side, byte[] bytes, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(sha256, side);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            await File.WriteAllBytesAsync(temp, bytes, ct);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Chaos could not cache a thumbnail: {0}", error.Message);
        }
    }
}
