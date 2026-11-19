using System.Security.Cryptography;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Attachments;
using Chaos.Application.Containers;
using Chaos.Application.Tasks;
using Chaos.Server.Data;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Attachments;
using Chaos.Server.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chaos.Server.Tests.Sync;

/// <summary>Counts what a device sends and can lose the connection part way through an upload.</summary>
internal sealed class CountingTransport(IBlobTransport inner) : IBlobTransport
{
    public int PartsSent;
    public int Downloads;

    /// <summary>The upload loses its connection when this many parts have gone (then the setting resets).</summary>
    public int? DropAfterParts { get; set; }

    public Task<RemoteBlob?> HeadAsync(string sha256, CancellationToken ct) => inner.HeadAsync(sha256, ct);

    public Task<RemoteUpload> StartUploadAsync(string sha256, long size, CancellationToken ct) => inner.StartUploadAsync(sha256, size, ct);

    public async Task PutPartAsync(string sha256, int index, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        if (DropAfterParts is { } limit && PartsSent >= limit)
        {
            DropAfterParts = null;
            throw new ChaosServiceException(ServiceFailure.Offline, "The connection was lost.");
        }

        await inner.PutPartAsync(sha256, index, bytes, ct);
        PartsSent++;
    }

    public Task<RemoteBlob> CompleteUploadAsync(string sha256, CancellationToken ct) => inner.CompleteUploadAsync(sha256, ct);

    public Task<Stream> OpenDownloadAsync(string sha256, long offset, CancellationToken ct)
    {
        Downloads++;
        return inner.OpenDownloadAsync(sha256, offset, ct);
    }
}

/// <summary>PLAN2 P16 end to end: two real desktops, the in-process service on real PostgreSQL, real encrypted blobs.</summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class BlobSyncTests(PostgresServer postgres)
{
    private static string Op() => Guid.NewGuid().ToString();

    private sealed class Mover
    {
        public required SyncDevice Device { get; init; }
        public required AttachmentCommands Commands { get; init; }
        public required IAttachmentFileStore Files { get; init; }
        public required AttachmentTransfer Transfer { get; init; }
        public required CountingTransport Transport { get; init; }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required SyncWorld World { get; init; }
        public required FakeScanner Scanner { get; init; }
        public required string Directory { get; init; }

        public async ValueTask DisposeAsync()
        {
            await World.DisposeAsync();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException error)
            {
                System.Diagnostics.Trace.TraceWarning("Could not remove temporary blob sync test directory: {0}", error.GetType().Name);
            }
        }
    }

    private async Task<Rig> StartAsync(long? quota = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "chaos-blob-sync-tests", Guid.NewGuid().ToString("N"));
        var scanner = new FakeScanner();
        var settings = new Dictionary<string, string?>
        {
            ["Blob:Directory"] = Path.Combine(directory, "objects"),
            ["KeyVault:MasterKeyPath"] = Path.Combine(directory, "master.key"),
        };
        if (quota is { } q)
        {
            settings["Limits:AccountQuotaBytes"] = q.ToString();
        }

        var world = await SyncWorld.StartAsync(postgres, settings, services =>
        {
            services.RemoveAll<IMalwareScanner>();
            services.AddSingleton<IMalwareScanner>(scanner);
        });
        return new Rig { World = world, Scanner = scanner, Directory = directory };
    }

    private static Mover MoverFor(SyncDevice device)
    {
        var files = AttachmentFileStoreFactory.Create(device.Storage.Options);
        var transport = new CountingTransport(device.Client);
        var transfer = new AttachmentTransfer(device.Store, files, transport, device.Clock);
        var commands = new AttachmentCommands(device.Store, files, device.Ids, device.Clock, new Chaos.Infrastructure.Attachments.DirectoryUploadStaging(device.Storage.Options.UploadsDirectory), availability: transfer);
        return new Mover { Device = device, Commands = commands, Files = files, Transfer = transfer, Transport = transport };
    }

    private static async Task<AttachmentRecord> AttachAsync(Mover mover, string taskId, string name, byte[] bytes)
    {
        var begun = await mover.Commands.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Task, taskId, name, bytes.Length));
        for (var index = 0; index * begun.ChunkBytes < bytes.Length; index++)
        {
            var slice = bytes.AsMemory(index * begun.ChunkBytes, Math.Min(begun.ChunkBytes, bytes.Length - (index * begun.ChunkBytes))).ToArray();
            await mover.Commands.ChunkAsync(begun.UploadId, index, slice);
        }

        return (await mover.Commands.CommitAsync(new CommitAttachmentCommand(Op(), begun.UploadId))).Attachment;
    }

    private static async Task<string> NewTaskAsync(SyncDevice device, string title, string containerId = "") =>
        (await device.Tasks.CaptureAsync(new CaptureTaskCommand(Op(), containerId, title, null))).Task.Id;

    /// <summary>One device sends what it has, the world syncs, another device then takes what it should.</summary>
    private static async Task PassAsync(SyncWorld world, params Mover[] movers)
    {
        foreach (var mover in movers)
        {
            await world.SettleAsync();
            await mover.Transfer.RunOnceAsync(CancellationToken.None);
        }

        await world.SettleAsync();
    }

    private static byte[] Random(int size) => RandomNumberGenerator.GetBytes(size);

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Test]
    public async Task A_small_file_attached_on_one_device_appears_on_the_other()
    {
        await using var rig = await StartAsync();
        var a = MoverFor(await rig.World.AddDeviceAsync());
        var b = MoverFor(await rig.World.AddDeviceAsync());
        var bytes = Random(40_000);
        var taskId = await NewTaskAsync(a.Device, "Holiday");
        var record = await AttachAsync(a, taskId, "shot.bin", bytes);

        await PassAsync(rig.World, a, b);

        await Assert.That(await b.Files.ExistsAsync(record.Sha256, CancellationToken.None)).IsTrue();
        var seen = await b.Commands.GetAsync(record.Id);
        await Assert.That(seen.Bytes).IsEqualTo(AttachmentBytesState.Present);
        await using var read = await b.Files.OpenReadAsync(record.Sha256, 0, CancellationToken.None);
        using var copy = new MemoryStream();
        await read!.CopyToAsync(copy);
        await Assert.That(copy.ToArray()).IsEquivalentTo(bytes);

        // The sender knows it is on the service; nothing is left to send.
        await Assert.That((await a.Commands.GetAsync(record.Id)).Transfer!.State).IsEqualTo(AttachmentSyncState.None);
        await Assert.That((await a.Transfer.RunOnceAsync(CancellationToken.None)).Uploaded).IsEqualTo(0);
    }

    [Test]
    public async Task A_large_file_comes_only_when_it_is_opened_and_reports_what_it_is_waiting_for()
    {
        await using var rig = await StartAsync();
        var a = MoverFor(await rig.World.AddDeviceAsync());
        var b = MoverFor(await rig.World.AddDeviceAsync());
        var bytes = Random((6 * 1024 * 1024) + 321); // above the 5 MiB automatic limit
        var taskId = await NewTaskAsync(a.Device, "Video");
        var record = await AttachAsync(a, taskId, "clip.bin", bytes);

        await PassAsync(rig.World, a, b);

        await Assert.That(await b.Files.ExistsAsync(record.Sha256, CancellationToken.None)).IsFalse();
        await Assert.That(b.Transport.Downloads).IsEqualTo(0);
        var waiting = await b.Commands.GetAsync(record.Id);
        await Assert.That(waiting.Bytes).IsEqualTo(AttachmentBytesState.Missing);
        await Assert.That(waiting.Transfer!.State).IsEqualTo(AttachmentSyncState.NotDownloaded);
        await Assert.That(waiting.Transfer.Message).IsEqualTo("Not downloaded yet");

        // Reading the first slice opens the file, which fetches it.
        var first = await b.Commands.ReadAsync(record.Id, 0, 1024);
        await Assert.That(first.Bytes).IsEquivalentTo(bytes[..1024]);
        await Assert.That(b.Transport.Downloads).IsEqualTo(1);
        await Assert.That((await b.Commands.GetAsync(record.Id)).Bytes).IsEqualTo(AttachmentBytesState.Present);
        var tail = await b.Commands.ReadAsync(record.Id, bytes.Length - 100, 100);
        await Assert.That(tail.Bytes).IsEquivalentTo(bytes[^100..]);
    }

    [Test]
    public async Task A_list_kept_offline_prefetches_its_large_files_without_opening_them()
    {
        await using var rig = await StartAsync();
        var a = MoverFor(await rig.World.AddDeviceAsync());
        var b = MoverFor(await rig.World.AddDeviceAsync());
        var list = (await new ContainerCommands(a.Device.Store, a.Device.Ids, a.Device.Clock).CreateAsync(new CreateContainerCommand(Op(), "Trip", ContainerKind.List, null))).Container;
        var taskId = await NewTaskAsync(a.Device, "Book flights", list.Id);
        var bytes = Random((5 * 1024 * 1024) + 99);
        var record = await AttachAsync(a, taskId, "itinerary.bin", bytes);
        await PassAsync(rig.World, a, b);
        await Assert.That(await b.Files.ExistsAsync(record.Sha256, CancellationToken.None)).IsFalse();

        await b.Transfer.SetContainerOfflineAsync(list.Id, true, CancellationToken.None);
        await Assert.That(await b.Transfer.OfflineContainersAsync(CancellationToken.None)).IsEquivalentTo(new[] { list.Id });
        await b.Transfer.RunOnceAsync(CancellationToken.None);
        await Assert.That(await b.Files.ExistsAsync(record.Sha256, CancellationToken.None)).IsTrue();

        // Turning it off keeps what came, and a new large file no longer follows.
        await b.Transfer.SetContainerOfflineAsync(list.Id, false, CancellationToken.None);
        var more = await AttachAsync(a, taskId, "second.bin", Random((5 * 1024 * 1024) + 7));
        await PassAsync(rig.World, a, b);
        await b.Transfer.RunOnceAsync(CancellationToken.None);
        await Assert.That(await b.Files.ExistsAsync(more.Sha256, CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task A_file_the_malware_check_blocks_shows_as_blocked_on_both_devices_and_is_never_downloaded()
    {
        await using var rig = await StartAsync();
        var a = MoverFor(await rig.World.AddDeviceAsync());
        var b = MoverFor(await rig.World.AddDeviceAsync());
        var bytes = Encoding.ASCII.GetBytes("EICAR test file, harmless");
        var taskId = await NewTaskAsync(a.Device, "Suspicious");
        var record = await AttachAsync(a, taskId, "eicar.txt", bytes);

        await PassAsync(rig.World, a, b);

        var onA = await a.Commands.GetAsync(record.Id);
        await Assert.That(onA.Transfer!.State).IsEqualTo(AttachmentSyncState.Blocked);
        await Assert.That(onA.Transfer.Message).IsEqualTo("Blocked by malware check");
        await Assert.That(await a.Device.CountAsync("SELECT count(*) FROM SyncAttention WHERE Category = 'attachment.blocked' AND ResolvedAtUtc IS NULL;")).IsEqualTo(1L);

        var onB = await b.Commands.GetAsync(record.Id);
        await Assert.That(onB.Transfer!.State).IsEqualTo(AttachmentSyncState.Blocked);
        await Assert.That(await b.Files.ExistsAsync(record.Sha256, CancellationToken.None)).IsFalse();
        await Assert.That(async () => await b.Commands.ReadAsync(record.Id, 0, 10)).Throws<AppException>();

        // The next pass does not ask the service for it again.
        var before = b.Transport.Downloads;
        await b.Transfer.RunOnceAsync(CancellationToken.None);
        await Assert.That(b.Transport.Downloads).IsEqualTo(before);
    }

    [Test]
    public async Task An_interrupted_upload_resumes_without_resending_the_parts_that_arrived()
    {
        await using var rig = await StartAsync();
        var a = MoverFor(await rig.World.AddDeviceAsync());
        var b = MoverFor(await rig.World.AddDeviceAsync());
        var bytes = Random((4 * 1024 * 1024) - 17); // four parts
        var taskId = await NewTaskAsync(a.Device, "Archive");
        var record = await AttachAsync(a, taskId, "archive.bin", bytes);

        a.Transport.DropAfterParts = 2;
        await rig.World.SettleAsync();
        var first = await a.Transfer.RunOnceAsync(CancellationToken.None);
        await Assert.That(first.Uploaded).IsEqualTo(0);
        await Assert.That(a.Transport.PartsSent).IsEqualTo(2);
        await Assert.That((await a.Commands.GetAsync(record.Id)).Transfer!.State).IsEqualTo(AttachmentSyncState.UploadPending);

        var second = await a.Transfer.RunOnceAsync(CancellationToken.None);
        await Assert.That(second.Uploaded).IsEqualTo(1);
        await Assert.That(a.Transport.PartsSent).IsEqualTo(4);

        await using var db = ServerDatabase.Open(rig.World.Server.ConnectionString);
        await Assert.That(await db.Blobs.CountAsync(x => x.Sha256 == Sha(bytes) && x.State == BlobStates.Clean)).IsEqualTo(1);
        await b.Commands.GetAsync(record.Id);
    }

    [Test]
    public async Task An_upload_over_quota_keeps_the_file_here_and_raises_an_attention_item()
    {
        await using var rig = await StartAsync(quota: 1000);
        var a = MoverFor(await rig.World.AddDeviceAsync());
        var bytes = Random(5000);
        var taskId = await NewTaskAsync(a.Device, "Too big");
        var record = await AttachAsync(a, taskId, "big.bin", bytes);

        await rig.World.SettleAsync();
        await a.Transfer.RunOnceAsync(CancellationToken.None);

        await Assert.That(await a.Files.ExistsAsync(record.Sha256, CancellationToken.None)).IsTrue();
        var view = await a.Commands.GetAsync(record.Id);
        await Assert.That(view.Bytes).IsEqualTo(AttachmentBytesState.Present);
        await Assert.That(view.Transfer!.State).IsEqualTo(AttachmentSyncState.OverQuota);
        await Assert.That(await a.Device.CountAsync("SELECT count(*) FROM SyncAttention WHERE Category = 'attachment.quota' AND ResolvedAtUtc IS NULL;")).IsEqualTo(1L);

        // It backs off instead of asking on every pass.
        var parts = a.Transport.PartsSent;
        await a.Transfer.RunOnceAsync(CancellationToken.None);
        await Assert.That(a.Transport.PartsSent).IsEqualTo(parts);
    }

    [Test]
    public async Task A_computer_that_has_not_joined_an_account_never_calls_the_service()
    {
        await using var rig = await StartAsync();
        var a = MoverFor(await rig.World.AddDeviceAsync(joined: false));
        var taskId = await NewTaskAsync(a.Device, "Local only");
        var record = await AttachAsync(a, taskId, "note.bin", Random(1000));

        var result = await a.Transfer.RunOnceAsync(CancellationToken.None);
        await Assert.That(result).IsEqualTo(new AttachmentTransferResult(0, 0, 0));
        await Assert.That((await a.Commands.GetAsync(record.Id)).Transfer).IsEqualTo(AttachmentTransferInfo.Idle);
        await using var db = ServerDatabase.Open(rig.World.Server.ConnectionString);
        await Assert.That(await db.Blobs.CountAsync()).IsEqualTo(0);
    }
}
