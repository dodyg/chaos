using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Data;

/// <summary>
/// Migration command and startup gate (PLAN2 §8.1, §8.7). <c>Chaos.Server migrate</c> applies the
/// EF Core migrations; the web host never migrates and refuses to start when any known migration
/// is missing or the database has one this build does not know (a newer database).
/// </summary>
public static class ServerSchemaGate
{
    /// <summary>Held on one connection for the whole migration, so migrators starting together run one after another.</summary>
    private const long MigrationLockKey = 0x43686F73_4D696772; // "ChosMigr"

    public static async Task<string?> MigrateAsync(ChaosDbContext db, CancellationToken ct)
    {
        // EF Core's own migration lock did not stop two migrators applying the same second migration, so
        // the whole run holds a session-level advisory lock; the loser then finds everything applied.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_lock({MigrationLockKey})", ct);
            try
            {
                await db.Database.MigrateAsync(ct);
            }
            finally
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({MigrationLockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();
    }

    public static async Task EnsureCurrentAsync(ChaosDbContext db, CancellationToken ct)
    {
        var known = db.Database.GetMigrations().ToList();
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();

        var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
        if (unknown.Count > 0)
        {
            throw new ServerSchemaException(
                $"The database has migrations this server does not know ({string.Join(", ", unknown)}). " +
                "It was migrated by a newer server version; deploy that version.");
        }

        var pending = known.Except(applied, StringComparer.Ordinal).ToList();
        if (pending.Count > 0)
        {
            throw new ServerSchemaException(
                $"The database is missing migrations ({string.Join(", ", pending)}). Run `Chaos.Server migrate` first.");
        }
    }
}

public sealed class ServerSchemaException(string message) : Exception(message);
