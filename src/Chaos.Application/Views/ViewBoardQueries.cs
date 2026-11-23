using Chaos.Application.Abstractions;
using Chaos.Application.Boards;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Domain.Tasks;
using Chaos.Domain.Views;
using Chaos.Application.People;

namespace Chaos.Application.Views;

/// <summary>
/// The board layout of a custom view (PLAN2 §11.2): the view's rows as cards in columns and swimlanes.
/// Rows come from the same specification as the list and table layouts, so the cards are exactly the
/// view's tasks; settings are the P2 <c>BoardSettings</c> row with the view as its target. A view spans
/// lists, so it cannot group by section, may use one lane per list, and a drop changes only the column
/// (the view's sort decides the order inside it). Cards are capped at 500 like a list board.
/// </summary>
public sealed class ViewBoardQueries(IApplicationStore store, TaskQueries queries)
{
    public async Task<BoardDto> GetAsync(string viewId, CapacityCategory? category = null, bool showCancelled = false, CancellationToken ct = default)
    {
        var view = await store.QueryAsync(async (session, token) =>
        {
            var found = await session.SavedViews.GetAsync(viewId, token) ?? throw AppException.NotFound("That view no longer exists. Refresh and try again.");
            return found.DeletedAtUtc is null ? found : throw AppException.NotFound("That view was deleted.");
        }, ct);

        var layout = ParseLayout(view.LayoutJson);
        var definition = SavedViewCommands.ParseDefinition(view.DefinitionJson);
        var rows = new List<TaskQueryRow>();
        string? cursor = null;
        long total;
        do
        {
            var page = await queries.RunAsync(new TaskQuerySpec
            {
                View = TaskView.Custom,
                SharedViewSpaceId = view.SpaceId,
                Definition = definition,
                Sort = new ViewSort(layout.SortField, layout.SortDescending),
                Category = category,
                Limit = TaskQueries.MaxLimit,
                Cursor = cursor,
            }, ct);
            rows.AddRange(page.Rows);
            cursor = page.NextCursor;
            total = page.TotalCount;
        }
        while (cursor is not null && rows.Count <= BoardQueries.MaxCards);

        return await store.QueryAsync(async (session, token) =>
        {
            var settings = await session.Boards.GetAsync(BoardTargetKind.View, view.Id, token) ?? BoardCommands.Defaults(BoardTargetKind.View, view.Id);
            // A view never groups by section (that is one list's field); a stale row falls back to status.
            if (settings.GroupBy == BoardGrouping.Section)
            {
                settings = settings with { GroupBy = BoardGrouping.Status };
            }

            IReadOnlyList<TagRecord> columnTags = settings.GroupBy == BoardGrouping.TagGroup && settings.ColumnTagId is { } parentId
                ? await session.Tags.ListChildrenAsync(parentId, token)
                : [];
            IReadOnlyList<TagRecord> laneTags = settings.SwimlaneBy == BoardSwimlane.TagGroup && settings.SwimlaneTagId is { } laneParentId
                ? await session.Tags.ListChildrenAsync(laneParentId, token)
                : [];

            var visible = rows.Where(row => showCancelled || row.Task.Status != TaskItemStatus.Cancelled).ToList();
            var shown = visible.Take(BoardQueries.MaxCards).ToList();
            var extras = await session.Boards.ListCardExtrasAsync([.. shown.Select(row => row.Task.Id)], token);
            var containerLanes = shown
                .GroupBy(row => row.Task.ContainerId, StringComparer.Ordinal)
                .Select(group => (Id: group.Key, Name: group.First().ContainerName))
                .OrderBy(container => container.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var people = new List<SpacePerson>();
            var peopleBySpace = new Dictionary<string, IReadOnlyList<SpacePerson>>(StringComparer.Ordinal);
            if (settings.GroupBy == BoardGrouping.Assignee || settings.SwimlaneBy == BoardSwimlane.Assignee)
            {
                foreach (var spaceId in shown.Select(row => row.Task.SpaceId).Distinct(StringComparer.Ordinal))
                {
                    var members = await session.People.ListAsync(spaceId, token);
                    peopleBySpace[spaceId] = members;
                    people.AddRange(members);
                }
            }
            var columns = BoardQueries.ColumnsFor(settings, [], columnTags, showCancelled, people);
            var lanes = BoardQueries.LanesFor(settings, laneTags, containerLanes, people);
            var cards = new List<BoardCardDto>();
            foreach (var row in shown)
            {
                var extra = extras.GetValueOrDefault(row.Task.Id) ?? new BoardCardExtras(0, 0, false);
                var assignments = settings.GroupBy == BoardGrouping.Assignee || settings.SwimlaneBy == BoardSwimlane.Assignee
                    ? await session.Tasks.ListAssigneesAsync(row.Task.Id, token) : [];
                cards.AddRange(BoardQueries.PlaceCards(settings, [], columnTags, laneTags, row.Task, row.TagIds,
                    extra.SubtasksTotal, extra.SubtasksDone, extra.HasIssueLink, assignments, peopleBySpace.GetValueOrDefault(row.Task.SpaceId) ?? [], containerLanes: true));
            }

            // With every row read the board counts exactly what it can show; past the cap the query's total stands in.
            var shownTotal = cursor is null ? visible.Count : (int)Math.Min(total, int.MaxValue);
            return new BoardDto(settings, columns, lanes, cards, shownTotal, visible.Count > BoardQueries.MaxCards);
        }, ct);
    }

    private static ViewLayout ParseLayout(string json)
    {
        try
        {
            return ViewLayout.Parse(json);
        }
        catch (Chaos.Domain.Validation.DomainValidationException error)
        {
            throw AppException.Validation("layout", error.Message);
        }
    }
}
