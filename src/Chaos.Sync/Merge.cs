namespace Chaos.Sync;

/// <summary>One change's operation (PLAN2 §7.1).</summary>
public enum SyncOp
{
    Upsert,
    Purge,
}

/// <summary>The values of one field group: column name → SQLite value (string, long, double, bool-as-long or null).</summary>
public sealed record SyncGroupValues(IReadOnlyDictionary<string, object?> Values)
{
    public static SyncGroupValues Empty { get; } = new(new Dictionary<string, object?>());
}

/// <summary>One field group with the clock it was last changed at.</summary>
public sealed record SyncGroupState(Hlc Hlc, SyncGroupValues Values);

/// <summary>One entity's change: the groups it carries, each with its clock (PLAN2 §7.12).</summary>
public sealed record SyncChange(
    string ChangeId,
    string Kind,
    string EntityKey,
    SyncOp Op,
    IReadOnlyDictionary<string, SyncGroupState> Groups,
    string? ActorId = null)
{
    public static SyncChange Upsert(string changeId, string kind, string entityKey, IReadOnlyDictionary<string, SyncGroupState> groups) =>
        new(changeId, kind, entityKey, SyncOp.Upsert, groups);

    public static SyncChange Purge(string changeId, string kind, string entityKey) =>
        new(changeId, kind, entityKey, SyncOp.Purge, new Dictionary<string, SyncGroupState>());
}

/// <summary>
/// The result of merging one incoming change into the stored entity state: the groups to store,
/// the incoming groups that won, and — for each winning group whose stored value differed — the
/// value it replaced (kept in history, PLAN2 §7.7).
/// </summary>
public sealed record MergeResult(
    bool Purge,
    IReadOnlyDictionary<string, SyncGroupState> Merged,
    IReadOnlyDictionary<string, SyncGroupState> Winners,
    IReadOnlyDictionary<string, SyncGroupValues> Replaced)
{
    public bool Changed => Purge || Winners.Count > 0;
}

/// <summary>
/// Field-group merge, latest clock wins, group by group (PLAN2 §5 decision 4). Pure: both the
/// client applier and the server feed it <paramref name="stored"/> state and get back what to
/// write. Equal clocks are the same device's own change coming back — idempotent, never a win.
/// Groups only one side knows about are kept on both sides.
/// </summary>
public static class Merge
{
    public static MergeResult Apply(IReadOnlyDictionary<string, SyncGroupState>? stored, SyncChange incoming)
    {
        if (incoming.Op == SyncOp.Purge)
        {
            return new MergeResult(true, new Dictionary<string, SyncGroupState>(), new Dictionary<string, SyncGroupState>(), new Dictionary<string, SyncGroupValues>());
        }

        var storedGroups = stored ?? new Dictionary<string, SyncGroupState>();
        var merged = new Dictionary<string, SyncGroupState>(storedGroups, StringComparer.Ordinal);
        var winners = new Dictionary<string, SyncGroupState>(StringComparer.Ordinal);
        var replaced = new Dictionary<string, SyncGroupValues>(StringComparer.Ordinal);

        foreach (var (name, incomingGroup) in incoming.Groups)
        {
            if (!storedGroups.TryGetValue(name, out var storedGroup))
            {
                merged[name] = incomingGroup;
                winners[name] = incomingGroup;
                continue;
            }

            if (incomingGroup.Hlc <= storedGroup.Hlc)
            {
                continue; // Local is newer, or this exact change already applied (equal clocks).
            }

            merged[name] = incomingGroup;
            winners[name] = incomingGroup;
            if (!SameValues(storedGroup.Values, incomingGroup.Values))
            {
                replaced[name] = storedGroup.Values;
            }
        }

        return new MergeResult(false, merged, winners, replaced);
    }

    private static bool SameValues(SyncGroupValues left, SyncGroupValues right)
    {
        if (left.Values.Count != right.Values.Count)
        {
            return false;
        }

        foreach (var (column, value) in left.Values)
        {
            if (!right.Values.TryGetValue(column, out var other) || !Equals(Normalize(value), Normalize(other)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>SQLite reads booleans back as long; compare by value across the numeric kinds.</summary>
    private static object? Normalize(object? value) => value switch
    {
        bool flag => flag ? 1L : 0L,
        int number => (long)number,
        short number => (long)number,
        byte number => (long)number,
        uint number => (long)number,
        _ => value,
    };
}
