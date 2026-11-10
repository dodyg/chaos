using System.Text;
using Chaos.Application.Tasks;
using Chaos.Application.Transfer;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Transfer;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>Settings → Data through the typed RPC surface (PLAN §14), against real SQLite.</summary>
public sealed class DataRpcTests
{
    private static async Task<(ChaosStorageHandle Storage, DataRpcService Rpc, TaskCommands Tasks, List<string> Opened)> StartAsync(string root, TimeProvider clock)
    {
        var ids = new SequentialIdGenerator();
        var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
        var protection = new DataProtectionService(storage.Store, storage.Options, new BackupManager(storage.Store.Source, storage.Options.BackupsDirectory, "0.1.0", clock), ids, clock);
        var opened = new List<string>();
        var rpc = new DataRpcService(protection, new DataTransfer(storage.Store, "0.1.0", clock), new ImportCommands(storage.Store, ids, clock), new DataPurge(storage.Store),
            storage.RecoveryNotice, null, folder => { opened.Add(folder); return true; });
        return (storage, rpc, new TaskCommands(storage.Store, ids, clock), opened);
    }

    [Test]
    public async Task BackupInspectRestore_ThroughRpc_NeedsThePreviewedChecksum()
    {
        var root = TestPaths.NewDataRoot();
        var (storage, rpc, tasks, opened) = await StartAsync(root, new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero)));
        using (storage)
        {
            await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Kept by the backup", null));
            var backup = await rpc.BackupNowAsync(new(), CancellationToken.None);
            await Assert.That(backup.Ok).IsTrue();
            await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Made after the backup", null));

            var status = await rpc.StatusAsync(new(), CancellationToken.None);
            await Assert.That(status.LatestBackup!.FileName).IsEqualTo(backup.Backup!.FileName);
            await Assert.That(status.Backups.Count).IsEqualTo(1);

            var inspect = await rpc.InspectBackupAsync(new(backup.Backup.FileName), CancellationToken.None);
            await Assert.That(inspect.CanRestore).IsTrue();
            await Assert.That(inspect.Counts!.Tasks).IsEqualTo(1);
            await Assert.That(inspect.Current!.Tasks).IsEqualTo(2);

            var wrong = await rpc.RestoreBackupAsync(new(backup.Backup.FileName, new string('b', 64)), CancellationToken.None);
            await Assert.That(wrong.Ok).IsFalse();
            await Assert.That(wrong.Error!.Fields.Single().FieldPath).IsEqualTo("checksum");

            var restored = await rpc.RestoreBackupAsync(new(backup.Backup.FileName, inspect.Checksum!), CancellationToken.None);
            await Assert.That(restored.Ok).IsTrue();
            await Assert.That(restored.Counts!.Tasks).IsEqualTo(1);
            await Assert.That(restored.SafetyBackupFileName).IsNotNull();

            await Assert.That((await rpc.OpenFolderAsync(new("backups", null), CancellationToken.None)).Ok).IsTrue();
            await Assert.That((await rpc.OpenFolderAsync(new("C:\\Windows", null), CancellationToken.None)).Ok).IsFalse();
            await Assert.That(opened).IsEquivalentTo(new[] { storage.Options.BackupsDirectory });
        }
    }

    [Test]
    public async Task ExportAndCsvImport_ThroughRpc_PreviewTokenGuardsTheCommit()
    {
        var root = TestPaths.NewDataRoot();
        var (storage, rpc, tasks, _) = await StartAsync(root, new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero)));
        using (storage)
        {
            await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Exported task", "with notes"));
            var exportDir = Path.Combine(root, "my exports");
            foreach (var format in new[] { "json", "markdown", "org" })
            {
                var export = await rpc.ExportAsync(new(format, exportDir), CancellationToken.None);
                await Assert.That(export.Ok).IsTrue();
                await Assert.That(File.Exists(export.Path!)).IsTrue();
            }

            await Assert.That((await rpc.ExportAsync(new("pdf", exportDir), CancellationToken.None)).Ok).IsFalse();
            await Assert.That((await rpc.ExportAsync(new("json", "relative\\folder"), CancellationToken.None)).Ok).IsFalse();

            var csv = Convert.ToBase64String(Encoding.UTF8.GetBytes("Task,Where,Deadline\nRenew passport,Admin,2026-12-01\nBook dentist,Admin,\n"));
            var preview = await rpc.ImportPreviewAsync(new("csv", "todo.csv", csv, null), CancellationToken.None);
            await Assert.That(preview.Ok).IsTrue();
            await Assert.That(preview.CsvHeaders).IsEquivalentTo(new[] { "Task", "Where", "Deadline" });
            // "Deadline" is recognised as the due date; "Where" is not known, so the list column is chosen by hand.
            await Assert.That(preview.CsvMapping.Select(m => m.Field)).IsEquivalentTo(new[] { "due", "title" });
            var mapping = new List<CsvColumnDto> { new("title", 0), new("list", 1), new("due", 2) };
            var mapped = await rpc.ImportPreviewAsync(new("csv", "todo.csv", csv, mapping), CancellationToken.None);
            await Assert.That(mapped.NewTasks).IsEqualTo(2);
            await Assert.That(mapped.Lists.Single().SourceName).IsEqualTo("Admin");

            var stale = await rpc.ImportCommitAsync(new("csv", "todo.csv", csv, mapping, preview.Token!, null, null), CancellationToken.None);
            await Assert.That(stale.Ok).IsFalse();
            await Assert.That(stale.Error!.Fields.Single().FieldPath).IsEqualTo("token");

            var committed = await rpc.ImportCommitAsync(new("csv", "todo.csv", csv, mapping, mapped.Token!, null, null), CancellationToken.None);
            await Assert.That(committed.Ok).IsTrue();
            await Assert.That(committed.Created).IsEqualTo(2);
            await Assert.That(committed.CreatedLists).IsEquivalentTo(new[] { "Admin" });
            await Assert.That(committed.BackupFileName).IsNotNull();

            var status = await rpc.StatusAsync(new(), CancellationToken.None);
            await Assert.That(status.Imports.Single().Created).IsEqualTo(2);
            await Assert.That(status.Backups.Single().Reason).IsEqualTo("PreImport");
        }
    }

    [Test]
    public async Task Reset_ThroughRpc_NeedsTheTypedPhrase()
    {
        var root = TestPaths.NewDataRoot();
        var (storage, rpc, tasks, _) = await StartAsync(root, new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero)));
        using (storage)
        {
            await tasks.CaptureAsync(new(Guid.NewGuid().ToString(), "", "Soon gone", null));

            var refused = await rpc.ResetAsync(new("delete", false), CancellationToken.None);
            await Assert.That(refused.Ok).IsFalse();
            await Assert.That((await tasks.ListInboxAsync()).Count).IsEqualTo(1);

            var reset = await rpc.ResetAsync(new(DataRpcService.ResetPhrase, false), CancellationToken.None);
            await Assert.That(reset.Ok).IsTrue();
            await Assert.That((await tasks.ListInboxAsync()).Count).IsEqualTo(0);
        }
    }
}
