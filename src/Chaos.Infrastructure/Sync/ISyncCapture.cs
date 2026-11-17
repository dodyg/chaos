using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sync;

/// <summary>
/// The flush hook of PLAN2 §7.3: the serialized writer calls it in the write transaction,
/// immediately before commit. It turns the rows the capture triggers pended into outbox
/// changes (with fresh hybrid clocks) while sync is enabled, and just clears them while sync
/// is disabled, so offline-only devices pay almost nothing. Everything runs in the user's
/// transaction: a rolled-back change produces no outbox row.
/// </summary>
public interface ISyncCapture
{
    void Flush(SqliteConnection connection);
}
