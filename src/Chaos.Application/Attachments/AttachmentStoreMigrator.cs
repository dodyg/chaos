using Chaos.Application.Abstractions;

namespace Chaos.Application.Attachments;

/// <summary>What copying one store into another did.</summary>
public sealed record AttachmentMigrationReport(int Copied, int AlreadyThere, long BytesCopied, IReadOnlyList<string> Failed)
{
    public bool Complete => Failed.Count == 0;
}

/// <summary>
/// Moves every blob from one <see cref="IAttachmentFileStore"/> to another: local folder to an
/// S3-compatible bucket, or back. Each blob is re-hashed as it is written (so a damaged source
/// blob is refused, never copied), blobs the target has are skipped, and the source is only
/// ever read, so the move can be repeated or stopped at any point. Switching providers is then:
/// run this, check <see cref="AttachmentMigrationReport.Complete"/>, point the app at the new
/// store, and delete the old one when satisfied.
/// </summary>
public static class AttachmentStoreMigrator
{
    public static async Task<AttachmentMigrationReport> CopyAllAsync(
        IAttachmentFileStore source,
        IAttachmentFileStore target,
        long maxBytes,
        IProgress<(int Done, string Sha256)>? progress,
        CancellationToken ct)
    {
        int copied = 0, already = 0, done = 0;
        long bytes = 0;
        var failed = new List<string>();
        await foreach (var blob in source.ListAsync(ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await target.ExistsAsync(blob.Sha256, ct))
                {
                    already++;
                }
                else
                {
                    await using var stream = await source.OpenReadAsync(blob.Sha256, 0, ct);
                    if (stream is null)
                    {
                        failed.Add(blob.Sha256);
                        continue;
                    }

                    var stored = await target.PutAsync(stream, maxBytes, blob.Sha256, ct);
                    copied++;
                    bytes += stored.Size;
                }
            }
            catch (AppException)
            {
                failed.Add(blob.Sha256);
            }

            progress?.Report((++done, blob.Sha256));
        }

        return new AttachmentMigrationReport(copied, already, bytes, failed);
    }
}
