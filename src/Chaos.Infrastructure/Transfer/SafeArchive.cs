using System.IO.Compression;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Transfer;

/// <summary>
/// Reading ZIP files from outside the app (PLAN §14): entry names must be plain relative
/// paths (no traversal, drive letters, absolute or device paths), and both the entry count and
/// the expanded size are capped before anything is decompressed, so a hostile archive cannot
/// write outside its staging area or exhaust memory or disk. Nothing in an archive is executed.
/// </summary>
public static class SafeArchive
{
    public const int MaxEntries = 10_000;
    public const long MaxEntryBytes = 256L * 1024 * 1024;
    public const long MaxTotalBytes = 512L * 1024 * 1024;

    /// <summary>Opens a ZIP and checks every entry name and declared size. Throws a validation error naming the problem.</summary>
    public static ZipArchive Open(Stream stream)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw AppException.Validation("file", "This file is not a readable ZIP archive.");
        }

        if (archive.Entries.Count > MaxEntries)
        {
            archive.Dispose();
            throw AppException.Validation("file", $"This archive has more than {MaxEntries} entries, which is more than Chaos accepts.");
        }

        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (!IsSafeEntryName(entry.FullName))
            {
                archive.Dispose();
                throw AppException.Validation("file", $"This archive contains an unsafe path ({Shorten(entry.FullName)}), so it was not opened.");
            }

            total += entry.Length;
            if (entry.Length > MaxEntryBytes || total > MaxTotalBytes)
            {
                archive.Dispose();
                throw AppException.Validation("file", "This archive expands to more data than Chaos accepts.");
            }
        }

        return archive;
    }

    /// <summary>Reads one entry fully, enforcing the size cap on the bytes actually produced, not only the declared size.</summary>
    public static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long read = 0;
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            read += count;
            if (read > MaxEntryBytes || read > entry.Length)
            {
                throw AppException.Validation("file", "An archive entry expands to more data than it declares, so the archive was not opened.");
            }

            output.Write(buffer, 0, count);
        }

        return output.ToArray();
    }

    public static bool IsSafeEntryName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 260 || name.Contains('\\') || name.Contains(':') || name.Contains('\0')
            || name.StartsWith('/') || Path.IsPathRooted(name))
        {
            return false;
        }

        foreach (var segment in name.Split('/'))
        {
            if (segment is "." or ".." || (segment.Length == 0 && !name.EndsWith('/')))
            {
                return false;
            }
        }

        return true;
    }

    private static string Shorten(string name) => name.Length <= 60 ? name : name[..57] + "...";
}
