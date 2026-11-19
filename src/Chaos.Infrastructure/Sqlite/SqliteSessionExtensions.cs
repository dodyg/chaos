using Chaos.Application.Abstractions;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public static class SqliteSessionExtensions
{
    /// <summary>
    /// The raw SQLite connection behind a session. Infrastructure code that runs SQL directly goes through this one
    /// accessor; it fails with a clear message if the session is not SQLite-backed (for example a recording wrapper).
    /// </summary>
    public static SqliteConnection Connection(this IDataSession session) =>
        session is SqliteDataSession sqlite
            ? sqlite.Connection
            : throw new InvalidOperationException($"Raw SQL needs a SQLite session, but this one is {session.GetType().Name}.");
}
