using System.Security.Cryptography;
using Chaos.Application.Abstractions;
using Chaos.Application.Attachments;

namespace Chaos.Infrastructure.Tests.Attachments;

/// <summary>
/// What every <see cref="IAttachmentFileStore"/> must do. The local folder store runs it now; an
/// S3-compatible adapter runs the same checks against a bucket, which is what makes switching
/// providers safe (see docs/attachment-storage.md). Call <see cref="RunAsync"/> with an empty store;
/// a broken rule throws with the rule's description.
/// </summary>
public static class AttachmentFileStoreContract
{
    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static async Task RunAsync(IAttachmentFileStore store)
    {
        var data = "the same bytes"u8.ToArray();
        var hash = Sha(data);

        // Put returns the address, and a second put of identical bytes keeps one copy.
        var first = await store.PutAsync(new MemoryStream(data), 1024, null, CancellationToken.None);
        var second = await store.PutAsync(new MemoryStream(data), 1024, hash, CancellationToken.None);
        Require(first.Sha256 == hash && first.Size == data.Length && !first.AlreadyPresent, "put returns the hash, size and AlreadyPresent=false");
        Require(second.AlreadyPresent && second.Sha256 == hash, "an identical put reports AlreadyPresent");
        var listed = new List<BlobInfo>();
        await foreach (var blob in store.ListAsync(CancellationToken.None))
        {
            listed.Add(blob);
        }

        Require(listed.Count == 1 && listed[0].Sha256 == hash && listed[0].Size == data.Length, "list shows exactly the one blob");

        // Reads: whole, from an offset, info, exists; a missing blob is null/false, never an exception.
        await using (var whole = await store.OpenReadAsync(hash, 0, CancellationToken.None))
        {
            Require(whole is not null && (await ReadAll(whole)).SequenceEqual(data), "a read returns the bytes");
        }

        await using (var tail = await store.OpenReadAsync(hash, 4, CancellationToken.None))
        {
            Require(tail is not null && (await ReadAll(tail)).SequenceEqual(data[4..]), "a read can start at an offset");
        }

        Require((await store.GetInfoAsync(hash, CancellationToken.None))?.Size == data.Length, "info reports the size");
        Require(await store.ExistsAsync(hash, CancellationToken.None), "exists is true for a stored blob");
        var absent = Sha("never stored"u8.ToArray());
        Require(!await store.ExistsAsync(absent, CancellationToken.None), "exists is false for a missing blob");
        Require(await store.OpenReadAsync(absent, 0, CancellationToken.None) is null, "reading a missing blob returns null");
        Require(await store.GetInfoAsync(absent, CancellationToken.None) is null, "info of a missing blob is null");

        // Addresses that are not hashes never reach the provider (no path traversal, no odd keys).
        Require(!await store.ExistsAsync("../../etc/passwd", CancellationToken.None), "a non-hash address does not exist");
        Require(await store.OpenReadAsync("../x", 0, CancellationToken.None) is null, "a non-hash address cannot be read");

        // A wrong checksum or too many bytes is refused and leaves nothing behind.
        var wrong = Sha("something else"u8.ToArray());
        await RequireRefusal(() => store.PutAsync(new MemoryStream("other bytes"u8.ToArray()), 1024, wrong, CancellationToken.None), "a hash mismatch is a validation error");
        Require(!await store.ExistsAsync(wrong, CancellationToken.None) && !await store.ExistsAsync(Sha("other bytes"u8.ToArray()), CancellationToken.None), "a hash mismatch stores nothing");
        var big = new byte[2048];
        await RequireRefusal(() => store.PutAsync(new MemoryStream(big), 1024, null, CancellationToken.None), "an oversize put is a validation error");
        Require(!await store.ExistsAsync(Sha(big), CancellationToken.None), "an oversize put stores nothing");

        // Delete is idempotent.
        await store.DeleteAsync(hash, CancellationToken.None);
        await store.DeleteAsync(hash, CancellationToken.None);
        Require(!await store.ExistsAsync(hash, CancellationToken.None), "delete removes the blob");

        // Empty files are files too.
        var empty = await store.PutAsync(new MemoryStream(), 1024, null, CancellationToken.None);
        Require(empty.Size == 0 && await store.ExistsAsync(empty.Sha256, CancellationToken.None), "an empty blob can be stored");
        await store.DeleteAsync(empty.Sha256, CancellationToken.None);
    }

    private static void Require(bool condition, string rule)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Attachment file store contract broken: " + rule);
        }
    }

    private static async Task RequireRefusal(Func<Task> action, string rule)
    {
        try
        {
            await action();
        }
        catch (AppException error) when (error.Kind == AppErrorKind.Validation)
        {
            return;
        }

        throw new InvalidOperationException("Attachment file store contract broken: " + rule);
    }

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }
}
