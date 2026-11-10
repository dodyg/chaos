using Chaos.Application.Abstractions;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>
/// Opens SQLite connections with the connection rules from PLAN §5: foreign keys enabled,
/// bounded busy timeout, synchronous FULL. WAL is a persistent database property set once
/// during initialization. Parameterized commands are the callers' responsibility.
/// </summary>
public sealed class SqliteConnectionSource
{
    public const int BusyTimeoutMilliseconds = 5000;

    private readonly string _connectionString;

    public string DatabasePath { get; }

    public SqliteConnectionSource(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Opens a connection and applies per-connection PRAGMAs.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, $"PRAGMA foreign_keys=ON;");
        Execute(connection, $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};");
        Execute(connection, "PRAGMA synchronous=FULL;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.ExecuteNonQuery();
    }
}
