using System.Security.Cryptography;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Attachments;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Attachments;

/// <summary>The download path of <see cref="AttachmentTransfer"/> against a fake service: faults reach the caller, and one caller giving up does not stop a shared download.</summary>
public sealed class AttachmentTransferTests
{
    private static readonly byte[] Bytes = "the attached file"u8.ToArray();
    private static readonly string Sha = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();

    [Test]
    public async Task DisableSync_StreamTransferStateIsForgotten_LocalBytesStateSurvives()
    {
        using var host = await DataHost.StartAsync();
        await host.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO AttachmentLocalState (Sha256,State,UpdatedAtUtc,Remote) VALUES ($sha,0,'now',1);
                INSERT INTO AttachmentStreamState (StreamId,Sha256,Remote) VALUES ('account',$sha,1),('space',$sha,1);
                """;
            command.Parameters.AddWithValue("$sha", Sha);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });

        await new SyncAccountState(host.Store).DisableAsync(CancellationToken.None);

        await Assert.That(await host.CountAsync("SELECT count(*) FROM AttachmentStreamState;")).IsEqualTo(0L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM AttachmentLocalState WHERE State=0 AND Remote=0;")).IsEqualTo(1L);
    }

    private sealed class FakeTransport(Func<CancellationToken, Task<Stream>> open) : IBlobTransport
    {
        public int Opens;
        public List<string?> Streams { get; } = [];
        public Func<string?, CancellationToken, Task<Stream>>? ByStream { get; init; }
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RemoteBlob?> HeadAsync(string sha256, BlobUploadTarget target, CancellationToken ct) => Task.FromResult<RemoteBlob?>(null);
        public Task<RemoteUpload> StartUploadAsync(string sha256, long size, BlobUploadTarget target, CancellationToken ct) => throw new NotSupportedException();
        public Task PutPartAsync(string sha256, int index, ReadOnlyMemory<byte> bytes, BlobUploadTarget target, CancellationToken ct) => throw new NotSupportedException();
        public Task<RemoteBlob> CompleteUploadAsync(string sha256, BlobUploadTarget target, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenDownloadAsync(string sha256, long offset, string? streamId, CancellationToken ct)
        {
            Interlocked.Increment(ref Opens);
            Streams.Add(streamId);
            Opened.TrySetResult();
            return ByStream is { } byStream ? byStream(streamId, ct) : open(ct);
        }
    }

    private sealed class PerStreamUploadTransport(string refusedStream) : IBlobTransport
    {
        public List<string> Started { get; } = [];
        private readonly HashSet<string> _held = [];
        public Task<RemoteBlob?> HeadAsync(string sha256, BlobUploadTarget target, CancellationToken ct) =>
            Task.FromResult<RemoteBlob?>(_held.Contains(target.StreamId) ? new RemoteBlob("Clean", Bytes.Length) : null);
        public Task<RemoteUpload> StartUploadAsync(string sha256, long size, BlobUploadTarget target, CancellationToken ct)
        {
            Started.Add(target.StreamId);
            if (target.StreamId == refusedStream) throw new BlobTransferException(BlobFailureKind.Quota, "Full");
            _held.Add(target.StreamId);
            return Task.FromResult(new RemoteUpload("Clean", 1024, 0, []));
        }
        public Task PutPartAsync(string sha256, int index, ReadOnlyMemory<byte> bytes, BlobUploadTarget target, CancellationToken ct) => throw new InvalidOperationException();
        public Task<RemoteBlob> CompleteUploadAsync(string sha256, BlobUploadTarget target, CancellationToken ct) => throw new InvalidOperationException();
        public Task<Stream> OpenDownloadAsync(string sha256, long offset, string? streamId, CancellationToken ct) => throw new InvalidOperationException();
    }

    [Test]
    public async Task Upload_SameHashInTwoStreams_TracksSuccessAndQuotaIndependently()
    {
        using var host = await DataHost.StartAsync();
        await EnableSyncAsync(host);
        const string first = "00000000-0000-0000-0000-000000000001";
        const string second = "00000000-0000-0000-0000-000000000002";
        var account = Guid.NewGuid().ToString("D");
        await host.Files.PutAsync(new MemoryStream(Bytes), Bytes.Length + 1, Sha, CancellationToken.None);
        await host.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                UPDATE SyncState SET AccountId=$account WHERE Id=1;
                UPDATE SyncApplyState SET Applying=1 WHERE Id=1;
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc)
                VALUES ($first,$owner,2,'First','now','now'),($second,$owner,2,'Second','now','now');
                INSERT INTO SyncStreams (StreamId,Kind,SpaceId,Role,Cursor,State)
                VALUES ($first,'space',$first,2,0,'active'),($second,'space',$second,2,0,'active');
                INSERT INTO Attachments (Id,SpaceId,OwnerKind,OwnerId,FileName,OriginalFileName,MediaType,ByteSize,Sha256,CreatedAtUtc,UpdatedAtUtc)
                VALUES ($a,$first,1,$task,'a','a','text/plain',$size,$sha,'now','now'),($b,$second,1,$task,'b','b','text/plain',$size,$sha,'now','now');
                UPDATE SyncApplyState SET Applying=0 WHERE Id=1;
                """;
            command.Parameters.AddWithValue("$account", account);
            command.Parameters.AddWithValue("$first", first);
            command.Parameters.AddWithValue("$second", second);
            command.Parameters.AddWithValue("$owner", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$a", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$b", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$task", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$sha", Sha);
            command.Parameters.AddWithValue("$size", Bytes.Length);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        var transport = new PerStreamUploadTransport(second);
        using var transfer = new AttachmentTransfer(host.Store, host.Files, transport, host.Clock);

        var pass = await transfer.RunOnceAsync(CancellationToken.None);
        await Assert.That(pass.Uploaded).IsEqualTo(1);
        await Assert.That(pass.Failed).IsEqualTo(1);
        await Assert.That(await host.CountAsync($"SELECT count(*) FROM AttachmentStreamState WHERE StreamId='{first}' AND Remote=1;")).IsEqualTo(1L);
        await Assert.That(await host.CountAsync($"SELECT count(*) FROM AttachmentStreamState WHERE StreamId='{second}' AND Remote=4;")).IsEqualTo(1L);
        await Assert.That((await transfer.DescribeAsync(Sha, true, CancellationToken.None)).State).IsEqualTo(AttachmentSyncState.OverQuota);
        await transfer.RunOnceAsync(CancellationToken.None);
        await Assert.That(transport.Started.SequenceEqual(new[] { first, second })).IsTrue();
    }

    [Test]
    public async Task Download_SameHashInTwoSpaces_TriesEachStreamUntilBytesAreFound()
    {
        using var host = await DataHost.StartAsync();
        await EnableSyncAsync(host);
        const string first = "00000000-0000-0000-0000-000000000001";
        const string second = "00000000-0000-0000-0000-000000000002";
        await host.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                UPDATE SyncApplyState SET Applying = 1 WHERE Id = 1;
                INSERT INTO Spaces (Id, OwnerProfileId, Kind, Name, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($first, $owner, 2, 'First', 'now', 'now'), ($second, $owner, 2, 'Second', 'now', 'now');
                INSERT INTO Attachments (Id, SpaceId, OwnerKind, OwnerId, FileName, OriginalFileName, MediaType, ByteSize, Sha256, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($attachment1, $first, 1, $task, 'a', 'a', 'application/octet-stream', $size, $sha, 'now', 'now'),
                       ($attachment2, $second, 1, $task, 'a', 'a', 'application/octet-stream', $size, $sha, 'now', 'now');
                UPDATE SyncApplyState SET Applying = 0 WHERE Id = 1;
                """;
            command.Parameters.AddWithValue("$first", first);
            command.Parameters.AddWithValue("$second", second);
            command.Parameters.AddWithValue("$owner", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$attachment1", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$attachment2", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$task", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$sha", Sha);
            command.Parameters.AddWithValue("$size", Bytes.Length);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        var transport = new FakeTransport(_ => throw new InvalidOperationException())
        {
            ByStream = (stream, _) => stream == first
                ? throw new BlobTransferException(BlobFailureKind.NotFound, "Not uploaded yet")
                : Task.FromResult<Stream>(new MemoryStream(Bytes)),
        };
        using var transfer = new AttachmentTransfer(host.Store, host.Files, transport, host.Clock);

        await Assert.That(await transfer.EnsureAvailableAsync(Sha, Bytes.Length, CancellationToken.None)).IsTrue();
        await Assert.That(transport.Streams.SequenceEqual(new[] { first, second })).IsTrue();
        await Assert.That(await host.Files.ExistsAsync(Sha, CancellationToken.None)).IsTrue();
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
        await transport.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));

        impatient.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);

        gate.SetResult();
        await Assert.That(await second).IsTrue();
        await Assert.That(await host.Files.ExistsAsync(Sha, CancellationToken.None)).IsTrue();
        await Assert.That(Volatile.Read(ref transport.Opens)).IsEqualTo(1);
    }
}
