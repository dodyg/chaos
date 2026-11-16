using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;

namespace Chaos.Application.Views;

/// <summary>
/// The tasks a view matches right now, and the silent baseline made from them (PLAN2 §11.2:
/// "when alerts are switched on, the current matches are stored without notifying").
/// </summary>
internal static class ViewAlertBaseline
{
    /// <summary>At most this many matches are tracked per view; a view broader than this cannot alert usefully.</summary>
    public const int MaxTracked = 5000;

    public static async Task<IReadOnlyList<string>> CurrentMatchesAsync(
        IDataSession session, SavedViewRecord view, DateTimeOffset now, CancellationToken token)
    {
        var context = await TaskQueries.ContextAsync(session, now, token);
        var spec = new TaskQuerySpec
        {
            View = Chaos.Application.Contracts.TaskView.Custom,
            Definition = SavedViewCommands.ParseDefinition(view.DefinitionJson),
        };
        var criteria = await TaskQueries.ResolveAsync(session, context, spec, token);
        var page = await session.Queries.QueryAsync(criteria with { Limit = MaxTracked, Cursor = null }, token);
        return [.. page.Rows.Select(row => row.Task.Id)];
    }

    public static async Task StoreAsync(IDataSession session, SavedViewRecord view, DateTimeOffset now, CancellationToken token)
    {
        var ids = await CurrentMatchesAsync(session, view, now, token);
        await session.SavedViews.ReplaceMatchesAsync(view.Id, [.. ids.Select(id => new SavedViewMatchRecord(view.Id, id, now, null))], token);
    }
}
