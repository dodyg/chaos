using System.Security.Cryptography;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Transfer;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Transfer;

public enum ExportFormat
{
    Json = 0,
    Markdown = 1,
    Org = 2,
}

public sealed record ExportOutcome(string Path, string FileName, long Bytes, ExportFormat Format);

/// <summary>What a source file contained, read and parsed, with nothing written.</summary>
public sealed record ImportSource(ImportDraft Draft, CsvTable? Csv, IReadOnlyDictionary<string, int>? CsvMapping, ArchiveContent? Archive);

/// <summary>
/// Export to a user-chosen folder and reading of import sources (PLAN §14). Exports are written
/// to a temporary file in the target folder and renamed when complete, so a failed export never
/// leaves a truncated file under the final name. Chaos never deletes or overwrites anything in
/// the export folder except the file it is writing.
/// </summary>
public sealed class DataTransfer(SqliteApplicationStore store, string appVersion, TimeProvider clock)
{
    public const long MaxImportBytes = 100L * 1024 * 1024;

    public async Task<ExportOutcome> ExportAsync(ExportFormat format, string directory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
        {
            throw AppException.Validation("directory", "Choose a full folder path to export to, such as C:\\Users\\you\\Documents\\Chaos exports.");
        }

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw AppException.Validation("directory", $"Chaos cannot write to that folder: {error.Message}");
        }

        var now = clock.GetUtcNow();
        var fileName = format switch
        {
            ExportFormat.Json => $"chaos-archive-{now:yyyyMMdd-HHmmss}.zip",
            ExportFormat.Markdown => $"chaos-markdown-{now:yyyyMMdd-HHmmss}.zip",
            _ => $"chaos-{now:yyyyMMdd-HHmmss}.org",
        };
        var path = Path.Combine(directory, fileName);
        var temp = path + ".partial";

        try
        {
            await store.QueryAsync<object?>(async (session, token) =>
            {
                var connection = ((SqliteDataSession)session).Connection;
                await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);
                switch (format)
                {
                    case ExportFormat.Json:
                        await JsonArchive.WriteAsync(connection, output, appVersion, now, token);
                        break;
                    case ExportFormat.Markdown:
                        PlainTextExport.WriteMarkdownZip(await ExportSnapshot.ReadAsync(connection, token), output, now);
                        break;
                    default:
                        var org = PlainTextExport.WriteOrg(await ExportSnapshot.ReadAsync(connection, token), now);
                        await output.WriteAsync(new UTF8Encoding(false).GetBytes(org), token);
                        break;
                }

                output.Flush(flushToDisk: true);
                return null;
            }, ct);

            File.Move(temp, path, overwrite: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DataProtection.BackupManager.TryDelete(temp);
            throw error is IOException io && DataProtection.BackupManager.IsDiskFull(io)
                ? new AppException(AppErrorKind.StorageFull, "There is not enough free disk space for the export. Free some space and try again.", innerException: error)
                : new AppException(AppErrorKind.StorageUnavailable, $"The export could not be written: {error.Message}", innerException: error);
        }
        catch
        {
            DataProtection.BackupManager.TryDelete(temp);
            throw;
        }

        return new ExportOutcome(path, fileName, new FileInfo(path).Length, format);
    }

    /// <summary>SHA-256 of the imported bytes, used as the file fingerprint and preview token base.</summary>
    public static string Fingerprint(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>
    /// Reads an import file. CSV uses <paramref name="csvMapping"/> when given, otherwise the
    /// suggested mapping. A Chaos archive is fully validated (entries, sizes, checksums, schema).
    /// </summary>
    public static ImportSource Read(ImportSourceKind kind, string sourceName, byte[] content, IReadOnlyDictionary<string, int>? csvMapping)
    {
        if (content.Length == 0)
        {
            throw AppException.Validation("file", "This file is empty.");
        }

        if (content.Length > MaxImportBytes)
        {
            throw AppException.Validation("file", $"This file is larger than {MaxImportBytes / (1024 * 1024)} MB, which is more than one import accepts.");
        }

        var name = Path.GetFileName(sourceName ?? "");
        var fingerprint = Fingerprint(content);
        switch (kind)
        {
            case ImportSourceKind.ChaosJson:
            {
                using var stream = new MemoryStream(content, writable: false);
                var archive = JsonArchive.Read(stream);
                return new ImportSource(ArchiveMerge.ToDraft(archive, name, fingerprint), null, null, archive);
            }

            case ImportSourceKind.Org:
                return new ImportSource(OrgImport.Parse(name, fingerprint, DecodeText(content)), null, null, null);

            default:
            {
                var table = CsvImport.ParseTable(DecodeText(content));
                var mapping = csvMapping ?? CsvImport.SuggestMapping(table.Headers);
                return new ImportSource(CsvImport.ToDraft(name, fingerprint, table, mapping), table, mapping, null);
            }
        }
    }

    /// <summary>UTF-8 text (with or without BOM); invalid UTF-8 is refused rather than silently changed.</summary>
    private static string DecodeText(byte[] content)
    {
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            throw AppException.Validation("file", "This file is not UTF-8 text. Save it as UTF-8 and try again.");
        }
    }
}
