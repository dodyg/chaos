using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Planning;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record WeekTaskDto(TaskResponse Task, string ContainerName, bool ContainerArchived);
public sealed record WeekPlacementDto(string TaskId, string? Day, string Origin);
public sealed record WeekGoalDto(string Id, string Title, string? ProjectId, string? ProjectName, IReadOnlyList<string> TaskIds, string Outcome, int DoneCount, int CountedCount, string Version);
public sealed record WeekDayDto(string Date, int AvailableMinutes, int? WorkAllowanceMinutes, bool IsOverride, int PlannedMinutes, int WorkMinutes, int UnknownEstimates, int OpenCount, bool OverTotal, bool OverWork);
public sealed record WeekLoadDto(int PlannedMinutes, int WorkMinutes, int UnknownEstimates, int OpenCount);
public sealed record WeekBoardDto(IReadOnlyList<WeekGoalDto> Goals, IReadOnlyList<WeekPlacementDto> Placements, IReadOnlyList<WeekDayDto> Days, WeekLoadDto Pool);
public sealed record WeekDraftGoalDto(string Id, string Title, string? ProjectId, IReadOnlyList<string> TaskIds, string Outcome);
/// <summary>A task in the draft week; a null day is the unassigned pool.</summary>
public sealed record WeekDraftAssignmentDto(string TaskId, string? Day);
public sealed record WeekDraftLeftoverDto(string TaskId, string Decision, string? LaterDate);
public sealed record WeekDraftOverrideDto(string Date, int TotalMinutes, int? WorkMinutes);
public sealed record WeekDraftDto(IReadOnlyList<WeekDraftGoalDto> Goals, IReadOnlyList<WeekDraftAssignmentDto> Assignments, IReadOnlyList<WeekDraftLeftoverDto> Leftovers, IReadOnlyList<WeekDraftOverrideDto> Overrides, int Step);
/// <param name="JournalSummaryId">The stored weekly summary the completion facts came from; null when they are live.</param>
/// <param name="JournalCompleted">Completed tasks with the titles the summary captured (journal facts only).</param>
public sealed record WeekLookBackDto(string WeekStart, string WeekEnd, bool HadPlan, IReadOnlyList<string> CompletedTaskIds, int CompletedCount, int AssignedCount, int AssignedDone, int AssignedCancelled, IReadOnlyList<string> UnfinishedTaskIds, IReadOnlyList<WeekGoalDto> Goals,
    string? JournalSummaryId, IReadOnlyList<SummaryTaskDto> JournalCompleted);
public sealed record WeekCandidateDto(string TaskId, string Reason);
public sealed record WeekGoalSuggestionDto(string Title, string? ProjectId, IReadOnlyList<string> TaskIds, string Reason);
public sealed record DayReconciliationDto(string Date, string WeekPlanId, string Revision, IReadOnlyList<string> AddTaskIds, IReadOnlyList<string> RemoveTaskIds);
public sealed record WeekSlipDto(string TaskId, string Day);
public sealed record WeekCheckInDto(bool Midweek, IReadOnlyList<WeekSlipDto> Slipped, IReadOnlyList<string> OverdueTaskIds, IReadOnlyList<string> OverloadedDays);
public sealed record WeekOverviewDto(
    string WeekStart, string WeekEnd, string Today, string FirstWeekday, string? PlanId, string? State, string ConfirmedRevision, string? ConfirmedAtUtc,
    bool Editable, bool AvailabilityConfigured, WeekBoardDto Confirmed, WeekDraftDto? Draft, string DraftRevision, WeekBoardDto? DraftBoard,
    WeekLookBackDto LookBack, IReadOnlyList<WeekCandidateDto> Candidates, IReadOnlyList<WeekGoalSuggestionDto> GoalSuggestions,
    IReadOnlyList<DayReconciliationDto> PendingReconciliation, WeekCheckInDto? CheckIn, IReadOnlyList<WeekTaskDto> Tasks);

/// <summary>A yyyy-MM-dd local date inside the wanted week, or null for this week.</summary>
public sealed record WeekOpenRequest(string? Date);
public sealed record WeekOpenResponse(bool Ok, WeekOverviewDto? Week, TaskErrorResponse? Error);
public sealed record WeekStartDraftRequest(string OperationId, string WeekStart);
public sealed record WeekSaveDraftRequest(string OperationId, string WeekStart, string ExpectedDraftRevision, WeekDraftDto Draft);
public sealed record WeekRevisionRequest(string OperationId, string WeekStart, string ExpectedDraftRevision);
public sealed record WeekReconcileRequest(string OperationId, string Date, string WeekPlanId, string Revision, IReadOnlyList<string> AddTaskIds, IReadOnlyList<string> RemoveTaskIds);
public sealed record WeekGoalOutcomeRequest(string OperationId, string GoalId, string ExpectedVersion, string Outcome);
public sealed record AvailabilityDayDto(string Weekday, int TotalMinutes, int? WorkMinutes);
public sealed record WeekAvailabilityRequest(string OperationId, IReadOnlyList<AvailabilityDayDto> Days);
public sealed record WeekChangeResponse(bool Ok, WeekOverviewDto? Week, bool Replayed, TaskErrorResponse? Error);
public sealed record WeekStartPreviewRequest(string FirstWeekday);
public sealed record WeekStartConflictDto(string WeekStart, string State, bool HasDraft, int AssignmentCount, int GoalCount);
public sealed record WeekStartPreviewResponse(bool Ok, string? Current, string? Proposed, IReadOnlyList<WeekStartConflictDto> Conflicts, string? NewCurrentWeekStart, TaskErrorResponse? Error);
public sealed record WeekStartChangeRequest(string OperationId, string FirstWeekday, bool Remap);
public sealed record WeekStartChangeResponse(bool Ok, string? FirstWeekday, IReadOnlyList<string> DraftWeekStarts, bool Replayed, TaskErrorResponse? Error);

/// <summary>The weekly planning ritual, goals, availability, day reconciliation and week-start changes (PLAN §10, WP9).</summary>
[NeoRpcService("myWeek", Version = 1)]
public sealed class MyWeekRpcService(MyWeekCommands weeks)
{
    [NeoRpcMethod("open")]
    public async ValueTask<WeekOpenResponse> OpenAsync(WeekOpenRequest request, CancellationToken ct)
    {
        try { return new(true, Map(await weeks.OpenAsync(OptionalDate(request.Date, "date"), ct)), null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("startDraft")]
    public ValueTask<WeekChangeResponse> StartDraftAsync(WeekStartDraftRequest request, CancellationToken ct) =>
        Change(() => weeks.StartDraftAsync(new(request.OperationId, Date(request.WeekStart, "weekStart")), ct));

    [NeoRpcMethod("saveDraft")]
    public ValueTask<WeekChangeResponse> SaveDraftAsync(WeekSaveDraftRequest request, CancellationToken ct) =>
        Change(() => weeks.SaveDraftAsync(new(request.OperationId, Date(request.WeekStart, "weekStart"), Revision(request.ExpectedDraftRevision, "expectedDraftRevision"), ToDraft(request.Draft)), ct));

    [NeoRpcMethod("discardDraft")]
    public ValueTask<WeekChangeResponse> DiscardDraftAsync(WeekRevisionRequest request, CancellationToken ct) =>
        Change(() => weeks.DiscardDraftAsync(new(request.OperationId, Date(request.WeekStart, "weekStart"), Revision(request.ExpectedDraftRevision, "expectedDraftRevision")), ct));

    [NeoRpcMethod("confirm")]
    public ValueTask<WeekChangeResponse> ConfirmAsync(WeekRevisionRequest request, CancellationToken ct) =>
        Change(() => weeks.ConfirmAsync(new(request.OperationId, Date(request.WeekStart, "weekStart"), Revision(request.ExpectedDraftRevision, "expectedDraftRevision")), ct));

    [NeoRpcMethod("reconcile")]
    public ValueTask<WeekChangeResponse> ReconcileAsync(WeekReconcileRequest request, CancellationToken ct) =>
        Change(() => weeks.ApplyReconciliationAsync(new(request.OperationId, Date(request.Date, "date"), request.WeekPlanId, Revision(request.Revision, "revision"), request.AddTaskIds ?? [], request.RemoveTaskIds ?? []), ct));

    [NeoRpcMethod("setGoalOutcome")]
    public ValueTask<WeekChangeResponse> SetGoalOutcomeAsync(WeekGoalOutcomeRequest request, CancellationToken ct) =>
        Change(() => weeks.SetGoalOutcomeAsync(new(request.OperationId, request.GoalId, Revision(request.ExpectedVersion, "expectedVersion"), Enum<WeekGoalOutcome>(request.Outcome, "outcome")), ct));

    [NeoRpcMethod("setAvailability")]
    public ValueTask<WeekChangeResponse> SetAvailabilityAsync(WeekAvailabilityRequest request, CancellationToken ct) =>
        Change(() => weeks.SetAvailabilityAsync(new(request.OperationId,
            [.. (request.Days ?? []).Select((day, index) => new AvailabilityDay(Enum<DayOfWeek>(day.Weekday, $"days[{index}].weekday"), day.TotalMinutes, day.WorkMinutes))]), ct));

    [NeoRpcMethod("previewWeekStart")]
    public async ValueTask<WeekStartPreviewResponse> PreviewWeekStartAsync(WeekStartPreviewRequest request, CancellationToken ct)
    {
        try
        {
            var preview = await weeks.PreviewWeekStartAsync(Enum<DayOfWeek>(request.FirstWeekday, "firstWeekday"), ct);
            return new(true, preview.Current.ToString(), preview.Proposed.ToString(),
                [.. preview.Conflicts.Select(item => new WeekStartConflictDto(StorageFormats.Date(item.WeekStart), item.State.ToString(), item.HasDraft, item.AssignmentCount, item.GoalCount))],
                StorageFormats.Date(preview.NewCurrentWeekStart), null);
        }
        catch (AppException error) { return new(false, null, null, [], null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("changeWeekStart")]
    public async ValueTask<WeekStartChangeResponse> ChangeWeekStartAsync(WeekStartChangeRequest request, CancellationToken ct)
    {
        try
        {
            var result = await weeks.ChangeWeekStartAsync(new(request.OperationId, Enum<DayOfWeek>(request.FirstWeekday, "firstWeekday"), request.Remap), ct);
            return new(true, result.FirstWeekday.ToString(), [.. result.DraftWeekStarts.Select(StorageFormats.Date)], result.Replayed, null);
        }
        catch (AppException error) { return new(false, null, [], false, TaskRpcService.ToErrorResponse(error)); }
    }

    private static async ValueTask<WeekChangeResponse> Change(Func<Task<WeekChangeResult>> run)
    {
        try
        {
            var result = await run();
            return new(true, Map(result.Week), result.Replayed, null);
        }
        catch (AppException error) { return new(false, null, false, TaskRpcService.ToErrorResponse(error)); }
    }

    private static WeekDraft ToDraft(WeekDraftDto? draft)
    {
        if (draft is null)
        {
            throw AppException.Validation("draft", "The draft is missing.");
        }

        return new WeekDraft(
            [.. (draft.Goals ?? []).Select((goal, index) => new WeekDraftGoal(goal.Id, goal.Title, goal.ProjectId, goal.TaskIds ?? [], Enum<WeekGoalOutcome>(goal.Outcome, $"goals[{index}].outcome")))],
            [.. (draft.Assignments ?? []).Select((item, index) => new WeekDraftAssignment(item.TaskId, OptionalDate(item.Day, $"assignments[{index}].day")))],
            [.. (draft.Leftovers ?? []).Select((item, index) => new WeekDraftLeftover(item.TaskId, Enum<LeftoverDecision>(item.Decision, $"leftovers[{index}].decision"), OptionalDate(item.LaterDate, $"leftovers[{index}].laterDate")))],
            [.. (draft.Overrides ?? []).Select((item, index) => new WeekDraftOverride(Date(item.Date, $"overrides[{index}].date"), item.TotalMinutes, item.WorkMinutes))],
            draft.Step);
    }

    private static WeekOverviewDto Map(WeekOverview week) => new(
        StorageFormats.Date(week.WeekStart),
        StorageFormats.Date(week.WeekEnd),
        StorageFormats.Date(week.Today),
        week.FirstWeekday.ToString(),
        week.PlanId,
        week.State?.ToString(),
        Text(week.ConfirmedRevision),
        week.ConfirmedAtUtc is { } at ? StorageFormats.Instant(at) : null,
        week.Editable,
        week.AvailabilityConfigured,
        Map(week.Confirmed),
        week.Draft is { } draft ? new WeekDraftDto(
            [.. draft.Goals.Select(goal => new WeekDraftGoalDto(goal.Id, goal.Title, goal.ProjectId, goal.TaskIds, goal.Outcome.ToString()))],
            [.. draft.Assignments.Select(item => new WeekDraftAssignmentDto(item.TaskId, OptionalText(item.Day)))],
            [.. draft.Leftovers.Select(item => new WeekDraftLeftoverDto(item.TaskId, item.Decision.ToString(), OptionalText(item.LaterDate)))],
            [.. draft.Overrides.Select(item => new WeekDraftOverrideDto(StorageFormats.Date(item.Date), item.TotalMinutes, item.WorkMinutes))],
            draft.Step) : null,
        Text(week.DraftRevision),
        week.DraftBoard is { } board ? Map(board) : null,
        new WeekLookBackDto(
            StorageFormats.Date(week.LookBack.WeekStart), StorageFormats.Date(week.LookBack.WeekEnd), week.LookBack.HadPlan,
            week.LookBack.CompletedTaskIds, (int)Math.Min(week.LookBack.CompletedCount, int.MaxValue), week.LookBack.AssignedCount, week.LookBack.AssignedDone,
            week.LookBack.AssignedCancelled, week.LookBack.UnfinishedTaskIds, [.. week.LookBack.Goals.Select(Map)],
            week.LookBack.JournalSummaryId, [.. (week.LookBack.JournalCompleted ?? []).Select(JournalRpcService.Map)]),
        [.. week.Candidates.Select(item => new WeekCandidateDto(item.TaskId, item.Reason.ToString()))],
        [.. week.GoalSuggestions.Select(item => new WeekGoalSuggestionDto(item.Title, item.ProjectId, item.TaskIds, item.Reason.ToString()))],
        [.. week.PendingReconciliation.Select(item => new DayReconciliationDto(StorageFormats.Date(item.Date), item.WeekPlanId, Text(item.Revision), item.AddTaskIds, item.RemoveTaskIds))],
        week.CheckIn is { } checkIn ? new WeekCheckInDto(
            checkIn.Midweek,
            [.. checkIn.Slipped.Select(item => new WeekSlipDto(item.TaskId, StorageFormats.Date(item.Day)))],
            checkIn.OverdueTaskIds,
            [.. checkIn.OverloadedDays.Select(StorageFormats.Date)]) : null,
        [.. week.Tasks.Select(item => new WeekTaskDto(TaskRpcService.ToResponse(item.Task), item.ContainerName, item.ContainerArchived))]);

    private static WeekBoardDto Map(WeekBoard board) => new(
        [.. board.Goals.Select(Map)],
        [.. board.Placements.Select(item => new WeekPlacementDto(item.TaskId, OptionalText(item.Day), item.Origin.ToString()))],
        [.. board.Days.Select(day => new WeekDayDto(StorageFormats.Date(day.Date), day.Availability.TotalMinutes, day.Availability.WorkMinutes, day.IsOverride,
            day.Load.PlannedMinutes, day.Load.WorkMinutes, day.Load.UnknownEstimates, day.Load.OpenCount, day.Load.OverTotal, day.Load.OverWork))],
        new WeekLoadDto(board.Pool.PlannedMinutes, board.Pool.WorkMinutes, board.Pool.UnknownEstimates, board.Pool.OpenCount));

    private static WeekGoalDto Map(WeekGoalProgress goal) =>
        new(goal.Id, goal.Title, goal.ProjectId, goal.ProjectName, goal.TaskIds, goal.Outcome.ToString(), goal.DoneCount, goal.CountedCount, Text(goal.Version));

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? OptionalText(DateOnly? date) => date is { } value ? StorageFormats.Date(value) : null;

    private static long Revision(string? value, string field) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= 0 ? result : throw AppException.Validation(field, "Invalid revision. Refresh My Week and try again.");

    private static T Enum<T>(string? value, string field) where T : struct, System.Enum =>
        System.Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && System.Enum.IsDefined(parsed) && !int.TryParse(value, out _)
            ? parsed
            : throw AppException.Validation(field, "Choose a valid value.");

    private static DateOnly Date(string? value, string field) =>
        OptionalDate(value, field) ?? throw AppException.Validation(field, "Choose a date.");

    private static DateOnly? OptionalDate(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null
        : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
        : throw AppException.Validation(field, "Enter a valid date.");
}
