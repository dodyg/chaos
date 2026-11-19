using Chaos.Application.Attachments;

namespace Chaos.Infrastructure.Attachments;

/// <summary>Upload chunks staged as files in one folder. Sessions do not survive a restart, so what an earlier run left behind is cleared on start.</summary>
public sealed class DirectoryUploadStaging : IUploadStaging
{
    private readonly string _directory;

    public DirectoryUploadStaging(string directory)
    {
        _directory = directory;
        try
        {
            if (Directory.Exists(directory))
            {
                foreach (var stale in Directory.EnumerateFiles(directory))
                {
                    File.Delete(stale);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Chaos could not clear old uploads: {0}", error.Message);
        }
    }

    public async Task<string> CreateAsync(string uploadId, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, uploadId + ".part");
        await using (File.Create(path))
        {
        }

        return path;
    }

    public async Task AppendAsync(string handle, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        await using var output = new FileStream(handle, FileMode.Append, FileAccess.Write, FileShare.None);
        await output.WriteAsync(bytes, ct);
    }

    public Task<Stream> OpenReadAsync(string handle, CancellationToken ct) =>
        Task.FromResult<Stream>(new FileStream(handle, FileMode.Open, FileAccess.Read, FileShare.Read));

    public void Discard(string handle)
    {
        try
        {
            File.Delete(handle);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.TraceWarning("Chaos could not delete an upload: {0}", error.Message);
        }
    }
}
