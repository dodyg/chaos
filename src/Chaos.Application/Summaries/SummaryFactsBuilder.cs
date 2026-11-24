using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.People;
using Chaos.Application.Planning;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Domain.Planning;
using Chaos.Domain.Summaries;
using Chaos.Domain.Tasks;
using Chaos.Sync;

namespace Chaos.Application.Summaries;

/// <summary>The local dates and UTC boundaries one summary covers, and how far its facts reach.</summary>
internal sealed record SummaryPeriod(SummaryKind Kind, DateOnly Start, DateOnly End, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset CapturedThroughUtc)
{
    /// <summary>
    /// Events strictly before this instant are inside the capture: the period end for a
    /// finished period, or just after the capture instant for one still running.
    /// </summary>
    public DateTimeOffset Cut => CapturedThroughUtc >= EndUtc ? EndUtc : CapturedThroughUtc.AddTicks(1);
}

/// <summary>
/// Builds the structured facts of one summary (PLAN §12) inside one storage session. The past
/// comes from append-only task history, not today's task state: a task's state at an instant
/// is the "before" snapshot of its first history entry at or after that instant, or its
/// current row when nothing changed since. So a completion that was later reopened is still a
/// completion event, a task deleted afterwards still appears with the title it had, and a
/// project's progress is measured at both ends of the period.
/// </summary>
internal sealed class SummaryFactsBuilder
{
    /// <summary>Every list in the facts is bounded so a busy period cannot produce an unbounded row.</summary>
    public const int ListLimit = 200;
    public const int SuggestionLimit = 10;
    public const int ExcerptLength = 160;

    private readonly IDataSession _session;
    private readonly TaskQueries.QueryContext _context;
    private readonly CancellationToken _token;
    private readonly IReadOnlyList<TaskHistoryRecord> _history;
    private readonly Dictionary<string, List<TaskHistoryRecord>> _historyByTask = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskAggregateSnapshot?> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskRecord?> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ContainerRecord?> _containers = new(StringComparer.Ordinal);

    internal SummaryFactsBuilder(IDataSession session, TaskQueries.QueryContext context, IReadOnlyList<TaskHistoryRecord> history, CancellationToken token)
    {
        _session = session;
        _context = context;
        _history = history;
        _token = token;
        foreach (var row in history)
        {
            if (!_historyByTask.TryGetValue(row.TaskId, out var rows))
            {
                _historyByTask[row.TaskId] = rows = [];
            }

            rows.Add(row);
        }
    }

    public static async Task<SummaryFacts> BuildAsync(IDataSession session, TaskQueries.QueryContext context, SummaryPeriod period, CancellationToken token)
    {
        var history = await session.Summaries.ListHistorySinceAsync(context.Space.Id, period.StartUtc, token);
        var builder = new SummaryFactsBuilder(session, context, history, token);
        return period.Kind switch
        {
            SummaryKind.Morning => await builder.MorningAsync(period),
            SummaryKind.Daily => await builder.DailyAsync(period),
            _ => await builder.WeeklyAsync(period),
        };
    }

    // ---------------------------------------------------------------- kinds

    private async Task<SummaryFacts> MorningAsync(SummaryPeriod period)
    {
        var date = period.Start;
        var selected = new List<SummaryTaskFact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (await _session.DayPlans.FindAsync(_context.Profile.Id, date, _token) is { } plan)
        {
            foreach (var item in await _session.DayPlans.ListItemsAsync(plan.Id, includeRemoved: false, _token))
            {
                if (await LiveAsync(item.TaskId) is { IsTerminal: false } task && seen.Add(task.Id))
                {
                    selected.Add(await FactAsync(task));
                }
            }
        }

        if (await WeekResolution.ConfirmedContainingAsync(_session, _context.Profile.Id, date, _token) is { } week)
        {
            foreach (var assignment in (await _session.Weeks.ListAssignmentsAsync(week.Id, _token)).Where(item => item.LocalDate == date))
            {
                if (await LiveAsync(assignment.TaskId) is { IsTerminal: false } task && seen.Add(task.Id))
                {
                    selected.Add(await FactAsync(task));
                }
            }
        }

        var overdue = await _session.Queries.CountAsync(await TaskQueries.ResolveAsync(_session, _context, new TaskQuerySpec { View = TaskView.Overdue }, _token), _token);
        return new SummaryFacts
        {
            Selected = Bounded(selected),
            Deadlines = await DueBetweenAsync(date, date),
            OverdueCount = (int)Math.Min(overdue, int.MaxValue),
            FollowUps = await FollowUpsDueAsync(period.EndUtc),
            Waiting = await WaitingAsync(),
            Delegations = await DelegationsAsync(),
            Week = await WeekProgressAsync(date, period),
            Meetings = await MeetingsAsync(date),
        };
    }

    /// <summary>The day's meetings, only when the profile allows them in summaries.</summary>
    private async Task<IReadOnlyList<MeetingFact>> MeetingsAsync(DateOnly date)
    {
        if (!_context.Profile.IncludeMeetingsInSummaries)
        {
            return [];
        }

        var days = await Chaos.Application.Planning.MeetingPlanning.LoadAsync(_session, _context.Zone, date, date, _token);
        return [.. days.Meetings.Select(item => new MeetingFact(item.Title, item.Date, item.Start, item.End, item.AllDay)).Take(ListLimit)];
    }

    private async Task<SummaryFacts> DailyAsync(SummaryPeriod period)
    {
        var date = period.Start;
        var next = date.AddDays(1);
        var unfinished = await UnfinishedAsync(period, [date]);
        var deadlines = await DueBetweenAsync(next, next);
        var (diary, diaryCount) = await DiaryAsync(period);
        var (spaces, ownInSpaces) = await SpacesAsync(period);
        return new SummaryFacts
        {
            Completions = WithOwn(await CompletionsAsync(period), ownInSpaces),
            Projects = await ProjectsAsync(period),
            Unfinished = unfinished,
            Waiting = await WaitingAsync(),
            Delegations = await DelegationsAsync(),
            Deadlines = deadlines,
            DiaryUpdates = diary,
            DiaryUpdateCount = diaryCount,
            Suggested = await SuggestAsync(unfinished, deadlines, next, next),
            Week = await WeekProgressAsync(date, period),
            Meetings = await MeetingsAsync(next),
            Spaces = spaces,
        };
    }

    private async Task<SummaryFacts> WeeklyAsync(SummaryPeriod period)
    {
        var dates = Enumerable.Range(0, period.End.DayNumber - period.Start.DayNumber + 1).Select(offset => period.Start.AddDays(offset)).ToList();
        var nextStart = period.End.AddDays(1);
        var nextEnd = WeekCalendar.EndOf(nextStart);
        var unfinished = await UnfinishedAsync(period, dates);
        var deadlines = await DueBetweenAsync(nextStart, nextEnd);
        var (diary, diaryCount) = await DiaryAsync(period);
        var (spaces, ownInSpaces) = await SpacesAsync(period);
        return new SummaryFacts
        {
            Completions = WithOwn(await CompletionsAsync(period), ownInSpaces),
            Projects = await ProjectsAsync(period),
            Unfinished = unfinished,
            Waiting = await WaitingAsync(),
            Delegations = await DelegationsAsync(),
            Deadlines = deadlines,
            DiaryUpdates = diary,
            DiaryUpdateCount = diaryCount,
            Suggested = await SuggestAsync(unfinished, deadlines, nextStart, nextEnd),
            Week = await WeekProgressAsync(period.Start, period),
            Spaces = spaces,
        };
    }

    /// <summary>The personal completions with the person's own completions in shared spaces, oldest first: their own work stays in the personal sections.</summary>
    private static IReadOnlyList<CompletionFact> WithOwn(IReadOnlyList<CompletionFact> personal, IReadOnlyList<CompletionFact> ownInSpaces) =>
        ownInSpaces.Count == 0 ? personal : Bounded(personal.Concat(ownInSpaces).OrderBy(item => item.CompletedAtUtc).ToList());

    /// <summary>A transition into Done, whatever the event (completed, a delegation reported done or closed). Restoring a deleted finished task is not one.</summary>
    private bool IsCompletion(TaskHistoryRecord row) =>
        Read(row.SnapshotAfter)?.Task is { Status: TaskItemStatus.Done, DeletedAtUtc: null } && Read(row.SnapshotBefore)?.Task is not { Status: TaskItemStatus.Done };

    // ---------------------------------------------------------------- facts

    /// <summary>Every transition into Done inside the capture, oldest first; with <paramref name="actor"/> only the ones that actor made.</summary>
    private async Task<IReadOnlyList<CompletionFact>> CompletionsAsync(SummaryPeriod period, string? actor = null)
    {
        var completions = new List<CompletionFact>();
        foreach (var row in _history.Where(row => row.EffectiveAtUtc < period.Cut))
        {
            if (!IsCompletion(row) || (actor is not null && !string.Equals(row.Actor, actor, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var after = Read(row.SnapshotAfter)!.Task;

            var reopened = _historyByTask[row.TaskId].Any(later =>
                later.EffectiveAtUtc > row.EffectiveAtUtc && later.EffectiveAtUtc < period.Cut
                && Read(later.SnapshotAfter)?.Task is { DeletedAtUtc: null, IsTerminal: false });
            var stillDone = await StateAtAsync(row.TaskId, period.Cut) is { Status: TaskItemStatus.Done, DeletedAtUtc: null };
            completions.Add(new CompletionFact(await FactAsync(after), row.EffectiveAtUtc, reopened, stillDone));
            if (completions.Count == ListLimit)
            {
                break;
            }
        }

        return completions;
    }

    /// <summary>Projects whose progress changed in the period: Done over noncancelled top-level tasks at both ends.</summary>
    private async Task<IReadOnlyList<ProjectProgressFact>> ProjectsAsync(SummaryPeriod period) => await ProjectsAsync(period, null);

    /// <summary>
    /// Projects whose progress changed in the period: Done over noncancelled top-level tasks at both ends.
    /// With <paramref name="spaceId"/> only projects of that space count (the spaces section, Q14).
    /// </summary>
    private async Task<IReadOnlyList<ProjectProgressFact>> ProjectsAsync(SummaryPeriod period, string? spaceId)
    {
        var touched = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in _history.Where(row => row.EffectiveAtUtc < period.Cut))
        {
            foreach (var task in new[] { Read(row.SnapshotBefore)?.Task, Read(row.SnapshotAfter)?.Task })
            {
                if (task is not null && (spaceId is null || task.SpaceId == spaceId)
                    && await ContainerAsync(task.ContainerId) is { Kind: ContainerKind.Project } project)
                {
                    if (!touched.TryGetValue(project.Id, out var ids))
                    {
                        touched[project.Id] = ids = new HashSet<string>(StringComparer.Ordinal);
                    }

                    ids.Add(task.Id);
                }
            }
        }

        var projects = new List<ProjectProgressFact>();
        foreach (var (projectId, taskIds) in touched.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            // Tasks inside the project now but untouched in the period were there all along.
            foreach (var task in await _session.Tasks.ListByContainerAsync(projectId, _token))
            {
                taskIds.Add(task.Id);
            }

            int doneBefore = 0, countedBefore = 0, doneAfter = 0, countedAfter = 0;
            foreach (var taskId in taskIds)
            {
                var (startDone, startCounted) = Counts(await StateAtAsync(taskId, period.StartUtc), projectId);
                var (endDone, endCounted) = Counts(await StateAtAsync(taskId, period.Cut), projectId);
                doneBefore += startDone;
                countedBefore += startCounted;
                doneAfter += endDone;
                countedAfter += endCounted;
            }

            if (doneBefore != doneAfter || countedBefore != countedAfter)
            {
                var project = await ContainerAsync(projectId);
                projects.Add(new ProjectProgressFact(projectId, project?.Name ?? "", doneBefore, countedBefore, doneAfter, countedAfter));
            }
        }

        return Bounded(projects);
    }

    /// <summary>
    /// What happened in each shared space the person belongs to (PLAN3 9.12): completions, new tasks and newly
    /// blocked work by other members, the open deadlines of the seven days after the period, and how each project
    /// moved. Only what this device holds; the person's own plans and meetings are never here. A space with
    /// nothing to say is left out, unless the person has work of their own there to post as a standup.
    /// Also returns the person's own completions in those spaces, which belong in the personal completions.
    /// </summary>
    private async Task<(IReadOnlyList<SpaceSummaryFact> Spaces, IReadOnlyList<CompletionFact> Own)> SpacesAsync(SummaryPeriod period)
    {
        var me = _context.Profile.Id;
        var facts = new List<SpaceSummaryFact>();
        var own = new List<CompletionFact>();
        foreach (var space in (await _session.Spaces.ListAsync(_token)).Where(space => space.Kind == SpaceKind.Shared && space.DeletedAtUtc is null))
        {
            var people = await _session.People.ListAsync(space.Id, _token);
            if (!people.Any(member => !member.Former && string.Equals(member.ActorId, $"person:{me}", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // The space's own history drives a builder of its own, so progress is measured against that space alone.
            var part = new SummaryFactsBuilder(_session, _context, await _session.Summaries.ListHistorySinceAsync(space.Id, period.StartUtc, _token), _token);
            var mine = (await part.CompletionsAsync(period, $"person:{me}")).Where(item => item.CompletedAtUtc >= period.StartUtc).ToList();
            own.AddRange(mine);
            var completed = await part.ActivityAsync(period, space.Id, (row, _) => part.IsCompletion(row), null, people);
            var added = await part.ActivityAsync(period, space.Id, (row, _) => row.EventKind == "task.created", null, people);
            var blocked = (await part.ActivityAsync(period, space.Id,
                    (row, task) => row.EventKind == "task.status-changed" && task.Status == TaskItemStatus.WaitingOnSomeone, SpaceBlockReason.Waiting, people))
                .Concat(await part.ActivityAsync(period, space.Id, (row, _) => row.EventKind == "task.delegation-decline", SpaceBlockReason.Declined, people))
                .OrderBy(item => item.AtUtc).ThenBy(item => item.Task.TaskId, StringComparer.Ordinal).ToList();
            var deadlines = await part.SpaceDeadlinesAsync(space.Id, period.End.AddDays(1), period.End.AddDays(7), people);
            var projects = await part.ProjectsAsync(period, space.Id);
            if (completed.Count + added.Count + blocked.Count + deadlines.Count + projects.Count == 0
                && mine.Count == 0 && (await part.RowsAsync(new TaskQuerySpec { View = TaskView.AssignedToMe, FilterSpaceId = space.Id })).Count == 0)
            {
                continue;
            }

            facts.Add(new SpaceSummaryFact(space.Id, space.Name, Bounded(completed), Bounded(added), Bounded(blocked), Bounded(deadlines), projects));
        }

        return (Bounded(facts), own);
    }

    /// <summary>
    /// History events in the space that <paramref name="matches"/> by actors other than the person (other people
    /// and agents): the task as it was after the event, the actor's name and when; <paramref name="blockedBy"/>
    /// marks newly blocked work.
    /// </summary>
    private async Task<List<SpaceActivityFact>> ActivityAsync(SummaryPeriod period, string spaceId, Func<TaskHistoryRecord, TaskRecord, bool> matches,
        SpaceBlockReason? blockedBy, IReadOnlyList<SpacePerson> people)
    {
        var myId = Guid.TryParse(_context.Profile.Id, out var parsed) ? parsed : Guid.Empty;
        var directory = people.Select(member => new DirectoryMember(member.ActorId, member.DisplayName)).ToList();
        var result = new List<SpaceActivityFact>();
        foreach (var row in _history.Where(row => row.EffectiveAtUtc >= period.StartUtc && row.EffectiveAtUtc < period.Cut))
        {
            if (Read(row.SnapshotAfter)?.Task is not { } task || task.SpaceId != spaceId || !matches(row, task))
            {
                continue;
            }

            // The person's own work is in the personal sections; here only what others did.
            if (!ActorId.TryParse(row.Actor, out var actor) || actor.Id == myId)
            {
                continue;
            }

            result.Add(new SpaceActivityFact(await FactAsync(task), PeopleDirectory.Resolve(row.Actor, $"person:{_context.Profile.Id}", directory).DisplayName, row.EffectiveAtUtc, blockedBy));
            if (result.Count == ListLimit)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>Open tasks of the space due between two local dates (inclusive), earliest first, with their live assignees' names.</summary>
    private async Task<List<SpaceDeadlineFact>> SpaceDeadlinesAsync(string spaceId, DateOnly from, DateOnly to, IReadOnlyList<SpacePerson> people)
    {
        var deadlines = new List<SpaceDeadlineFact>();
        foreach (var row in (await RowsAsync(new TaskQuerySpec { View = TaskView.Search, FilterSpaceId = spaceId, Due = new LocalDateRange(from, to) }))
            .OrderBy(row => row.Task.DueDate).ThenBy(row => row.Task.DueTime ?? TimeOnly.MaxValue).ThenBy(row => row.Task.Id, StringComparer.Ordinal))
        {
            var names = new List<string>();
            foreach (var assignee in await _session.Tasks.ListAssigneesAsync(row.Task.Id, _token))
            {
                if (assignee.DeletedAtUtc is null)
                {
                    names.Add(people.FirstOrDefault(member => string.Equals(member.ActorId, $"person:{assignee.MemberId}", StringComparison.OrdinalIgnoreCase))?.DisplayName
                        ?? "Unknown person");
                }
            }

            deadlines.Add(new SpaceDeadlineFact(await FactAsync(row.Task), names));
            if (deadlines.Count == ListLimit)
            {
                break;
            }
        }

        return deadlines;
    }

    /// <summary>
    /// The person's own standup for the space (PLAN3 9.12): what they completed in the period, what is planned
    /// (open tasks of the space assigned to them, earliest due first) and what is blocked (their Waiting tasks,
    /// with the person each waits on). Meetings and other people's work stay out. The text is the start of the
    /// editor; the person reviews and changes it before posting.
    /// </summary>
    internal async Task<string> StandupMarkdownAsync(string spaceId, SummaryPeriod period, IReadOnlyList<SpacePerson> people, CancellationToken token)
    {
        var me = $"person:{_context.Profile.Id}";
        var done = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in _history.Where(row => IsCompletion(row) && row.EffectiveAtUtc >= period.StartUtc && row.EffectiveAtUtc < period.Cut
                 && string.Equals(row.Actor, me, StringComparison.OrdinalIgnoreCase)))
        {
            if (Read(row.SnapshotAfter)?.Task is { } task && task.SpaceId == spaceId && seen.Add(task.Id))
            {
                done.Add(task.Title);
            }
        }

        var planned = new List<SummaryTaskFact>();
        var blocked = new List<string>();
        foreach (var row in await RowsAsync(new TaskQuerySpec { View = TaskView.AssignedToMe, FilterSpaceId = spaceId }))
        {
            if (row.Task.Status == TaskItemStatus.WaitingOnSomeone)
            {
                var person = (await _session.Waiting.FindActiveAsync(row.Task.Id, token))?.Person;
                blocked.Add(Item(row.Task.Title, person is null ? null : $"waiting on {person}"));
            }
            else
            {
                planned.Add(await FactAsync(row.Task));
            }
        }

        var headings = StandupTemplates.Headings(_context.Profile.Locale);
        var text = new StringBuilder();
        void Section(string heading, IEnumerable<string> items)
        {
            if (text.Length > 0)
            {
                text.AppendLine().AppendLine();
            }

            text.Append("## ").AppendLine(heading);
            foreach (var item in items)
            {
                text.Append("- ").AppendLine(item);
            }
        }
        Section(headings.Done, done.Select(title => Item(title, null)));
        Section(headings.Planned, planned.OrderBy(task => task.DueDate ?? DateOnly.MaxValue).ThenBy(task => task.Title, StringComparer.OrdinalIgnoreCase)
            .Select(task => Item(task.Title, task.DueDate is { } due ? $"due {due:yyyy-MM-dd}" : null)));
        Section(headings.Blocked, blocked);
        return text.ToString();

        // One list item: the title on one line, with an optional note after it.
        static string Item(string title, string? note) => string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            + (note is null ? string.Empty : $" — {note}");
    }

    /// <summary>
    /// Planned work still open at capture: My Day items of the dates (minus ones the person took
    /// off), confirmed week assignments of the dates (the whole week, pool included, for a weekly
    /// summary) and tasks whose own planned date falls in the period.
    /// </summary>
    private async Task<IReadOnlyList<UnfinishedFact>> UnfinishedAsync(SummaryPeriod period, IReadOnlyList<DateOnly> dates)
    {
        var found = new List<UnfinishedFact>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        async Task OfferAsync(string taskId, UnfinishedSource source)
        {
            if (seen.Contains(taskId) || await StateAtAsync(taskId, period.Cut) is not { DeletedAtUtc: null, IsTerminal: false } task)
            {
                return;
            }

            seen.Add(taskId);
            found.Add(new UnfinishedFact(await FactAsync(task), source));
        }

        foreach (var plan in await _session.DayPlans.ListRangeAsync(_context.Profile.Id, dates[0], dates[^1], _token))
        {
            foreach (var item in await _session.DayPlans.ListItemsAsync(plan.Id, includeRemoved: true, _token))
            {
                if (item.RemovedAtUtc is null || item.RemovedAtUtc >= period.Cut)
                {
                    await OfferAsync(item.TaskId, UnfinishedSource.MyDay);
                }
            }
        }

        var weekly = period.Kind == SummaryKind.Weekly;
        if (await WeekResolution.ConfirmedContainingAsync(_session, _context.Profile.Id, dates[0], _token) is { } week)
        {
            foreach (var assignment in await _session.Weeks.ListAssignmentsAsync(week.Id, _token))
            {
                if (weekly || assignment.LocalDate is { } day && dates.Contains(day))
                {
                    await OfferAsync(assignment.TaskId, UnfinishedSource.Week);
                }
            }
        }

        foreach (var row in await RowsAsync(new TaskQuerySpec { View = TaskView.AssignedToMe, Planned = new LocalDateRange(dates[0], dates[^1]), IncludeArchived = true }))
        {
            await OfferAsync(row.Task.Id, UnfinishedSource.Planned);
        }

        return Bounded(found);
    }

    /// <summary>Open Waiting-on-someone tasks with the person and next follow-up, as they stand when the summary is made.</summary>
    private async Task<IReadOnlyList<WaitingFact>> WaitingAsync()
    {
        var waiting = new List<WaitingFact>();
        foreach (var row in await RowsAsync(new TaskQuerySpec { View = TaskView.Waiting }))
        {
            waiting.Add(new WaitingFact(await FactAsync(row.Task), row.WaitingPerson, row.NextFollowUpAtUtc));
        }

        return waiting;
    }

    private async Task<IReadOnlyList<SummaryTaskFact>> DueBetweenAsync(DateOnly from, DateOnly to)
    {
        var due = new List<SummaryTaskFact>();
        foreach (var row in (await RowsAsync(new TaskQuerySpec { View = TaskView.AssignedToMe, Due = new LocalDateRange(from, to) }))
            .OrderBy(row => row.Task.DueDate).ThenBy(row => row.Task.DueTime ?? TimeOnly.MaxValue).ThenBy(row => row.Task.Id, StringComparer.Ordinal))
        {
            due.Add(await FactAsync(row.Task));
        }

        return due;
    }

    private async Task<(IReadOnlyList<DiaryFact> Entries, int Count)> DiaryAsync(SummaryPeriod period)
    {
        var entries = await _session.Summaries.ListDiaryEntriesAsync(_context.Space.Id, period.StartUtc, period.Cut, _token);
        var facts = new List<DiaryFact>();
        foreach (var entry in entries.Take(ListLimit))
        {
            var task = await CurrentAsync(entry.TaskId);
            facts.Add(new DiaryFact(entry.Id, entry.TaskId, task?.Title ?? "", entry.EntryAtUtc, Excerpt(entry.RawMarkdown)));
        }

        return (facts, entries.Count);
    }

    /// <summary>
    /// A deterministic suggested start for the next day or week: overdue work first, then this
    /// period's unfinished plan, then what is due and what the confirmed week already assigns.
    /// Tasks already chosen for the next My Day are left out. Nothing is added by itself.
    /// </summary>
    private async Task<IReadOnlyList<SuggestionFact>> SuggestAsync(IReadOnlyList<UnfinishedFact> unfinished, IReadOnlyList<SummaryTaskFact> due, DateOnly from, DateOnly to)
    {
        var chosen = new HashSet<string>(StringComparer.Ordinal);
        if (from == to && await _session.DayPlans.FindAsync(_context.Profile.Id, from, _token) is { } nextDay)
        {
            foreach (var item in await _session.DayPlans.ListItemsAsync(nextDay.Id, includeRemoved: false, _token))
            {
                chosen.Add(item.TaskId);
            }
        }

        var suggestions = new List<SuggestionFact>();
        void Offer(SummaryTaskFact task, SuggestionSource reason)
        {
            if (suggestions.Count < SuggestionLimit && chosen.Add(task.TaskId))
            {
                suggestions.Add(new SuggestionFact(task, reason));
            }
        }

        foreach (var row in (await RowsAsync(new TaskQuerySpec { View = TaskView.Overdue }))
            .OrderBy(row => row.Task.DueDate).ThenByDescending(row => row.Task.Priority).ThenBy(row => row.Task.Id, StringComparer.Ordinal))
        {
            Offer(await FactAsync(row.Task), SuggestionSource.Overdue);
        }

        foreach (var item in unfinished)
        {
            Offer(item.Task, SuggestionSource.Unfinished);
        }

        foreach (var task in due)
        {
            Offer(task, SuggestionSource.DueNext);
        }

        if (await WeekResolution.ConfirmedContainingAsync(_session, _context.Profile.Id, from, _token) is { } week)
        {
            foreach (var assignment in await _session.Weeks.ListAssignmentsAsync(week.Id, _token))
            {
                if (assignment.LocalDate is { } day && day >= from && day <= to && await LiveAsync(assignment.TaskId) is { IsTerminal: false } task)
                {
                    Offer(await FactAsync(task), SuggestionSource.PlannedNext);
                }
            }
        }

        return suggestions;
    }

    /// <summary>The confirmed week containing the date, measured at capture: assigned tasks done/cancelled and goal progress.</summary>
    private async Task<WeekProgressFact?> WeekProgressAsync(DateOnly date, SummaryPeriod period)
    {
        if (await WeekResolution.ConfirmedContainingAsync(_session, _context.Profile.Id, date, _token) is not { } week)
        {
            return null;
        }

        int assigned = 0, done = 0, cancelled = 0;
        foreach (var assignment in await _session.Weeks.ListAssignmentsAsync(week.Id, _token))
        {
            if (await StateAtAsync(assignment.TaskId, period.Cut) is not { DeletedAtUtc: null } task)
            {
                continue;
            }

            assigned++;
            done += task.Status == TaskItemStatus.Done ? 1 : 0;
            cancelled += task.Status == TaskItemStatus.Cancelled ? 1 : 0;
        }

        var goals = new List<GoalFact>();
        foreach (var goal in await _session.Weeks.ListGoalsAsync(week.Id, _token))
        {
            int goalDone = 0, counted = 0;
            foreach (var taskId in goal.TaskIds)
            {
                if (await StateAtAsync(taskId, period.Cut) is { DeletedAtUtc: null } task && task.Status != TaskItemStatus.Cancelled)
                {
                    counted++;
                    goalDone += task.Status == TaskItemStatus.Done ? 1 : 0;
                }
            }

            goals.Add(new GoalFact(goal.Id, goal.Title, goal.Outcome, goalDone, counted));
        }

        return new WeekProgressFact(week.WeekStartDate, week.ConfirmedRevision, assigned, done, cancelled, goals);
    }

    /// <summary>Live delegations (Q13): the ones this person handed out, then the ones waiting on them, with their follow-up when it is theirs.</summary>
    private async Task<IReadOnlyList<DelegationFact>> DelegationsAsync()
    {
        var facts = new List<DelegationFact>();
        var followUps = await _session.Waiting.ListActiveFollowUpsAsync(_context.Profile.Id, _token);
        foreach (var side in new[] { DelegationSide.From, DelegationSide.To })
        {
            var rows = side == DelegationSide.From
                ? await _session.Delegations.ListLiveFromAsync(_context.Profile.Id, _token)
                : await _session.Delegations.ListLiveToAsync(_context.Profile.Id, _token);
            foreach (var row in rows)
            {
                if (await LiveAsync(row.TaskId) is not { IsTerminal: false } task) continue;
                var other = side == DelegationSide.From ? row.ToMemberId : row.FromMemberId;
                var person = (await _session.People.ListAsync(task.SpaceId, _token))
                    .FirstOrDefault(member => string.Equals(member.ActorId, "person:" + other, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? "Unknown person";
                var followUp = followUps.FirstOrDefault(rule => string.Equals(rule.DelegationId, row.Id, StringComparison.OrdinalIgnoreCase));
                facts.Add(new DelegationFact(await FactAsync(task), person, side, row.State, row.DueDate, followUp?.DueAtUtc));
                if (facts.Count >= ListLimit) return facts;
            }
        }
        return facts;
    }

    private async Task<IReadOnlyList<FollowUpFact>> FollowUpsDueAsync(DateTimeOffset before)
    {
        var facts = new List<FollowUpFact>();
        foreach (var followUp in await _session.Waiting.ListActiveFollowUpsAsync(_context.Profile.Id, _token))
        {
            if (followUp.DueAtUtc is { } due && due < before && await LiveAsync(followUp.TaskId) is { IsTerminal: false } task)
            {
                var person = (await _session.Waiting.FindActiveAsync(task.Id, _token))?.Person;
                facts.Add(new FollowUpFact(await FactAsync(task), person, due));
            }
        }

        return Bounded(facts);
    }

    /// <summary>Project progress contribution: (done, counted) for one task state.</summary>
    private static (int Done, int Counted) Counts(TaskRecord? task, string projectId) =>
        task is { DeletedAtUtc: null } && task.ContainerId == projectId && task.Status != TaskItemStatus.Cancelled
            ? (task.Status == TaskItemStatus.Done ? 1 : 0, 1)
            : (0, 0);

    // ---------------------------------------------------------------- state

    /// <summary>
    /// The task as it stood just before <paramref name="instant"/>: the before-snapshot of its
    /// first history entry at or after the instant, or its current row (including a tombstone)
    /// when it has not changed since. Null when it did not exist yet.
    /// </summary>
    private async Task<TaskRecord?> StateAtAsync(string taskId, DateTimeOffset instant)
    {
        if (_historyByTask.TryGetValue(taskId, out var rows) && rows.FirstOrDefault(row => row.EffectiveAtUtc >= instant) is { } next)
        {
            return Read(next.SnapshotBefore)?.Task;
        }

        return await CurrentAsync(taskId);
    }

    private async Task<TaskRecord?> CurrentAsync(string taskId)
    {
        if (!_current.TryGetValue(taskId, out var task))
        {
            _current[taskId] = task = await _session.Tasks.GetIncludingDeletedAsync(taskId, _token);
        }

        return task;
    }

    /// <summary>The current row when it is not deleted and its list or project still exists.</summary>
    private async Task<TaskRecord?> LiveAsync(string taskId) =>
        await CurrentAsync(taskId) is { DeletedAtUtc: null } task && await ContainerAsync(task.ContainerId) is { DeletedAtUtc: null } ? task : null;

    private async Task<ContainerRecord?> ContainerAsync(string containerId)
    {
        if (!_containers.TryGetValue(containerId, out var container))
        {
            _containers[containerId] = container = await _session.Containers.GetIncludingDeletedAsync(containerId, _token);
        }

        return container;
    }

    private TaskAggregateSnapshot? Read(string? json)
    {
        if (json is null)
        {
            return null;
        }

        if (!_snapshots.TryGetValue(json, out var snapshot))
        {
            _snapshots[json] = snapshot = StoredJsonFormats.ReadSnapshot<TaskAggregateSnapshot>(json);
        }

        return snapshot;
    }

    private async Task<SummaryTaskFact> FactAsync(TaskRecord task)
    {
        var container = await ContainerAsync(task.ContainerId);
        return new SummaryTaskFact(
            task.Id, task.Title, task.ContainerId, container?.Name ?? "", container?.Kind ?? ContainerKind.List, task.Status, task.Priority,
            task.PlannedDate, task.DueDate, task.DueTime, task.EstimateMinutes);
    }

    private async Task<IReadOnlyList<TaskQueryRow>> RowsAsync(TaskQuerySpec spec) =>
        (await _session.Queries.QueryAsync(await TaskQueries.ResolveAsync(_session, _context, spec with { Limit = TaskQueries.MaxLimit }, _token), _token)).Rows;

    private static IReadOnlyList<T> Bounded<T>(List<T> items) => items.Count <= ListLimit ? items : items[..ListLimit];

    /// <summary>The first words of a diary entry on one line.</summary>
    internal static string Excerpt(string markdown)
    {
        var text = string.Join(' ', markdown.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length <= ExcerptLength)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', ExcerptLength);
        return string.Concat(text.AsSpan(0, cut > ExcerptLength / 2 ? cut : ExcerptLength), "…");
    }
}
