namespace Chaos.Application.Tasks;

/// <summary>
/// Manual ordering (PLAN §6 task operations): sibling ranks are integers spaced 1,024
/// apart. A move lands between its neighbors when a free rank exists (middle of the
/// gap); otherwise the whole sibling set is compacted (renumbered 0, 1024, 2048, …).
/// Ties always sort by ID. This function is pure: callers apply the returned
/// assignments inside one transaction with optimistic concurrency.
/// </summary>
internal static class RankOrdering
{
    public const long Gap = 1024;

    public readonly record struct Assignment(string Id, long NewPosition, long ExpectedVersion);

    /// <param name="siblingsOrdered">All active siblings including the moving row, ordered by (Position, Id).</param>
    /// <param name="movingId">ID of the row being moved.</param>
    /// <param name="insertionIndex">Desired ordinal of the moving row among the siblings once it is removed (0 = first).</param>
    /// <returns>Assignments for the rows whose rank actually changes; empty when the move is a no-op.</returns>
    public static IReadOnlyList<Assignment> Place(
        IReadOnlyList<(string Id, long Position, long Version)> siblingsOrdered,
        string movingId,
        int insertionIndex)
    {
        var others = new List<(string Id, long Position, long Version)>(siblingsOrdered.Count);
        var movingVersion = 0L;
        var currentRank = 0L;
        foreach (var row in siblingsOrdered)
        {
            if (row.Id == movingId)
            {
                movingVersion = row.Version;
                currentRank = row.Position;
                continue;
            }

            others.Add(row);
        }

        if (others.Count == 0)
        {
            // The only sibling in the set: it keeps rank 0.
            return currentRank == 0 ? [] : [new Assignment(movingId, 0, movingVersion)];
        }

        var index = Math.Clamp(insertionIndex, 0, others.Count);
        var previousRank = index > 0 ? others[index - 1].Position : -1L;
        var nextRank = index < others.Count ? others[index].Position : long.MaxValue;

        long? targetRank = null;
        if (index == others.Count)
        {
            // Appending after the last sibling always has room and keeps the 1,024 spacing.
            targetRank = previousRank + Gap;
        }
        else if (nextRank - previousRank >= 2)
        {
            // A free integer rank exists strictly between the neighbors.
            var between = previousRank < currentRank && currentRank < nextRank;
            targetRank = between ? currentRank : previousRank + ((nextRank - previousRank) / 2);
        }

        if (targetRank is not null)
        {
            return targetRank.Value == currentRank
                ? []
                : [new Assignment(movingId, targetRank.Value, movingVersion)];
        }

        // No gap at the insertion point: renumber the whole sibling set in one pass.
        // Slot order: rows before the insertion index keep their relative order, then the
        // moving row, then the remaining rows; every slot lands on a multiple of the gap.
        var assignments = new List<Assignment>(others.Count + 1);
        for (var slot = 0; slot < others.Count + 1; slot++)
        {
            string id;
            long version;
            if (slot == index)
            {
                id = movingId;
                version = movingVersion;
            }
            else
            {
                var row = others[slot < index ? slot : slot - 1];
                id = row.Id;
                version = row.Version;
            }

            var target = slot * Gap;
            if (target != RankOf(siblingsOrdered, id))
            {
                assignments.Add(new Assignment(id, target, version));
            }
        }

        return assignments;
    }

    private static long RankOf(IReadOnlyList<(string Id, long Position, long Version)> rows, string id)
    {
        foreach (var row in rows)
        {
            if (row.Id == id)
            {
                return row.Position;
            }
        }

        throw new InvalidOperationException($"Sibling '{id}' is not part of the ordered set.");
    }
}
