namespace Chaos.Domain.GitHub;

public sealed record ChecklistBase(Guid SubtaskId, int Ordinal, string Text);
public sealed record ChecklistMatch(ChecklistItem Item, Guid? SubtaskId);
public sealed record ChecklistReconciliation(IReadOnlyList<ChecklistMatch> Items, IReadOnlyList<Guid> RemovedSubtaskIds);

public static class ChecklistReconciler
{
    public static ChecklistReconciliation Match(IReadOnlyList<ChecklistBase> previous, IReadOnlyList<ChecklistItem> current)
    {
        var available = previous.ToList();
        var matches = new ChecklistBase?[current.Count];
        for (var pass = 0; pass < 3; pass++)
        {
            for (var index = 0; index < current.Count; index++)
            {
                if (matches[index] is not null) continue;
                var item = current[index];
                var found = available.FindIndex(candidate => pass switch
                {
                    0 => candidate.Ordinal == item.Ordinal && candidate.Text == item.Text,
                    1 => candidate.Text == item.Text,
                    _ => candidate.Ordinal == item.Ordinal,
                });
                if (found < 0) continue;
                matches[index] = available[found];
                available.RemoveAt(found);
            }
        }
        return new(current.Select((item, index) => new ChecklistMatch(item, matches[index]?.SubtaskId)).ToArray(),
            available.Select(item => item.SubtaskId).ToArray());
    }
}
