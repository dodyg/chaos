using System.Security.Cryptography;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Attachments;
using Chaos.Infrastructure.Tests.DataProtection;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Attachments;

/// <summary>The download path of <see cref="AttachmentTransfer"/> against a fake service: faults reach the caller, and one caller giving up does not stop a shared download.</summary>
public sealed class AttachmentTransferTests
{
    private static readonly byte[] Bytes = "the attached file"u8.ToArray();
    private static readonly string Sha = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();

    private sealed class FakeTransport(Func<CancellationToken, Task<Stream>> open) : IBlobTransport
    {
        public int Opens;
        public Task<RemoteBlob?> HeadAsync(string sha256, CancellationToken ct) => Task.FromResult<RemoteBlob?>(null);
        public Task<RemoteUpload> StartUploadAsync(string sha256, long size, CancellationToken ct) => throw new NotSupportedException();
        public Task PutPartAsync(string sha256, int index, ReadOnlyMemory<byte> bytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<RemoteBlob> CompleteUploadAsync(string sha256, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenDownloadAsync(string sha256, long offset, CancellationToken ct) { Interlocked.Increment(ref Opens); return open(ct); }
    }

    [Test]
    public async Task An_offline_service_reaches_the_caller_as_an_offline_error_not_a_cancellation()
    {
        using var host = await DataHost.StartAsync();
        await EnableSyncAsync(host);
        var transport = new FakeTransport(_ => throw new ChaosServiceException(ServiceFailure.Offline, "no network"));
        using var transfer = new AttachmentTransfer(host.Store, host.Files, transport, host.Clock);

        var error = await Assert.ThrowsAsync<ChaosServiceException>(() => transfer.EnsureAvailableAsync(Sha, Bytes.Length, CancellationToken.None));
        await Assert.That(error!.Kind).IsEqualTo(ServiceFailure.Offline);
    }

    [Test]
    public async Task A_caller_that_cancels_stops_waiting_but_the_shared_download_still_finishes_for_the_other()
    {
        using var host = await DataHost.StartAsync();
        await EnableSyncAsync(host);
        var gate = new TaskCompletionSource();
        var transport = new FakeTransport(async _ => { await gate.Task; return new MemoryStream(Bytes); });
        using var transfer = new AttachmentTransfer(host.Store, host.Files, transport, host.Clock);

        using var impatient = new CancellationTokenSource();
        var first = transfer.EnsureAvailableAsync(Sha, Bytes.Length, impatient.Token);
        var second = transfer.EnsureAvailableAsync(Sha, Bytes.Length, CancellationToken.None);
        while (Volatile.Read(ref transport.Opens) == 0)
        {
            await Task.Delay(10);
        }

        impatient.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);

        gate.SetResult();
        await Assert.That(await second).IsTrue();
        await Assert.That(await host.Files.ExistsAsync(Sha, CancellationToken.None)).IsTrue();
        await Assert.That(Volatile.Read(ref transport.Opens)).IsEqualTo(1);
    }
}
