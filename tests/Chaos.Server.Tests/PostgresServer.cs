using Chaos.Server.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using TUnit.Core.Interfaces;

namespace Chaos.Server.Tests;

/// <summary>
/// One PostgreSQL server per test session (PLAN2 §8.8). <c>CHAOS_TEST_PG</c> names an existing
/// server (its user must be allowed to create databases); otherwise Testcontainers starts one in
/// Docker. Each test gets its own fresh database from <see cref="CreateDatabaseAsync"/>.
/// </summary>
public sealed class PostgresServer : IAsyncInitializer, IAsyncDisposable
{
    public const string EnvironmentVariable = "CHAOS_TEST_PG";

    private PostgreSqlContainer? _container;
    private string _adminConnectionString = "";

    public async Task InitializeAsync()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            _adminConnectionString = fromEnvironment;
            return;
        }

        try
        {
            _container = new PostgreSqlBuilder("postgres:18.7").Build();
            await _container.StartAsync();
            _adminConnectionString = _container.GetConnectionString();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Server tests need PostgreSQL. Set {EnvironmentVariable} to a connection string for a server " +
                "where the user may create databases, or start Docker so Testcontainers can run postgres:18.7. " +
                $"Starting the container failed: {ex.Message}", ex);
        }
    }

    private readonly SemaphoreSlim _templateGate = new(1, 1);
    private string? _templateName;

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public Task<string> CreateDatabaseAsync() => CreateAsync(template: null);

    /// <summary>
    /// Creates a database with every migration applied. The migrations run once per session into a template
    /// database; each test then gets a file-level copy of it, which is far cheaper than migrating again.
    /// </summary>
    public async Task<string> CreateMigratedDatabaseAsync()
    {
        if (_templateName is null)
        {
            await _templateGate.WaitAsync();
            try
            {
                if (_templateName is null)
                {
                    var connectionString = await CreateAsync(template: null, namePrefix: "chaos_template_");
                    await using (var db = ServerDatabase.Open(connectionString))
                    {
                        await ServerSchemaGate.MigrateAsync(db, CancellationToken.None);
                    }

                    // CREATE DATABASE ... TEMPLATE needs a template with no open connections.
                    NpgsqlConnection.ClearAllPools();
                    _templateName = new NpgsqlConnectionStringBuilder(connectionString).Database!;
                }
            }
            finally
            {
                _templateGate.Release();
            }
        }

        return await CreateAsync(_templateName);
    }

    private async Task<string> CreateAsync(string? template, string namePrefix = "chaos_test_")
    {
        var name = namePrefix + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            var sql = template is null ? $"CREATE DATABASE {name};" : $"CREATE DATABASE {name} TEMPLATE {template};";
            await using var create = new NpgsqlCommand(sql, admin);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_adminConnectionString) { Database = name, Pooling = false }.ConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }
}
