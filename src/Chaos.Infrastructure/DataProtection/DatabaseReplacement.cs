using System.Text.Json;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Infrastructure.DataProtection;

/// <summary>Where an interrupted replacement had got to; written before each step so startup can finish or undo it.</summary>
public enum ReplacementStage
{
    /// <summary>The current files are being moved aside; whatever is still in place is the original.</summary>
    MovingOut = 0,
    /// <summary>The originals are aside and the staged file is being moved in.</summary>
    MovingIn = 1,
    /// <summary>The new file is in place; it still has to open, migrate and validate.</summary>
    Swapped = 2,
}

public sealed record ReplacementMarker(ReplacementStage Stage, string Description, DateTimeOffset StartedAtUtc);

/// <summary>What startup did about an interrupted replacement; null when there was none.</summary>
public sealed record RecoveryNotice(bool KeptPrevious, string Message);

/// <summary>
/// Replaces the live database file with a staged one while no connection is open (PLAN §14).
/// The original database and its WAL/SHM files are moved aside, never deleted, until the new
/// file has opened and validated; a marker file records each step, so a crash at any point
/// restarts into a clear state: either the previous data, or the finished replacement.
/// </summary>
public static class DatabaseReplacement
{
    public const string MarkerFileName = "replace-pending.json";
    public const string ReplacedSuffix = ".replaced";
    public const string StagedFileName = "replace-staged.db";
    private static readonly string[] Companions = ["", "-wal", "-shm", "-journal"];

    public static string MarkerPath(StorageOptions options) => Path.Combine(DataDirectory(options), MarkerFileName);

    public static string StagedPath(StorageOptions options) => Path.Combine(DataDirectory(options), StagedFileName);

    private static string DataDirectory(StorageOptions options) => Path.GetDirectoryName(options.DatabasePath)!;

    /// <summary>
    /// Moves the current database files aside and the staged file into place. Every
    /// connection to the database must be closed. On return the stage is Swapped; the caller
    /// opens and validates the new file, then calls <see cref="Complete"/> or <see cref="Revert"/>.
    /// </summary>
    public static void Swap(StorageOptions options, string stagedPath, string description, DateTimeOffset now)
    {
        var database = options.DatabasePath;
        if (File.Exists(MarkerPath(options)) || Companions.Any(suffix => File.Exists(database + ReplacedSuffix + suffix)))
        {
            throw new IOException("An earlier replacement of the data file was not finished. Restart Chaos so it can recover, then try again.");
        }

        WriteMarker(options, new ReplacementMarker(ReplacementStage.MovingOut, description, now));
        foreach (var suffix in Companions)
        {
            if (File.Exists(database + suffix))
            {
                File.Move(database + suffix, database + ReplacedSuffix + suffix);
            }
        }

        WriteMarker(options, new ReplacementMarker(ReplacementStage.MovingIn, description, now));
        File.Move(stagedPath, database);
        WriteMarker(options, new ReplacementMarker(ReplacementStage.Swapped, description, now));
    }

    /// <summary>The new file validated: drops the set-aside originals and the marker.</summary>
    public static void Complete(StorageOptions options)
    {
        foreach (var suffix in Companions)
        {
            BackupManager.TryDelete(options.DatabasePath + ReplacedSuffix + suffix);
        }

        File.Delete(MarkerPath(options));
    }

    /// <summary>Puts the originals back: deletes whatever new files are in place, then moves the set-aside files home.</summary>
    public static void Revert(StorageOptions options)
    {
        var database = options.DatabasePath;
        var marker = ReadMarker(options);
        var originalsAside = File.Exists(database + ReplacedSuffix);

        // While moving out, anything still in place is an original that was not moved yet.
        if (marker?.Stage != ReplacementStage.MovingOut && originalsAside)
        {
            foreach (var suffix in Companions)
            {
                if (File.Exists(database + suffix))
                {
                    File.Delete(database + suffix);
                }
            }
        }

        foreach (var suffix in Companions)
        {
            if (File.Exists(database + ReplacedSuffix + suffix) && !File.Exists(database + suffix))
            {
                File.Move(database + ReplacedSuffix + suffix, database + suffix);
            }
        }

        BackupManager.TryDelete(StagedPath(options));
        BackupManager.TryDelete(MarkerPath(options));
    }

    /// <summary>
    /// Startup recovery (before any connection opens): an interrupted replacement either
    /// completes, when the new file is fully in place and passes its checks, or is undone.
    /// </summary>
    public static RecoveryNotice? RecoverInterrupted(StorageOptions options)
    {
        var marker = ReadMarker(options);
        if (marker is null && !File.Exists(MarkerPath(options)))
        {
            BackupManager.TryDelete(StagedPath(options));
            return null;
        }

        if (marker?.Stage == ReplacementStage.Swapped && File.Exists(options.DatabasePath) && BackupManager.CheckDatabaseFile(options.DatabasePath).Valid)
        {
            Complete(options);
            return new RecoveryNotice(false, $"Chaos finished the interrupted {marker.Description} when it started again.");
        }

        if (!File.Exists(options.DatabasePath + ReplacedSuffix) && marker?.Stage != ReplacementStage.MovingOut)
        {
            // Nothing was set aside, so what is in place is all there is: keep it.
            BackupManager.TryDelete(StagedPath(options));
            BackupManager.TryDelete(MarkerPath(options));
            return new RecoveryNotice(true, "An interrupted replacement of the data file was found and cleared; your data was left as it was.");
        }

        Revert(options);
        return new RecoveryNotice(true, $"The {marker?.Description ?? "replacement of the data file"} was interrupted, so Chaos kept your data as it was before. You can try again from Settings.");
    }

    private static ReplacementMarker? ReadMarker(StorageOptions options)
    {
        var path = MarkerPath(options);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ReplacementMarker>(File.ReadAllText(path), BackupManager.ManifestJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteMarker(StorageOptions options, ReplacementMarker marker)
    {
        var path = MarkerPath(options);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, marker, BackupManager.ManifestJson);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
