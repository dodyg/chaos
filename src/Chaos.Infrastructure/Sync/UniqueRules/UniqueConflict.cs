using Chaos.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sync.UniqueRules;

/// <summary>
/// One unique-index break between an incoming change and a local row (PLAN2 §7.6), as a rule sees
/// it. The winner is the row with the lowest entity key (ordinal string comparison), so every
/// device makes the same choice. The applier supplies the two effects a rule cannot perform on its
/// own: writing the incoming row as a new row, and raising an attention item.
/// </summary>
internal sealed record UniqueConflict(
    SqliteConnection Connection,
    SyncSchema Schema,
    SyncTable Table,
    SyncChange Change,
    IReadOnlyDictionary<string, string> Key,
    string LocalKey,
    DateTimeOffset Wall,
    Func<MergeResult, CancellationToken, Task> WriteNewRowAsync,
    Action<string, string?, string?, string> Attention)
{
    public string IncomingKey => Change.EntityKey;

    public bool IncomingWins => string.CompareOrdinal(IncomingKey, LocalKey) <= 0;

    public string WinnerKey => IncomingWins ? IncomingKey : LocalKey;

    public string LoserKey => IncomingWins ? LocalKey : IncomingKey;

    /// <summary>The highest clock among the change's groups: the deterministic clock rule side effects carry.</summary>
    public string ClockText => Change.Groups.Values.Select(group => group.Hlc).Max().Encode();
}
