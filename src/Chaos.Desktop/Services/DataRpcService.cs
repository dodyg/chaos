using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Transfer;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Transfer;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record DataCountsDto(int Tasks, int OpenTasks, int Lists, int Subtasks, int DiaryEntries, int PadItems, int Tags);
public sealed record BackupDto(string FileName, string? CreatedAtUtc, string? Reason, string? AppVersion, int? SchemaVersion, double SizeBytes, bool Described, string? Problem, DataCountsDto? Counts);
public sealed record ImportBatchDto(string Id, string Source, string SourceName, string ImportedAtUtc, int Created, int Skipped, string? BackupFileName);
public sealed record DataStatusRequest();
public sealed record DataStatusResponse(
    bool Ok,
    string? DataDirectory,
    string? BackupsDirectory,
    string? DefaultExportDirectory,
    BackupDto? LatestBackup,
    string? LastBackupError,
    string? LastBackupErrorAtUtc,
    string? RecoveryNotice,
    IReadOnlyList<BackupDto> Backups,
    IReadOnlyList<ImportBatchDto> Imports,
    int DailyKept,
    int WeeklyKept,
    TaskErrorResponse? Error);
public sealed record BackupNowRequest();
public sealed record BackupNowResponse(bool Ok, BackupDto? Backup, TaskErrorResponse? Error);
public sealed record BackupInspectRequest(string FileName);
public sealed record BackupInspectResponse(bool Ok, bool CanRestore, string? Problem, string? Checksum, string? CreatedAtUtc, string? Reason, int SchemaVersion, DataCountsDto? Counts, DataCountsDto? Current, TaskErrorResponse? Error);
public sealed record BackupRestoreRequest(string FileName, string Checksum);
public sealed record RestoreResponse(bool Ok, string? RestoredFrom, string? SafetyBackupFileName, DataCountsDto? Counts, TaskErrorResponse? Error);
public sealed record ExportRequest(string Format, string? Directory);
public sealed record ExportResponse(bool Ok, string? Path, string? FileName, double Bytes, TaskErrorResponse? Error);
public sealed record OpenFolderRequest(string Which, string? Path);
public sealed record OpenFolderResponse(bool Ok, TaskErrorResponse? Error);
public sealed record CsvColumnDto(string Field, int Column);
/// <summary>Format is "csv", "json" (merge) or "json-restore". ContentBase64 holds the file bytes.</summary>
public sealed record ImportPreviewRequest(string Format, string FileName, string ContentBase64, IReadOnlyList<CsvColumnDto>? Mapping);
public sealed record ImportIssueDto(int Line, string Field, string Message);
public sealed record ImportListPlanDto(string SourceName, int TaskCount, string? ExistingContainerId, string? ExistingName);
public sealed record ImportItemDto(int Line, string Title, string? ListName, string Status, int Subtasks, string Outcome, string? Detail);
public sealed record ArchiveSummaryDto(string ExportedAtUtc, string AppVersion, int SchemaVersion, IReadOnlyList<CsvColumnDto> TableRows, IReadOnlyList<string> TableNames);
public sealed record ImportPreviewResponse(
    bool Ok,
    string? Token,
    bool CanCommit,
    int NewTasks,
    int Duplicates,
    int Conflicts,
    int Subtasks,
    int DiaryEntries,
    IReadOnlyList<ImportListPlanDto> Lists,
    IReadOnlyList<ImportItemDto> Items,
    IReadOnlyList<ImportIssueDto> Errors,
    IReadOnlyList<ImportIssueDto> Warnings,
    IReadOnlyList<string> CsvHeaders,
    IReadOnlyList<CsvColumnDto> CsvMapping,
    IReadOnlyList<string> CsvFields,
    ArchiveSummaryDto? Archive,
    DataCountsDto? ArchiveCounts,
    TaskErrorResponse? Error);
public sealed record ListTargetDto(string SourceName, string ContainerId);
public sealed record ImportCommitRequest(string Format, string FileName, string ContentBase64, IReadOnlyList<CsvColumnDto>? Mapping, string Token, IReadOnlyList<ListTargetDto>? ListTargets, string? DefaultContainerId);
public sealed record ImportCommitResponse(bool Ok, string? BatchId, int Created, int Duplicates, int Conflicts, IReadOnlyList<string> CreatedLists, string? BackupFileName, RestoreResponse? Restore, TaskErrorResponse? Error);
public sealed record PurgeRequest(string? TaskId);
public sealed record PurgeCountsDto(int Tasks, int Subtasks, int DiaryEntries, int PadItems, int Lists, int Sections, int Tags, int Templates, int Total);
public sealed record PurgeResponse(bool Ok, PurgeCountsDto? Counts, TaskErrorResponse? Error);
public sealed record ResetRequest(string Confirmation, bool DeleteBackups);
public sealed record ResetResponse(bool Ok, int BackupFilesDeleted, TaskErrorResponse? Error);

/// <summary>
/// Settings → Data (PLAN §14): backups and restore, export, import with preview, permanent
/// purge and "Delete all local data". Errors come back as data. Every destructive action needs
/// an explicit confirmation value from the preview it follows: the backup's checksum, the import
/// preview token, or the typed reset phrase.
/// </summary>
[NeoRpcService("data", Version = 1)]
public sealed class DataRpcService(DataProtectionService protection, DataTransfer transfer, ImportCommands imports, DataPurge purge, RecoveryNotice? recoveryNotice, Func<IReadOnlyList<string>, CancellationToken, Task>? cancelOsNotifications = null, Func<string, bool>? folderOpener = null)
{
    public const string ResetPhrase = "DELETE";

    private string? _recoveryNotice = recoveryNotice?.Message;
    private string? _lastExportDirectory;

    public static string DefaultExportDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Chaos exports");

    [NeoRpcMethod("status")]
    public async ValueTask<DataStatusResponse> StatusAsync(DataStatusRequest request, CancellationToken ct)
    {
        try
        {
            var backups = protection.Backups.List();
            var batches = await imports.ListBatchesAsync(10, ct);
            var latest = backups.FirstOrDefault(entry => entry.Manifest is not null);
            var error = protection.Backups.LastError;
            var notice = _recoveryNotice;
            _recoveryNotice = null; // shown once
            return new(true, Path.GetDirectoryName(protection.Options.DatabasePath), protection.Backups.BackupsDirectory, _lastExportDirectory ?? DefaultExportDirectory,
                latest is null ? null : Map(latest), error?.Message, error is { } e ? Instant(e.AtUtc) : null, notice,
                [.. backups.Select(Map)],
                [.. batches.Select(batch => new ImportBatchDto(batch.Id, batch.SourceKind.ToString(), batch.SourceName, Instant(batch.ImportedAtUtc), batch.CreatedCount, batch.SkippedCount, batch.BackupFileName))],
                BackupManager.DailyToKeep, BackupManager.WeeklyToKeep, null);
        }
        catch (AppException error)
        {
            return new(false, null, null, null, null, null, null, null, [], [], 0, 0, TaskRpcService.ToErrorResponse(error));
        }
    }

    [NeoRpcMethod("backupNow")]
    public async ValueTask<BackupNowResponse> BackupNowAsync(BackupNowRequest request, CancellationToken ct)
    {
        try
        {
            var manifest = await protection.BackupNowAsync(BackupReason.Manual, ct);
            return new(true, Map(new BackupEntry(manifest.FileName, manifest, BackupValidationStatus.Valid, null, manifest.SizeBytes, manifest.CreatedAtUtc)), null);
        }
        catch (AppException error)
        {
            return new(false, null, TaskRpcService.ToErrorResponse(error));
        }
    }

    [NeoRpcMethod("inspectBackup")]
    public async ValueTask<BackupInspectResponse> InspectBackupAsync(BackupInspectRequest request, CancellationToken ct)
    {
        try
        {
            var inspection = protection.Backups.Inspect(request.FileName);
            var checksum = inspection.CanRestore ? BackupManager.ComputeChecksum(protection.Backups.ResolveBackupPath(request.FileName)) : null;
            var current = await CurrentCountsAsync(ct);
            return new(true, inspection.CanRestore, inspection.Problem, checksum, inspection.Manifest is { } m ? Instant(m.CreatedAtUtc) : null,
                inspection.Manifest?.Reason.ToString(), Int(inspection.SchemaVersion), Map(inspection.Counts), current, null);
        }
        catch (AppException error)
        {
            return new(false, false, null, null, null, null, 0, null, null, TaskRpcService.ToErrorResponse(error));
        }
    }

    [NeoRpcMethod("restoreBackup")]
    public async ValueTask<RestoreResponse> RestoreBackupAsync(BackupRestoreRequest request, CancellationToken ct)
    {
        try
        {
            return Map(await protection.RestoreBackupAsync(request.FileName, request.Checksum, ct));
        }
        catch (AppException error)
        {
            return new(false, null, null, null, TaskRpcService.ToErrorResponse(error));
        }
        catch (IOException error)
        {
            return new(false, null, null, null, TaskRpcService.ToErrorResponse(new AppException(AppErrorKind.StorageUnavailable, $"The restore could not finish ({error.Message}). Your previous data was kept.", innerException: error)));
        }
    }

    [NeoRpcMethod("export")]
    public async ValueTask<ExportResponse> ExportAsync(ExportRequest request, CancellationToken ct)
    {
        try
        {
            var format = request.Format.ToLowerInvariant() switch
            {
                "json" => ExportFormat.Json,
                "markdown" => ExportFormat.Markdown,
                _ => throw AppException.Validation("format", "Choose JSON archive or Markdown."),
            };
            var directory = string.IsNullOrWhiteSpace(request.Directory) ? DefaultExportDirectory : request.Directory.Trim();
            var outcome = await transfer.ExportAsync(format, directory, ct);
            _lastExportDirectory = directory;
            return new(true, outcome.Path, outcome.FileName, outcome.Bytes, null);
        }
        catch (AppException error)
        {
            return new(false, null, null, 0, TaskRpcService.ToErrorResponse(error));
        }
    }

    /// <summary>Opens only the backups folder or the folder of the last export, never an arbitrary path.</summary>
    [NeoRpcMethod("openFolder")]
    public ValueTask<OpenFolderResponse> OpenFolderAsync(OpenFolderRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var folder = request.Which switch
        {
            "backups" => protection.Backups.BackupsDirectory,
            "exports" => _lastExportDirectory ?? DefaultExportDirectory,
            _ => null,
        };
        if (folder is null)
        {
            return ValueTask.FromResult(new OpenFolderResponse(false, TaskRpcService.ToErrorResponse(AppException.Validation("which", "Choose the backups or exports folder."))));
        }

        Directory.CreateDirectory(folder);
        var opened = (folderOpener ?? OpenWithShell)(folder);
        return ValueTask.FromResult(opened
            ? new OpenFolderResponse(true, null)
            : new OpenFolderResponse(false, TaskRpcService.ToErrorResponse(new AppException(AppErrorKind.UnsupportedCapability, "The folder could not be opened."))));
    }

    [NeoRpcMethod("importPreview")]
    public async ValueTask<ImportPreviewResponse> ImportPreviewAsync(ImportPreviewRequest request, CancellationToken ct)
    {
        try
        {
            var (kind, restore) = ParseFormat(request.Format);
            var content = Decode(request.ContentBase64);
            var source = DataTransfer.Read(kind, request.FileName, content, Mapping(request.Mapping));
            var token = Token(request.Format, content, source.CsvMapping);
            var mapping = source.CsvMapping?.Select(pair => new CsvColumnDto(pair.Key, pair.Value)).OrderBy(item => item.Field, StringComparer.Ordinal).ToList() ?? [];
            var archive = source.Archive is { } a
                ? new ArchiveSummaryDto(Instant(a.Manifest.ExportedAtUtc), a.Manifest.AppVersion, Int(a.Manifest.SchemaVersion),
                    [.. a.Manifest.Tables.Where(pair => pair.Value > 0).Select(pair => new CsvColumnDto(pair.Key, (int)Math.Min(pair.Value, int.MaxValue)))], [.. a.Manifest.Tables.Keys])
                : null;

            if (restore)
            {
                var archiveCounts = ArchiveCounts(source.Archive!);
                return new(true, token, true, 0, 0, 0, 0, 0, [], [], [], [], [], [], [], archive, archiveCounts, null);
            }

            var preview = await imports.PreviewAsync(source.Draft, ct);
            return new(true, token, preview.CanCommit, preview.NewTasks, preview.Duplicates, preview.Conflicts, preview.Subtasks, preview.DiaryEntries,
                [.. preview.Lists.Select(list => new ImportListPlanDto(list.SourceName, list.TaskCount, list.ExistingContainerId, list.ExistingName))],
                [.. preview.Items.Select(item => new ImportItemDto(item.Line, item.Title, item.ListName, item.Status.ToString(), item.Subtasks, item.Outcome.ToString(), item.Detail))],
                [.. preview.Errors.Select(Map)], [.. preview.Warnings.Select(Map)],
                source.Csv?.Headers ?? [], mapping, CsvImport.Fields, archive, null, null);
        }
        catch (AppException error)
        {
            return new(false, null, false, 0, 0, 0, 0, 0, [], [], [], [], [], [], [], null, null, TaskRpcService.ToErrorResponse(error));
        }
    }

    [NeoRpcMethod("importCommit")]
    public async ValueTask<ImportCommitResponse> ImportCommitAsync(ImportCommitRequest request, CancellationToken ct)
    {
        try
        {
            var (kind, restore) = ParseFormat(request.Format);
            var content = Decode(request.ContentBase64);
            var source = DataTransfer.Read(kind, request.FileName, content, Mapping(request.Mapping));
            if (!string.Equals(Token(request.Format, content, source.CsvMapping), request.Token, StringComparison.Ordinal))
            {
                throw AppException.Validation("token", "The file or its column choices changed after the preview. Preview it again before importing.");
            }

            if (restore)
            {
                var outcome = Map(await protection.RestoreArchiveAsync(source.Archive!, Path.GetFileName(request.FileName), ct));
                return new(true, null, 0, 0, 0, [], outcome.SafetyBackupFileName, outcome, null);
            }

            if (source.Draft.Errors.Count > 0 || ImportCommands.Validate(source.Draft).Count > 0)
            {
                // Same message the commit gives; checked here so no backup is made for nothing.
                await imports.CommitAsync(source.Draft, new ImportCommitOptions(new Dictionary<string, string>(), null, null), ct);
            }

            var targets = (request.ListTargets ?? []).Where(target => !string.IsNullOrWhiteSpace(target.ContainerId))
                .GroupBy(target => target.SourceName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().ContainerId, StringComparer.OrdinalIgnoreCase);
            var result = await protection.GuardAsync(async () =>
            {
                var backup = await protection.Backups.CreateAsync(BackupReason.PreImport, ct);
                var committed = await imports.CommitAsync(source.Draft, new ImportCommitOptions(targets, string.IsNullOrWhiteSpace(request.DefaultContainerId) ? null : request.DefaultContainerId, backup.FileName), ct);
                return (committed, backup.FileName);
            }, ct);
            return new(true, result.committed.BatchId, result.committed.Created, result.committed.Duplicates, result.committed.Conflicts, result.committed.CreatedListNames, result.FileName, null, null);
        }
        catch (AppException error)
        {
            return new(false, null, 0, 0, 0, [], null, null, TaskRpcService.ToErrorResponse(error));
        }
    }

    [NeoRpcMethod("purgePreview")]
    public async ValueTask<PurgeResponse> PurgePreviewAsync(PurgeRequest request, CancellationToken ct)
    {
        try
        {
            return new(true, Map(await purge.PreviewAsync(Blank(request.TaskId), ct)), null);
        }
        catch (AppException error)
        {
            return new(false, null, TaskRpcService.ToErrorResponse(error));
        }
    }

    [NeoRpcMethod("purge")]
    public async ValueTask<PurgeResponse> PurgeAsync(PurgeRequest request, CancellationToken ct)
    {
        try
        {
            var outcome = await protection.GuardAsync(() => purge.PurgeAsync(Blank(request.TaskId), ct), ct);
            if (cancelOsNotifications is not null && outcome.OsNotificationIds.Count > 0)
            {
                await cancelOsNotifications(outcome.OsNotificationIds, ct); // purged reminders leave the OS schedule too
            }

            return new(true, Map(outcome.Removed), null);
        }
        catch (AppException error)
        {
            return new(false, null, TaskRpcService.ToErrorResponse(error));
        }
    }

    [NeoRpcMethod("reset")]
    public async ValueTask<ResetResponse> ResetAsync(ResetRequest request, CancellationToken ct)
    {
        try
        {
            if (!string.Equals(request.Confirmation?.Trim(), ResetPhrase, StringComparison.Ordinal))
            {
                throw AppException.Validation("confirmation", $"Type {ResetPhrase} to confirm that all local data should be deleted.");
            }

            var outcome = await protection.ResetAsync(request.DeleteBackups, ct);
            return new(true, outcome.BackupFilesDeleted, null);
        }
        catch (AppException error)
        {
            return new(false, 0, TaskRpcService.ToErrorResponse(error));
        }
        catch (IOException error)
        {
            return new(false, 0, TaskRpcService.ToErrorResponse(new AppException(AppErrorKind.StorageUnavailable, $"Not everything could be deleted ({error.Message}). Close other programs using the data folder and try again.", innerException: error)));
        }
    }

    private async Task<DataCountsDto?> CurrentCountsAsync(CancellationToken ct)
    {
        var preview = await purge.CurrentCountsAsync(ct);
        return Map(preview);
    }

    private static (ImportSourceKind Kind, bool Restore) ParseFormat(string format) => format.ToLowerInvariant() switch
    {
        "csv" => (ImportSourceKind.Csv, false),
        "json" => (ImportSourceKind.ChaosJson, false),
        "json-restore" => (ImportSourceKind.ChaosJson, true),
        _ => throw AppException.Validation("format", "Choose CSV or a Chaos JSON archive."),
    };

    private static byte[] Decode(string base64)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > (DataTransfer.MaxImportBytes / 3 + 1) * 4)
        {
            throw AppException.Validation("file", "Choose a file to import (at most 100 MB).");
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw AppException.Validation("file", "The file could not be read.");
        }
    }

    private static IReadOnlyDictionary<string, int>? Mapping(IReadOnlyList<CsvColumnDto>? mapping) =>
        mapping is null ? null : mapping.Where(item => item.Column >= 0).GroupBy(item => item.Field, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Column, StringComparer.Ordinal);

    /// <summary>Binds a commit to exactly the previewed bytes, format and column mapping.</summary>
    private static string Token(string format, byte[] content, IReadOnlyDictionary<string, int>? mapping)
    {
        var text = new StringBuilder(format.ToLowerInvariant()).Append('|').Append(DataTransfer.Fingerprint(content));
        foreach (var (field, column) in (mapping ?? new Dictionary<string, int>()).OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            text.Append('|').Append(field).Append('=').Append(column.ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static DataCountsDto ArchiveCounts(ArchiveContent archive)
    {
        int Active(string table)
        {
            var data = archive.Table(table);
            if (data is null)
            {
                return 0;
            }

            var deleted = data.Columns.ToList().IndexOf("DeletedAtUtc");
            return deleted < 0 ? data.Rows.Count : data.Rows.Count(row => row[deleted] is null);
        }

        var tasks = archive.Table("Tasks");
        var statusIndex = tasks?.Columns.ToList().IndexOf("Status") ?? -1;
        var deletedIndex = tasks?.Columns.ToList().IndexOf("DeletedAtUtc") ?? -1;
        var open = tasks is null || statusIndex < 0 ? 0 : tasks.Rows.Count(row => (deletedIndex < 0 || row[deletedIndex] is null) && row[statusIndex] is long status && status is not (4 or 5));
        return new(Active("Tasks"), open, Active("Containers"), Active("Subtasks"), Active("DiaryEntries"), Active("ScratchPadItems"), Active("Tags"));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Instant(DateTimeOffset value) => StorageFormats.Instant(value);

    private static BackupDto Map(BackupEntry entry) => new(
        entry.FileName,
        Instant(entry.CreatedAtUtc),
        entry.Manifest?.Reason.ToString(),
        entry.Manifest?.AppVersion,
        entry.Manifest is null ? null : Int(entry.Manifest.SchemaVersion),
        entry.SizeBytes,
        entry.Manifest is not null,
        entry.Problem,
        entry.Manifest is null ? null : Map(entry.Manifest.Counts));

    private static DataCountsDto Map(DataCounts counts) => new(Int(counts.Tasks), Int(counts.OpenTasks), Int(counts.Lists), Int(counts.Subtasks), Int(counts.DiaryEntries), Int(counts.PadItems), Int(counts.Tags));

    private static int Int(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    private static PurgeCountsDto Map(PurgeCounts counts) => new(Int(counts.Tasks), Int(counts.Subtasks), Int(counts.DiaryEntries), Int(counts.PadItems), Int(counts.Lists), Int(counts.Sections), Int(counts.Tags), Int(counts.Templates), Int(counts.Total));

    private static ImportIssueDto Map(ImportIssue issue) => new(issue.Line, issue.Field, issue.Message);

    private static RestoreResponse Map(RestoreOutcome outcome) => new(true, outcome.RestoredFrom, outcome.SafetyBackupFileName, Map(outcome.Counts), null);

    private static bool OpenWithShell(string folder)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false });
            return true;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
