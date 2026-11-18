using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chaos.Server.Tests;

/// <summary>PLAN2 P12 item 2 and §8.1: the EF Core migrations against real PostgreSQL. They create
/// the S001 tables (OpenIddict's included), are idempotent and safe to run concurrently; the
/// startup gate refuses unmigrated and newer databases; the model never drifts from the migrations.</summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class ServerSchemaGateTests(PostgresServer postgres)
{
    private static readonly string[] S001Tables =
    [
        "Accounts", "AccountLogins", "Devices", "EmailCodes", "AuditEvents",
        "OpenIddictApplications", "OpenIddictAuthorizations", "OpenIddictScopes", "OpenIddictTokens",
    ];

    [Test]
    public async Task MigrateAsync_EmptyDatabase_CreatesS001Tables()
    {
        await using var db = ServerDatabase.Open(await postgres.CreateDatabaseAsync());

        var latest = await ServerSchemaGate.MigrateAsync(db, CancellationToken.None);

        await Assert.That(latest).IsEqualTo(db.Database.GetMigrations().Last());
        foreach (var table in S001Tables)
        {
            await Assert.That(await TableExistsAsync(db, table)).IsTrue();
        }
    }

    [Test]
    public async Task MigrateAsync_RunTwice_AppliesEachMigrationOnce()
    {
        await using var db = ServerDatabase.Open(await postgres.CreateDatabaseAsync());

        await ServerSchemaGate.MigrateAsync(db, CancellationToken.None);
        await ServerSchemaGate.MigrateAsync(db, CancellationToken.None);

        await Assert.That((await db.Database.GetAppliedMigrationsAsync()).Count())
            .IsEqualTo(db.Database.GetMigrations().Count());
        await ServerSchemaGate.EnsureCurrentAsync(db, CancellationToken.None);
    }

    [Test]
    public async Task MigrateAsync_ConcurrentMigrators_BothSucceed()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var first = ServerDatabase.Open(connectionString);
        await using var second = ServerDatabase.Open(connectionString);

        await Task.WhenAll(
            ServerSchemaGate.MigrateAsync(first, CancellationToken.None),
            ServerSchemaGate.MigrateAsync(second, CancellationToken.None));

        await ServerSchemaGate.EnsureCurrentAsync(first, CancellationToken.None);
    }

    [Test]
    public async Task EnsureCurrentAsync_UnmigratedDatabase_RefusesAndCreatesNothing()
    {
        await using var db = ServerDatabase.Open(await postgres.CreateDatabaseAsync());

        await Assert.That(() => ServerSchemaGate.EnsureCurrentAsync(db, CancellationToken.None))
            .Throws<ServerSchemaException>();
        await Assert.That(await TableExistsAsync(db, "Accounts")).IsFalse();
    }

    [Test]
    public async Task EnsureCurrentAsync_NewerDatabase_Refuses()
    {
        await using var db = ServerDatabase.Open(await ServerDatabase.CreateMigratedAsync(postgres));
        await db.Database.ExecuteSqlAsync(
            $"""INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES ('29991231000000_S999_Future', '99.0.0');""");

        await Assert.That(() => ServerSchemaGate.EnsureCurrentAsync(db, CancellationToken.None))
            .Throws<ServerSchemaException>();
    }

    [Test]
    public async Task EnsureCurrentAsync_MissingMigration_Refuses()
    {
        await using var db = ServerDatabase.Open(await ServerDatabase.CreateMigratedAsync(postgres));
        var last = db.Database.GetMigrations().Last();
        await db.Database.ExecuteSqlAsync($"""DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = {last};""");

        await Assert.That(() => ServerSchemaGate.EnsureCurrentAsync(db, CancellationToken.None))
            .Throws<ServerSchemaException>();
    }

    [Test]
    public async Task S001_UpperCasePrimaryEmail_IsRejected()
    {
        await using var db = ServerDatabase.Open(await ServerDatabase.CreateMigratedAsync(postgres));
        db.Accounts.Add(new Account { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, PrimaryEmail = "Someone@Example.com" });

        await Assert.That(() => db.SaveChangesAsync()).Throws<DbUpdateException>();
    }

    [Test]
    public async Task S001_DeletingAccount_CascadesToLoginsAndDevicesButKeepsAudit()
    {
        await using var db = ServerDatabase.Open(await ServerDatabase.CreateMigratedAsync(postgres));
        var now = DateTimeOffset.UtcNow;
        var account = new Account { Id = Guid.NewGuid(), CreatedAt = now, PrimaryEmail = "someone@example.com" };
        account.Logins.Add(new AccountLogin { Provider = LoginProviders.Email, Subject = "someone@example.com", LinkedAt = now });
        account.Devices.Add(new Device
        {
            Id = Guid.NewGuid(), Name = "Desk", Platform = "windows", ClientVersion = "0.2.1", ClientSchema = 32, CreatedAt = now, LastSeenAt = now,
        });
        db.Accounts.Add(account);
        db.AuditEvents.Add(new AuditEvent { AccountId = account.Id, Kind = AuditKinds.SignIn, At = now });
        await db.SaveChangesAsync();

        await db.Accounts.Where(a => a.Id == account.Id).ExecuteDeleteAsync();

        await Assert.That(await db.AccountLogins.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.Devices.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.AuditEvents.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task Model_HasNoChangesWithoutAMigration()
    {
        // Needs no connection: compares the model with the last migration's snapshot (PLAN2 §8.1).
        await using var db = ServerDatabase.Open("Host=localhost;Database=unused");

        if (db.Database.HasPendingModelChanges())
        {
            throw new InvalidOperationException(
                "ChaosDbContext has changes with no migration. Add one: " +
                "dotnet tool run dotnet-ef migrations add SNNN_Name --project src/Chaos.Server --output-dir Data/Migrations");
        }
    }

    private static async Task<bool> TableExistsAsync(ChaosDbContext db, string table)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = new NpgsqlCommand("SELECT to_regclass(quote_ident(@t)) IS NOT NULL;", connection);
            command.Parameters.AddWithValue("t", table);
            return await command.ExecuteScalarAsync() is true;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
