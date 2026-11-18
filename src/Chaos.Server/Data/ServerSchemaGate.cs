using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Data;

/// <summary>
/// Migration command and startup gate (PLAN2 §8.1, §8.7). <c>Chaos.Server migrate</c> applies the
/// EF Core migrations; the web host never migrates and refuses to start when any known migration
/// is missing or the database has one this build does not know (a newer database).
/// </summary>
public static class ServerSchemaGate
{
    public static async Task<string?> MigrateAsync(ChaosDbContext db, CancellationToken ct)
    {
        // EF Core takes a database-wide migration lock, so two migrators starting together are safe.
        await db.Database.MigrateAsync(ct);
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
