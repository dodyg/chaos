using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Boards;

public sealed record BoardColumnDto(string Key, string Title, bool IsNone);

/// <summary>A swimlane: its key and title; the "none" lane holds cards the lane field does not apply to.</summary>
public sealed record BoardLaneDto(string Key, string Title, bool IsNone);

/// <summary>One card: the task plus what the card face shows (PLAN2 §11.1).</summary>
public sealed record BoardCardDto(
    TaskRecord Task,
    IReadOnlyList<string> TagIds,
    int SubtasksTotal,
    int SubtasksDone,
    bool HasIssueLink,
    string ColumnKey,
    string LaneKey);

/// <summary>The whole board: settings, columns, lanes and cards, already assigned to their places.</summary>
public sealed record BoardDto(
    BoardSettingsRecord Settings,
    IReadOnlyList<BoardColumnDto> Columns,
    IReadOnlyList<BoardLaneDto> Lanes,
    IReadOnlyList<BoardCardDto> Cards,
    int TotalCount,
    bool Truncated);

/// <summary>
/// The board read (PLAN2 P2): one container's tasks as columns, swimlanes and cards. Rows come
/// from the board store in the container's manual order; grouping is computed here so columns
/// always agree with the task rows. Cards are capped at 500 with a truncation flag.
/// </summary>
public sealed class BoardQueries(IApplicationStore store)
{
    public const int MaxCards = 500;

    public async Task<BoardDto> GetAsync(string containerId, CapacityCategory? category = null, bool showCancelled = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(containerId))
        {
            throw AppException.Validation("containerId", "Choose a list or project.");
        }

        return await store.QueryAsync(async (session, token) =>
        {
            var container = await session.Containers.GetAsync(containerId, token)
                ?? throw AppException.NotFound("That list or project no longer exists. Refresh and try again.");
            var settings = await session.Boards.GetAsync(BoardTargetKind.Container, container.Id, token)
                ?? BoardCommands.Defaults(BoardTargetKind.Container, container.Id);

            var sections = await session.Sections.ListByContainerAsync(container.Id, token);
            IReadOnlyList<TagRecord> columnTags = settings.GroupBy == BoardGrouping.TagGroup && settings.ColumnTagId is { } parentId
                ? await session.Tags.ListChildrenAsync(parentId, token)
                : [];
            IReadOnlyList<TagRecord> laneTags = settings.SwimlaneBy == BoardSwimlane.TagGroup && settings.SwimlaneTagId is { } laneParentId
                ? await session.Tags.ListChildrenAsync(laneParentId, token)
                : [];

            var columns = ColumnsFor(settings, sections, columnTags, showCancelled);
            var lanes = LanesFor(settings, laneTags);

            var rows = await session.Boards.ListCardsAsync(container.Id, token);
            var links = await session.Boards.ListCardTagsAsync(container.Id, token);
            var tagsByTask = links.GroupBy(link => link.TaskId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.Select(link => link.TagId)], StringComparer.Ordinal);

            var visible = rows
                .Where(row => category is null || row.Task.CapacityCategory == category)
                .Where(row => showCancelled || row.Task.Status != TaskItemStatus.Cancelled)
                .ToList();
            var truncated = visible.Count > MaxCards;
            var cards = new List<BoardCardDto>(Math.Min(visible.Count, MaxCards));
            foreach (var row in visible.Take(MaxCards))
            {
                var tagIds = tagsByTask.GetValueOrDefault(row.Task.Id) ?? [];
                cards.Add(new BoardCardDto(
                    row.Task,
                    tagIds,
                    row.SubtasksTotal,
                    row.SubtasksDone,
                    row.HasIssueLink,
                    ColumnKeyFor(settings, sections, columnTags, row.Task, tagIds),
                    LaneKeyFor(settings, laneTags, row.Task, tagIds)));
            }

            return new BoardDto(settings, columns, lanes, cards, visible.Count, truncated);
        }, ct);
    }

    private static IReadOnlyList<BoardColumnDto> ColumnsFor(
        BoardSettingsRecord settings,
        IReadOnlyList<SectionRecord> sections,
        IReadOnlyList<TagRecord> columnTags,
        bool showCancelled)
    {
        switch (settings.GroupBy)
        {
            case BoardGrouping.Section:
                return [.. sections.Select(section => new BoardColumnDto(section.Id, section.Title, IsNone: false)),
                    new(string.Empty, "No section", IsNone: true)];

            case BoardGrouping.Priority:
                return
                [
                    new(nameof(TaskPriority.Urgent), "Urgent", IsNone: false),
                    new(nameof(TaskPriority.High), "High", IsNone: false),
                    new(nameof(TaskPriority.Normal), "Normal", IsNone: false),
                    new(nameof(TaskPriority.Low), "Low", IsNone: false),
                    new(nameof(TaskPriority.None), "None", IsNone: true),
                ];

            case BoardGrouping.TagGroup:
                return [.. columnTags.Select(tag => new BoardColumnDto(tag.Id, tag.DisplayName, IsNone: false)),
                    new(string.Empty, "None", IsNone: true)];

            default:
                var statusColumns = new List<BoardColumnDto>
                {
                    new(nameof(TaskItemStatus.NotStarted), "Not started", IsNone: false),
                    new(nameof(TaskItemStatus.InProgress), "In progress", IsNone: false),
                    new(nameof(TaskItemStatus.WaitingOnSomeone), "Waiting on someone", IsNone: false),
                    new(nameof(TaskItemStatus.NeedsReview), "Needs review", IsNone: false),
                    new(nameof(TaskItemStatus.Done), "Done", IsNone: false),
                };
                if (showCancelled)
                {
                    statusColumns.Add(new(nameof(TaskItemStatus.Cancelled), "Cancelled", IsNone: false));
                }

                return statusColumns;
        }
    }

    private static IReadOnlyList<BoardLaneDto> LanesFor(BoardSettingsRecord settings, IReadOnlyList<TagRecord> laneTags) =>
        settings.SwimlaneBy switch
        {
            BoardSwimlane.Priority =>
            [
                new(nameof(TaskPriority.Urgent), "Urgent", IsNone: false),
                new(nameof(TaskPriority.High), "High", IsNone: false),
                new(nameof(TaskPriority.Normal), "Normal", IsNone: false),
                new(nameof(TaskPriority.Low), "Low", IsNone: false),
                new(nameof(TaskPriority.None), "None", IsNone: true),
            ],
            BoardSwimlane.TagGroup => [.. laneTags.Select(tag => new BoardLaneDto(tag.Id, tag.DisplayName, IsNone: false)),
                new(string.Empty, "None", IsNone: true)],
            BoardSwimlane.Category =>
            [
                new(nameof(CapacityCategory.Work), "Work", IsNone: false),
                new(nameof(CapacityCategory.Personal), "Personal", IsNone: false),
            ],
            // BoardSwimlane.Container lanes belong to custom views (P3); a container board has one lane.
            _ => [new(string.Empty, "All tasks", IsNone: true)],
        };

    private static string ColumnKeyFor(
        BoardSettingsRecord settings,
        IReadOnlyList<SectionRecord> sections,
        IReadOnlyList<TagRecord> columnTags,
        TaskRecord task,
        IReadOnlyList<string> tagIds)
    {
        switch (settings.GroupBy)
        {
            case BoardGrouping.Section:
                return task.SectionId is { } sectionId && sections.Any(section => section.Id == sectionId) ? sectionId : string.Empty;

            case BoardGrouping.Priority:
                return task.Priority.ToString();

            case BoardGrouping.TagGroup:
                return columnTags.FirstOrDefault(tag => tagIds.Contains(tag.Id))?.Id ?? string.Empty;

            default:
                return task.Status.ToString();
        }
    }

    private static string LaneKeyFor(BoardSettingsRecord settings, IReadOnlyList<TagRecord> laneTags, TaskRecord task, IReadOnlyList<string> tagIds) =>
        settings.SwimlaneBy switch
        {
            BoardSwimlane.Priority => task.Priority.ToString(),
            BoardSwimlane.TagGroup => laneTags.FirstOrDefault(tag => tagIds.Contains(tag.Id))?.Id ?? string.Empty,
            BoardSwimlane.Category => task.CapacityCategory.ToString(),
            _ => string.Empty,
        };
}
