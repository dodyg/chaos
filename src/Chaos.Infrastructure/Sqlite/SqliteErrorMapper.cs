using Chaos.Application.Abstractions;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>
/// Maps SQLite failures to the stable AppErrorKind set (PLAN §4). Renderer-facing messages
/// never contain stack traces, SQL or local paths; each failure carries a correlation ID.
/// The original exception is preserved as InnerException for diagnostics only.
/// </summary>
internal static class SqliteErrorMapper
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteReadOnly = 8;
    private const int SqliteIoErr = 10;
    private const int SqliteCorrupt = 11;
    private const int SqliteFull = 13;
    private const int SqliteCantOpen = 14;
    private const int SqliteConstraint = 19;
    private const int SqliteNotADatabase = 26;

    /// <summary>Returns the exception to throw: AppException and cancellation pass through unchanged.</summary>
    public static Exception Map(Exception error) => error switch
    {
        AppException => error,
        OperationCanceledException => error,
        SqliteException sqlite => MapSqlite(sqlite),
        _ => new AppException(
            AppErrorKind.StorageUnavailable,
            "The local database reported an unexpected error. Your data was not changed; try again.",
            innerException: error),
    };

    private static Exception MapSqlite(SqliteException error)
    {
        var (kind, message) = error.SqliteErrorCode switch
        {
            SqliteFull => (AppErrorKind.StorageFull,
                "There is not enough free space on this disk to save your changes. Free up space and try again."),
            SqliteBusy or SqliteLocked => (AppErrorKind.StorageUnavailable,
                "The local database is busy for a moment. Wait a few seconds and try again."),
            SqliteConstraint => (AppErrorKind.Validation,
                "This change breaks a data rule, for example a name that must be unique or a missing related item."),
            SqliteReadOnly or SqliteIoErr or SqliteCantOpen => (AppErrorKind.StorageUnavailable,
                "The local data folder cannot be accessed. Check disk space and permissions, then try again."),
            SqliteCorrupt or SqliteNotADatabase => (AppErrorKind.StorageUnavailable,
                "The local database appears to be damaged or is not a Chaos database. Restore a validated backup."),
            _ => (AppErrorKind.StorageUnavailable,
                "The local database reported an unexpected error. Try again; if it keeps failing, restore a validated backup."),
        };
        return new AppException(kind, message, innerException: error);
    }
}
