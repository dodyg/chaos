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

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = "chaos_test_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name};", admin);
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
