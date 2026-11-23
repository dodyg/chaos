using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Tests;

/// <summary>Creates <see cref="ChaosDbContext"/> instances with the service's own provider options.</summary>
internal static class ServerDatabase
{
    public static ChaosDbContext Open(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<ChaosDbContext>();
        ChaosDbContext.Configure(builder, connectionString);
        return new ChaosDbContext(builder.Options);
    }

    /// <summary>A fresh database with every migration applied.</summary>
    public static async Task<string> CreateMigratedAsync(PostgresServer postgres)
    {
        return await postgres.CreateMigratedDatabaseAsync();
    }
}
