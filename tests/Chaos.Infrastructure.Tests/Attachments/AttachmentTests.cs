using System.IO.Compression;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Attachments;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Pad;
using Chaos.Application.Pages;
using Chaos.Application.Tasks;
using Chaos.Infrastructure.Attachments;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Infrastructure.Transfer;

namespace Chaos.Infrastructure.Tests.Attachments;

/// <summary>PLAN2 P9: attachment storage, upload sessions, reference counting, backup, restore and archive round trips.</summary>
public sealed class AttachmentTests
{
    private static async Task<string> NewTaskAsync(DataHost host, string title = "Owner")
    {
        var inbox = await host.Store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token);
            var space = await session.Spaces.GetPersonalByOwnerAsync(profile!.Id, token);
            return (await session.Containers.GetInboxAsync(space!.Id, token))!.Id;
        });
        return (await host.Tasks.CaptureAsync(new CaptureTaskCommand(DataHost.Op(), inbox, title, null))).Task.Id;
    }

    private static async Task<AttachmentRecord> AddAsync(DataHost host, string ownerId, string name, byte[] bytes, AttachmentOwnerKind kind = AttachmentOwnerKind.Task, string? operationId = null)
    {
        var upload = await host.Attachments.BeginAsync(new BeginAttachmentUploadCommand(kind, ownerId, name, bytes.Length));
        var index = 0;
        for (var offset = 0; offset < bytes.Length; offset += upload.ChunkBytes)
        {
            await host.Attachments.ChunkAsync(upload.UploadId, index++, bytes[offset..Math.Min(bytes.Length, offset + upload.ChunkBytes)]);
        }

        return (await host.Attachments.CommitAsync(new CommitAttachmentCommand(operationId ?? DataHost.Op(), upload.UploadId))).Attachment;
    }

    private static byte[] PngBytes(int width, int height, byte fill = 0)
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        bytes[40] = fill;
        return bytes;
    }

    private static async Task<List<string>> BlobsAsync(IAttachmentFileStore store)
    {
        var all = new List<string>();
        await foreach (var blob in store.ListAsync(CancellationToken.None))
        {
            all.Add(blob.Sha256);
        }

        return all;
    }

    // ---- Storage contract and provider switching ----------------------------------------------

    [Test]
    public async Task FileAttachmentStore_MeetsTheProviderContract()
    {
        var store = new FileAttachmentStore(Path.Combine(TestPaths.NewDataRoot(), "blobs"));
        await AttachmentFileStoreContract.RunAsync(store);
        await Assert.That(store.Provider).IsEqualTo("local");
    }

    [Test]
    public async Task FileAttachmentStore_WritesAtomically_NoPartialFileStaysAndBlobsSitUnderTheirPrefix()
    {
        var root = Path.Combine(TestPaths.NewDataRoot(), "blobs");
        var store = new FileAttachmentStore(root);
        var stored = await store.PutAsync(new MemoryStream("abc"u8.ToArray()), 100, null, CancellationToken.None);

        await Assert.That(File.Exists(Path.Combine(root, stored.Sha256[..2], stored.Sha256))).IsTrue();
        await Assert.That(Directory.EnumerateFiles(Path.Combine(root, ".partial")).Any()).IsFalse();

        await Assert.That(async () => await store.PutAsync(new MemoryStream(new byte[300]), 100, null, CancellationToken.None)).Throws<AppException>();
        await Assert.That(Directory.EnumerateFiles(Path.Combine(root, ".partial")).Any()).IsFalse();
    }

    [Test]
    public async Task Factory_RefusesAnUnknownProviderWithAReadableError()
    {
        var options = Chaos.Infrastructure.Bootstrap.StorageOptions.FromDataRoot(TestPaths.NewDataRoot());
        await Assert.That(AttachmentFileStoreFactory.Create(options).Provider).IsEqualTo("local");
        var error = await Assert.That(() => AttachmentFileStoreFactory.Create(options, "s3")).Throws<AppException>();
        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.UnsupportedCapability);
    }

    [Test]
    public async Task Migrator_CopiesEveryBlobToAnotherStore_SkipsWhatIsThere_AndRefusesDamagedSourceBlobs()
    {
        var root = TestPaths.NewDataRoot();
        var source = new FileAttachmentStore(Path.Combine(root, "source"));
        var target = new FileAttachmentStore(Path.Combine(root, "target"));
        var a = await source.PutAsync(new MemoryStream("alpha"u8.ToArray()), 100, null, CancellationToken.None);
        var b = await source.PutAsync(new MemoryStream("bravo"u8.ToArray()), 100, null, CancellationToken.None);
        await target.PutAsync(new MemoryStream("alpha"u8.ToArray()), 100, null, CancellationToken.None);

        var report = await AttachmentStoreMigrator.CopyAllAsync(source, target, 100, null, CancellationToken.None);
        await Assert.That(report.Copied).IsEqualTo(1);
        await Assert.That(report.AlreadyThere).IsEqualTo(1);
        await Assert.That(report.Complete).IsTrue();
        await Assert.That(await target.ExistsAsync(b.Sha256, CancellationToken.None)).IsTrue();

        // A source blob whose bytes no longer match its address is not copied.
        var c = await source.PutAsync(new MemoryStream("charlie"u8.ToArray()), 100, null, CancellationToken.None);
        await File.WriteAllBytesAsync(Path.Combine(root, "source", c.Sha256[..2], c.Sha256), "damaged"u8.ToArray());
        var damaged = await AttachmentStoreMigrator.CopyAllAsync(source, target, 100, null, CancellationToken.None);
        await Assert.That(damaged.Complete).IsFalse();
        await Assert.That(damaged.Failed).Contains(c.Sha256);
        await Assert.That(await target.ExistsAsync(c.Sha256, CancellationToken.None)).IsFalse();
    }

    // ---- Upload sessions and commit ------------------------------------------------------------

    [Test]
    public async Task Commit_StoresTheBytes_SniffsTheType_ReadsImageSize_AndCountsAsAnUndoableOperation()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var operation = DataHost.Op();

        var png = await AddAsync(host, task, @"C:\temp\shot.jpg", PngBytes(640, 480), operationId: operation);

        await Assert.That(png.MediaType).IsEqualTo("image/png"); // the bytes win over ".jpg"
        await Assert.That(png.Width).IsEqualTo(640);
        await Assert.That(png.Height).IsEqualTo(480);
        await Assert.That(png.FileName).IsEqualTo("shot.jpg"); // no path parts
        await Assert.That(png.OriginalFileName).IsEqualTo(@"C:\temp\shot.jpg");
        await Assert.That(await host.Files.ExistsAsync(png.Sha256, CancellationToken.None)).IsTrue();
        var local = await host.Store.QueryAsync(async (session, token) => await session.Attachments.GetLocalStateAsync(png.Sha256, token));
        await Assert.That(local!.State).IsEqualTo(AttachmentBytesState.Present);

        var view = (await host.Attachments.ListAsync(AttachmentOwnerKind.Task, task)).Single();
        await Assert.That(view.Preview).IsEqualTo(AttachmentPreview.Image);
        await Assert.That(view.Bytes).IsEqualTo(AttachmentBytesState.Present);

        await new UndoCommands(host.Store, host.Ids, host.Clock).UndoAsync(new(DataHost.Op(), operation));
        await Assert.That(await host.Attachments.ListAsync(AttachmentOwnerKind.Task, task)).IsEmpty();
    }

    [Test]
    public async Task Commit_RepeatedWithTheSameOperation_ReturnsTheSameAttachmentOnce()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var upload = await host.Attachments.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Task, task, "a.txt", 3));
        await host.Attachments.ChunkAsync(upload.UploadId, 0, "abc"u8.ToArray());
        var operation = DataHost.Op();

        var first = await host.Attachments.CommitAsync(new CommitAttachmentCommand(operation, upload.UploadId));
        var again = await host.Attachments.CommitAsync(new CommitAttachmentCommand(operation, upload.UploadId));

        await Assert.That(again.Replayed).IsTrue();
        await Assert.That(again.Attachment.Id).IsEqualTo(first.Attachment.Id);
        await Assert.That(await host.Attachments.ListAsync(AttachmentOwnerKind.Task, task)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task IdenticalBytesAttachedTwice_AreStoredOnce_ButAreTwoAttachments()
    {
        using var host = await DataHost.StartAsync();
        var first = await NewTaskAsync(host, "One");
        var second = await NewTaskAsync(host, "Two");
        var a = await AddAsync(host, first, "a.txt", "same text"u8.ToArray());
        var b = await AddAsync(host, second, "b.txt", "same text"u8.ToArray());

        await Assert.That(a.Id).IsNotEqualTo(b.Id);
        await Assert.That(a.Sha256).IsEqualTo(b.Sha256);
        await Assert.That(await BlobsAsync(host.Files)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Upload_RefusesOutOfOrderChunks_TooMuchData_IncompleteFiles_OversizeFiles_AndAMissingOwner()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);

        var upload = await host.Attachments.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Task, task, "x.bin", 4));
        await Assert.That(async () => await host.Attachments.ChunkAsync(upload.UploadId, 1, [1, 2])).Throws<AppException>();
        await Assert.That(async () => await host.Attachments.ChunkAsync(upload.UploadId, 0, [1, 2, 3, 4, 5])).Throws<AppException>();
        await host.Attachments.ChunkAsync(upload.UploadId, 0, [1, 2]);
        await host.Attachments.ChunkAsync(upload.UploadId, 0, [1, 2]); // a retry of an arrived chunk is ignored
        await Assert.That(async () => await host.Attachments.CommitAsync(new CommitAttachmentCommand(DataHost.Op(), upload.UploadId))).Throws<AppException>();
        await host.Attachments.ChunkAsync(upload.UploadId, 1, [3, 4]);
        var done = await host.Attachments.CommitAsync(new CommitAttachmentCommand(DataHost.Op(), upload.UploadId));
        await Assert.That(done.Attachment.ByteSize).IsEqualTo(4L);

        var small = new AttachmentCommands(host.Store, host.Files, host.Ids, host.Clock, new DirectoryUploadStaging(Path.Combine(host.Root, "u2")), new AttachmentLimits(10));
        await Assert.That(async () => await small.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Task, task, "big.bin", 11))).Throws<AppException>();

        await Assert.That(async () => await host.Attachments.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Task, Guid.NewGuid().ToString(), "x", 1))).Throws<AppException>();
        await Assert.That(async () => await host.Attachments.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Page, task, "x", 1))).Throws<AppException>();
    }

    [Test]
    public async Task Upload_AllowsFourSessionsAtOnce_AndSessionsExpireAfterAnHour()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var sessions = new List<AttachmentUploadStarted>();
        for (var i = 0; i < AttachmentLimits.MaxConcurrentUploads; i++)
        {
            sessions.Add(await host.Attachments.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Task, task, $"f{i}.txt", 1)));
        }

        await Assert.That(async () => await host.Attachments.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Task, task, "fifth.txt", 1))).Throws<AppException>();

        host.Clock.Now += TimeSpan.FromMinutes(61);
        await Assert.That(async () => await host.Attachments.ChunkAsync(sessions[0].UploadId, 0, [1])).Throws<AppException>();
        // Expired sessions free their slots and their temp files.
        var next = await host.Attachments.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Task, task, "later.txt", 1));
        await Assert.That(next.UploadId).IsNotNull();
        await Assert.That(Directory.EnumerateFiles(host.Storage.Options.UploadsDirectory).Count()).IsEqualTo(1);

        host.Attachments.Cancel(next.UploadId);
        await Assert.That(Directory.EnumerateFiles(host.Storage.Options.UploadsDirectory).Any()).IsFalse();
    }

    [Test]
    public async Task Rename_Delete_Restore_AreVersionCheckedOperations_AndExecutablesCannotOpen()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var item = await AddAsync(host, task, "setup.exe", "MZ"u8.ToArray());
        await Assert.That((await host.Attachments.GetAsync(item.Id)).CanOpen).IsFalse();

        var renamed = (await host.Attachments.RenameAsync(new RenameAttachmentCommand(DataHost.Op(), item.Id, item.Version, "notes.txt"))).Attachment;
        await Assert.That(renamed.FileName).IsEqualTo("notes.txt");
        await Assert.That((await host.Attachments.GetAsync(item.Id)).CanOpen).IsTrue();
        await Assert.That(async () => await host.Attachments.RenameAsync(new RenameAttachmentCommand(DataHost.Op(), item.Id, item.Version, "stale.txt"))).Throws<AppException>();

        var deleted = (await host.Attachments.DeleteAsync(new AttachmentCommand(DataHost.Op(), item.Id, renamed.Version))).Attachment;
        await Assert.That(await host.Attachments.ListAsync(AttachmentOwnerKind.Task, task)).IsEmpty();
        await host.Attachments.RestoreAsync(new AttachmentCommand(DataHost.Op(), item.Id, deleted.Version));
        await Assert.That(await host.Attachments.ListAsync(AttachmentOwnerKind.Task, task)).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Read_ReturnsTheBytesInSlices_AndAMissingFileIsAReadableError()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var bytes = Enumerable.Range(0, 3000).Select(i => (byte)(i % 251)).ToArray();
        var item = await AddAsync(host, task, "numbers.bin", bytes);

        var chunk = await host.Attachments.ReadAsync(item.Id, 1000, 500);
        await Assert.That(chunk.Bytes.SequenceEqual(bytes[1000..1500])).IsTrue();
        await Assert.That(chunk.TotalSize).IsEqualTo(3000L);
        var tail = await host.Attachments.ReadAsync(item.Id, 2900, 5000);
        await Assert.That(tail.Bytes.Length).IsEqualTo(100);

        await host.Files.DeleteAsync(item.Sha256, CancellationToken.None);
        await Assert.That((await host.Attachments.GetAsync(item.Id)).Bytes).IsEqualTo(AttachmentBytesState.Missing);
        await Assert.That(async () => await host.Attachments.ReadAsync(item.Id, 0, 10)).Throws<AppException>();
    }

    // ---- Purge and reference counting ----------------------------------------------------------

    [Test]
    public async Task Purge_RemovesAFileOnlyWhenNoAttachmentRowNamesItsBytes()
    {
        using var host = await DataHost.StartAsync();
        var one = await NewTaskAsync(host, "One");
        var two = await NewTaskAsync(host, "Two");
        var a = await AddAsync(host, one, "a.txt", "shared bytes"u8.ToArray());
        await AddAsync(host, two, "b.txt", "shared bytes"u8.ToArray());
        var solo = await AddAsync(host, one, "solo.txt", "only here"u8.ToArray());

        var first = (await host.Tasks.GetAsync(one))!;
        await host.Tasks.DeleteAsync(new DeleteTaskCommand(DataHost.Op(), first.Id, first.Version));
        var purged = await host.Purge.PurgeAsync(one, CancellationToken.None);

        await Assert.That(purged.Removed.Attachments).IsEqualTo(2L);
        await Assert.That(await host.Files.ExistsAsync(solo.Sha256, CancellationToken.None)).IsFalse();
        await Assert.That(await host.Files.ExistsAsync(a.Sha256, CancellationToken.None)).IsTrue(); // task Two still names it
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Attachments;")).IsEqualTo(1L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM AttachmentLocalState;")).IsEqualTo(1L);

        var second = (await host.Tasks.GetAsync(two))!;
        await host.Tasks.DeleteAsync(new DeleteTaskCommand(DataHost.Op(), second.Id, second.Version));
        await host.Purge.PurgeAsync(null, CancellationToken.None);
        await Assert.That(await host.Files.ExistsAsync(a.Sha256, CancellationToken.None)).IsFalse();
        await Assert.That(await host.CountAsync("SELECT count(*) FROM Attachments;")).IsEqualTo(0L);
        await Assert.That(await host.CountAsync("SELECT count(*) FROM AttachmentLocalState;")).IsEqualTo(0L);
    }

    [Test]
    public async Task Purge_OfADeletedAttachment_RemovesItsRowAndFile_ButNotWhileTheOwnerIsInTheTrash()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var item = await AddAsync(host, task, "gone.txt", "bye"u8.ToArray());
        var deleted = (await host.Attachments.DeleteAsync(new AttachmentCommand(DataHost.Op(), item.Id, item.Version))).Attachment;

        // Undo of the delete would still need the bytes, so nothing is purged until the person purges the trash.
        await Assert.That(await host.Files.ExistsAsync(item.Sha256, CancellationToken.None)).IsTrue();
        var counts = await host.Purge.PurgeAsync(null, CancellationToken.None);
        await Assert.That(counts.Removed.Attachments).IsEqualTo(1L);
        await Assert.That(await host.Files.ExistsAsync(item.Sha256, CancellationToken.None)).IsFalse();
        await Assert.That(deleted.DeletedAtUtc).IsNotNull();
    }

    // ---- Backup, restore, reset ----------------------------------------------------------------

    [Test]
    public async Task Backup_KeepsAttachmentCopiesInTheSharedStore_AndRestoreBringsLostFilesBack()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var item = await AddAsync(host, task, "keep.txt", "precious"u8.ToArray());

        var manifest = await host.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);
        await Assert.That(manifest.AttachmentHashes).Contains(item.Sha256);
        await Assert.That(await host.BackupFiles.ExistsAsync(item.Sha256, CancellationToken.None)).IsTrue();

        // The live bytes are lost (a wiped folder); the backup still has them.
        await host.Files.DeleteAsync(item.Sha256, CancellationToken.None);
        var restored = await host.Protection.RestoreBackupAsync(manifest.FileName, manifest.ChecksumSha256, CancellationToken.None);

        await Assert.That(restored.MissingAttachments).IsEqualTo(0);
        await Assert.That(await host.Files.ExistsAsync(item.Sha256, CancellationToken.None)).IsTrue();
        await Assert.That((await host.Attachments.GetAsync(item.Id)).Bytes).IsEqualTo(AttachmentBytesState.Present);
        var chunk = await host.Attachments.ReadAsync(item.Id, 0, 100);
        await Assert.That(Encoding.UTF8.GetString(chunk.Bytes)).IsEqualTo("precious");
    }

    [Test]
    public async Task Backups_ShareOneCopyPerFile_AndPruningAnOldBackupKeepsFilesTheNewerOneStillNeeds()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var shared = await AddAsync(host, task, "shared.txt", "in both backups"u8.ToArray());
        var removed = await AddAsync(host, task, "removed.txt", "only in the first backup"u8.ToArray());

        // A safety backup is kept by retention even next to a newer one on the same day.
        var older = await host.Protection.BackupNowAsync(BackupReason.PreImport, CancellationToken.None);
        await Assert.That(await BlobsAsync(host.BackupFiles)).Count().IsEqualTo(2);

        await host.Attachments.DeleteAsync(new AttachmentCommand(DataHost.Op(), removed.Id, removed.Version));
        await host.Purge.PurgeAsync(null, CancellationToken.None);
        host.Clock.Now += TimeSpan.FromMinutes(5);
        var newer = await host.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);
        await Assert.That(newer.AttachmentHashes).Contains(shared.Sha256);
        await Assert.That(newer.AttachmentHashes).DoesNotContain(removed.Sha256);
        await Assert.That(await BlobsAsync(host.BackupFiles)).Count().IsEqualTo(2); // shared once, plus the first backup's own

        // Retention drops the older backup: its private file goes, the shared one stays.
        File.Delete(Path.Combine(host.Backups.BackupsDirectory, older.FileName));
        File.Delete(Path.Combine(host.Backups.BackupsDirectory, older.FileName + BackupManager.ManifestSuffix));
        host.Backups.SweepBackupAttachments();

        await Assert.That(await host.BackupFiles.ExistsAsync(shared.Sha256, CancellationToken.None)).IsTrue();
        await Assert.That(await host.BackupFiles.ExistsAsync(removed.Sha256, CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task Sweep_DoesNothingWhileABackupHasNoDescription()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var item = await AddAsync(host, task, "x.txt", "x"u8.ToArray());
        var manifest = await host.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);
        File.Delete(Path.Combine(host.Backups.BackupsDirectory, manifest.FileName + BackupManager.ManifestSuffix));

        host.Backups.SweepBackupAttachments();

        await Assert.That(await host.BackupFiles.ExistsAsync(item.Sha256, CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task Reset_RemovesAttachmentFilesAndBackupCopiesWhenAsked()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var item = await AddAsync(host, task, "x.txt", "x"u8.ToArray());
        await host.Protection.BackupNowAsync(BackupReason.Manual, CancellationToken.None);

        await host.Protection.ResetAsync(deleteBackups: false, CancellationToken.None);
        await Assert.That(await host.Files.ExistsAsync(item.Sha256, CancellationToken.None)).IsFalse();
        await Assert.That(await host.BackupFiles.ExistsAsync(item.Sha256, CancellationToken.None)).IsTrue();

        await host.Protection.ResetAsync(deleteBackups: true, CancellationToken.None);
        await Assert.That(await host.BackupFiles.ExistsAsync(item.Sha256, CancellationToken.None)).IsFalse();
    }

    // ---- Export and archive --------------------------------------------------------------------

    [Test]
    public async Task JsonArchive_RoundTripsAttachmentsWithChecksums_ThroughARestoreIntoAnotherDataRoot()
    {
        using var source = await DataHost.StartAsync();
        var task = await NewTaskAsync(source);
        var page = (await new PageCommands(source.Store, source.Ids, source.Clock).CreateAsync(new CreatePageCommand(DataHost.Op(), null, "Pic", "text"))).Page;
        var image = await AddAsync(source, page.Id, "pic.png", PngBytes(10, 20, 7), AttachmentOwnerKind.Page);
        var text = await AddAsync(source, task, "note.txt", "round trip"u8.ToArray());
        var export = await source.Transfer.ExportAsync(ExportFormat.Json, Path.Combine(source.Root, "out"), CancellationToken.None);

        ArchiveContent archive;
        await using (var stream = File.OpenRead(export.Path))
        {
            archive = JsonArchive.Read(stream);
        }

        await Assert.That(archive.Attachments.Keys).IsEquivalentTo(new[] { image.Sha256, text.Sha256 }.Order().ToArray());
        await Assert.That(archive.Manifest.Files.Count(file => file.Path.StartsWith("attachments/", StringComparison.Ordinal))).IsEqualTo(2);
        await Assert.That(archive.Manifest.Tables["Attachments"]).IsEqualTo(2L);
        await Assert.That(archive.Table("AttachmentLocalState")).IsNull();

        using var target = await DataHost.StartAsync();
        await target.Protection.RestoreArchiveAsync(archive, export.FileName, CancellationToken.None);

        var restored = await target.Attachments.GetAsync(image.Id);
        await Assert.That(restored.Attachment.Width).IsEqualTo(10);
        await Assert.That(restored.Bytes).IsEqualTo(AttachmentBytesState.Present);
        await Assert.That((await target.Attachments.ReadAsync(text.Id, 0, 100)).Bytes.SequenceEqual("round trip"u8.ToArray())).IsTrue();
    }

    [Test]
    public async Task JsonArchive_WithATamperedAttachment_IsRefusedBeforeAnythingIsWritten()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var item = await AddAsync(host, task, "note.txt", "genuine bytes"u8.ToArray());
        var export = await host.Transfer.ExportAsync(ExportFormat.Json, Path.Combine(host.Root, "out"), CancellationToken.None);
        var good = await File.ReadAllBytesAsync(export.Path);

        byte[] Rewrite(Action<ZipArchive> change)
        {
            using var stream = new MemoryStream();
            stream.Write(good);
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
            {
                change(zip);
            }

            return stream.ToArray();
        }

        var tampered = Rewrite(zip =>
        {
            zip.GetEntry(JsonArchive.AttachmentEntry(item.Sha256))!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(JsonArchive.AttachmentEntry(item.Sha256)).Open());
            writer.Write("forged bytes!!");
        });
        var undeclared = Rewrite(zip =>
        {
            using var writer = new StreamWriter(zip.CreateEntry(JsonArchive.AttachmentEntry(new string('b', 64))).Open());
            writer.Write("sneaked in");
        });
        var badName = Rewrite(zip => zip.CreateEntry("attachments/not-a-hash"));
        var missing = Rewrite(zip => zip.GetEntry(JsonArchive.AttachmentEntry(item.Sha256))!.Delete());

        foreach (var (bytes, expected) in new[] { (tampered, "checksum"), (undeclared, "does not list"), (badName, "attachments/<hash>"), (missing, "not in it") })
        {
            var error = await Assert.That(() => DataTransfer.Read(Chaos.Application.Transfer.ImportSourceKind.ChaosJson, "x.zip", bytes, null)).Throws<AppException>();
            await Assert.That(error!.Message).Contains(expected);
        }
    }

    [Test]
    public async Task MarkdownExport_WritesAttachmentFilesAndRewritesReferencesToRelativePaths()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host, "Has a picture");
        var image = await AddAsync(host, task, "my picture.png", PngBytes(4, 4));
        var pad = (await new PadCommands(host.Store, host.Ids, host.Clock).CreateAsync(new CreatePadItemCommand(DataHost.Op(), "see ![x](chaos:attachment/" + image.Id + ")"))).Item;
        var current = (await host.Tasks.GetAsync(task))!;
        await host.Tasks.UpdateDetailsAsync(new UpdateTaskDetailsCommand(DataHost.Op(), task, current.Version, current.Title,
            "Look: ![pic](chaos:attachment/" + image.Id + ") and chaos:attachment/" + Guid.NewGuid(),
            null, null, null, null, null, null, current.Priority, null, current.CapacityCategory));

        var export = await host.Transfer.ExportAsync(ExportFormat.Markdown, Path.Combine(host.Root, "out"), CancellationToken.None);

        using var zip = ZipFile.OpenRead(export.Path);
        var file = zip.Entries.Single(entry => entry.FullName.StartsWith("attachments/", StringComparison.Ordinal));
        await Assert.That(file.FullName).IsEqualTo($"attachments/{image.Id}-my picture.png");
        string Read(string prefix)
        {
            using var reader = new StreamReader(zip.Entries.Single(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal) && entry.FullName.EndsWith(".md", StringComparison.Ordinal)).Open());
            return reader.ReadToEnd();
        }

        var taskText = Read("tasks/");
        await Assert.That(taskText).Contains($"](../../attachments/{image.Id}-my%20picture.png)");
        await Assert.That(taskText).Contains("chaos:attachment/"); // the unknown id is left as it was
        await Assert.That(Read("pad/")).Contains($"](../attachments/{image.Id}-my%20picture.png)");
        await Assert.That(pad.Id).IsNotNull();
    }
}
