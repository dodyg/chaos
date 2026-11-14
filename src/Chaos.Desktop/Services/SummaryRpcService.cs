using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Summaries;
using Chaos.Domain.Summaries;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record SummaryTaskDto(string TaskId, string Title, string ContainerId, string ContainerName, string ContainerKind, string Status, string Priority,
    string? PlannedDate, string? DueDate, string? DueTime, int? EstimateMinutes);
public sealed record CompletionDto(SummaryTaskDto Task, string CompletedAtUtc, bool ReopenedAfter, bool StillDone);
public sealed record ProjectProgressDto(string ProjectId, string Name, int DoneBefore, int CountedBefore, int DoneAfter, int CountedAfter);
public sealed record UnfinishedDto(SummaryTaskDto Task, string Source);
public sealed record WaitingFactDto(SummaryTaskDto Task, string? Person, string? FollowUpDueAtUtc);
public sealed record DiaryFactDto(string EntryId, string TaskId, string TaskTitle, string EntryAtUtc, string Excerpt);
public sealed record SuggestionDto(SummaryTaskDto Task, string Reason);
public sealed record GoalFactDto(string GoalId, string Title, string Outcome, int DoneCount, int CountedCount);
public sealed record WeekProgressDto(string WeekStart, string Revision, int AssignedCount, int DoneCount, int CancelledCount, IReadOnlyList<GoalFactDto> Goals);
public sealed record FollowUpFactDto(SummaryTaskDto Task, string? Person, string DueAtUtc);
public sealed record SummaryFactsDto(
    IReadOnlyList<CompletionDto> Completions, IReadOnlyList<ProjectProgressDto> Projects, IReadOnlyList<UnfinishedDto> Unfinished,
    IReadOnlyList<WaitingFactDto> Waiting, IReadOnlyList<SummaryTaskDto> Deadlines, IReadOnlyList<DiaryFactDto> DiaryUpdates, int DiaryUpdateCount,
    IReadOnlyList<SuggestionDto> Suggested, WeekProgressDto? Week, IReadOnlyList<SummaryTaskDto> Selected, int OverdueCount, IReadOnlyList<FollowUpFactDto> FollowUps);
public sealed record SummaryCountsDto(int CompletionEvents, int StillDone, int Unfinished, int DiaryUpdates, int Deadlines, int Waiting, int Selected, bool Quiet);
/// <param name="Partial">Generated before the period ended; <see cref="CapturedThroughUtc"/> says how far it reaches.</param>
public sealed record SummaryHeaderDto(string Id, string Kind, string PeriodStart, string PeriodEnd, string Revision, string CapturedThroughUtc, bool Partial,
    string Trigger, string GeneratedAtUtc, string ZoneId, SummaryCountsDto Counts);
public sealed record SummaryRevisionDto(string Id, string Revision, string GeneratedAtUtc, string CapturedThroughUtc, string Trigger);
public sealed record SummaryDetailDto(SummaryHeaderDto Header, SummaryFactsDto Facts, IReadOnlyList<SummaryRevisionDto> Revisions, bool IsLatest);

public sealed record SummaryListRequest(string? Cursor, int? Limit);
public sealed record SummaryListResponse(bool Ok, IReadOnlyList<SummaryHeaderDto> Entries, string? NextCursor, TaskErrorResponse? Error);
public sealed record SummaryGetRequest(string SummaryId);
/// <summary>The newest summary of the period containing the date (kind Morning, Daily or Weekly).</summary>
public sealed record SummaryFindRequest(string Kind, string Date);
public sealed record SummaryDetailResponse(bool Ok, SummaryDetailDto? Summary, TaskErrorResponse? Error);
public sealed record SummaryGenerateRequest(string OperationId, string Kind, string Date);
public sealed record SummaryPromptsRequest();
public sealed record SummaryPromptsResponse(bool Ok, IReadOnlyList<SummaryHeaderDto> Prompts, TaskErrorResponse? Error);
public sealed record SummaryDismissRequest(string SummaryId);
public sealed record SummaryDismissResponse(bool Ok, TaskErrorResponse? Error);
public sealed record CarryOverPreviewRequest(string SummaryId);
public sealed record CarryOverCandidateDto(SummaryTaskDto Task, bool StillOpen, bool AlreadyPlanned);
public sealed record CarryOverPreviewResponse(bool Ok, string? Target, string? TargetDate, IReadOnlyList<CarryOverCandidateDto> Candidates, TaskErrorResponse? Error);
public sealed record CarryOverRequest(string OperationId, string SummaryId, IReadOnlyList<string> TaskIds);
public sealed record CarryOverResponse(bool Ok, string? Target, string? TargetDate, int Added, int Skipped, bool Replayed, TaskErrorResponse? Error);
public sealed record SummarySettingsRequest();
/// <summary>Times are HH:mm; null means that summary is not scheduled.</summary>
public sealed record SummarySettingsResponse(bool Ok, string? MorningTime, string? EveningTime, string? WeeklyDay, string? FirstWeekday, string? Version, string? ZoneId, TaskErrorResponse? Error);
public sealed record SummarySettingsUpdateRequest(string OperationId, string ExpectedVersion, string? MorningTime, string? EveningTime, string? WeeklyDay);

/// <summary>Plain summaries, their history, carry-over and the summary schedule (PLAN §12, WP11).</summary>
[NeoRpcService("summaries", Version = 1)]
public sealed class SummaryRpcService(SummaryCommands summaries)
{
    [NeoRpcMethod("list")]
    public async ValueTask<SummaryListResponse> ListAsync(SummaryListRequest request, CancellationToken ct)
    {
        try
        {
            var page = await summaries.ListAsync(request.Cursor, request.Limit ?? 20, ct);
            return new(true, [.. page.Entries.Select(entry => Header(entry.Summary, entry.Counts))], page.NextCursor, null);
        }
        catch (AppException error) { return new(false, [], null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("get")]
    public ValueTask<SummaryDetailResponse> GetAsync(SummaryGetRequest request, CancellationToken ct) =>
        Detail(async () => await summaries.GetAsync(request.SummaryId, ct));

    [NeoRpcMethod("find")]
    public ValueTask<SummaryDetailResponse> FindAsync(SummaryFindRequest request, CancellationToken ct) =>
        Detail(async () => await summaries.FindAsync(Kind(request.Kind), Date(request.Date, "date"), ct));

    [NeoRpcMethod("generate")]
    public ValueTask<SummaryDetailResponse> GenerateAsync(SummaryGenerateRequest request, CancellationToken ct) =>
        Detail(async () => await summaries.GenerateAsync(new GenerateSummaryCommand(request.OperationId, Kind(request.Kind), Date(request.Date, "date")), ct));

    /// <summary>Generates anything due first, so a prompt appears as soon as the frontend asks.</summary>
    [NeoRpcMethod("prompts")]
    public async ValueTask<SummaryPromptsResponse> PromptsAsync(SummaryPromptsRequest request, CancellationToken ct)
    {
        try
        {
            await summaries.GenerateDueAsync(ct);
            return new(true, [.. (await summaries.PromptsAsync(ct)).Select(prompt => Header(prompt.Summary, prompt.Counts))], null);
        }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("dismiss")]
    public async ValueTask<SummaryDismissResponse> DismissAsync(SummaryDismissRequest request, CancellationToken ct)
    {
        try
        {
            await summaries.DismissPromptAsync(request.SummaryId, ct);
            return new(true, null);
        }
        catch (AppException error) { return new(false, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("previewCarryOver")]
    public async ValueTask<CarryOverPreviewResponse> PreviewCarryOverAsync(CarryOverPreviewRequest request, CancellationToken ct)
    {
        try
        {
            var preview = await summaries.PreviewCarryOverAsync(request.SummaryId, ct);
            return new(true, preview.Target.ToString(), StorageFormats.Date(preview.TargetDate),
                [.. preview.Candidates.Select(item => new CarryOverCandidateDto(Map(item.Task), item.StillOpen, item.AlreadyPlanned))], null);
        }
        catch (AppException error) { return new(false, null, null, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("carryOver")]
    public async ValueTask<CarryOverResponse> CarryOverAsync(CarryOverRequest request, CancellationToken ct)
    {
        try
        {
            var result = await summaries.CarryOverAsync(new SummaryCarryOverCommand(request.OperationId, request.SummaryId, request.TaskIds ?? []), ct);
            return new(true, result.Target.ToString(), StorageFormats.Date(result.TargetDate), result.Added, result.Skipped, result.Replayed, null);
        }
        catch (AppException error) { return new(false, null, null, 0, 0, false, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("settings")]
    public async ValueTask<SummarySettingsResponse> SettingsAsync(SummarySettingsRequest request, CancellationToken ct)
    {
        try { return Map(await summaries.GetSettingsAsync(ct)); }
        catch (AppException error) { return new(false, null, null, null, null, null, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("updateSettings")]
    public async ValueTask<SummarySettingsResponse> UpdateSettingsAsync(SummarySettingsUpdateRequest request, CancellationToken ct)
    {
        try
        {
            var weekly = string.IsNullOrWhiteSpace(request.WeeklyDay) ? (DayOfWeek?)null : ReminderRpcService.Parse<DayOfWeek>(request.WeeklyDay, "weeklyDay");
            return Map(await summaries.UpdateSettingsAsync(new UpdateSummarySettingsCommand(
                request.OperationId,
                ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"),
                Time(request.MorningTime, "morningTime"),
                Time(request.EveningTime, "eveningTime"),
                weekly), ct));
        }
        catch (AppException error) { return new(false, null, null, null, null, null, null, TaskRpcService.ToErrorResponse(error)); }
    }

    private static async ValueTask<SummaryDetailResponse> Detail(Func<Task<SummaryView?>> run)
    {
        try
        {
            var view = await run();
            return new(true, view is null ? null : Map(view), null);
        }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }

    internal static SummaryHeaderDto Header(SummaryRecord row, SummaryCounts counts) => new(
        row.Id, row.Kind.ToString(), StorageFormats.Date(row.PeriodStart), StorageFormats.Date(row.PeriodEnd), row.Revision.ToString(CultureInfo.InvariantCulture),
        StorageFormats.Instant(row.CapturedThroughUtc), row.IsPartial, row.Trigger.ToString(), StorageFormats.Instant(row.GeneratedAtUtc), row.ZoneId,
        new SummaryCountsDto(counts.CompletionEvents, counts.StillDone, counts.Unfinished, counts.DiaryUpdates, counts.Deadlines, counts.Waiting, counts.Selected, counts.Quiet));

    private static SummaryDetailDto Map(SummaryView view) => new(
        Header(view.Summary, view.Counts),
        Map(view.Facts),
        [.. view.Revisions.Select(item => new SummaryRevisionDto(item.Id, item.Revision.ToString(CultureInfo.InvariantCulture), StorageFormats.Instant(item.GeneratedAtUtc),
            StorageFormats.Instant(item.CapturedThroughUtc), item.Trigger.ToString()))],
        view.IsLatest);

    private static SummaryFactsDto Map(SummaryFacts facts) => new(
        [.. facts.Completions.Select(item => new CompletionDto(Map(item.Task), StorageFormats.Instant(item.CompletedAtUtc), item.ReopenedAfter, item.StillDone))],
        [.. facts.Projects.Select(item => new ProjectProgressDto(item.ProjectId, item.Name, item.DoneBefore, item.CountedBefore, item.DoneAfter, item.CountedAfter))],
        [.. facts.Unfinished.Select(item => new UnfinishedDto(Map(item.Task), item.Source.ToString()))],
        [.. facts.Waiting.Select(item => new WaitingFactDto(Map(item.Task), item.Person, Optional(item.FollowUpDueAtUtc)))],
        [.. facts.Deadlines.Select(Map)],
        [.. facts.DiaryUpdates.Select(item => new DiaryFactDto(item.EntryId, item.TaskId, item.TaskTitle, StorageFormats.Instant(item.EntryAtUtc), item.Excerpt))],
        facts.DiaryUpdateCount,
        [.. facts.Suggested.Select(item => new SuggestionDto(Map(item.Task), item.Reason.ToString()))],
        facts.Week is { } week
            ? new WeekProgressDto(StorageFormats.Date(week.WeekStart), week.Revision.ToString(CultureInfo.InvariantCulture), week.AssignedCount, week.DoneCount, week.CancelledCount,
                [.. week.Goals.Select(goal => new GoalFactDto(goal.GoalId, goal.Title, goal.Outcome.ToString(), goal.DoneCount, goal.CountedCount))])
            : null,
        [.. facts.Selected.Select(Map)],
        facts.OverdueCount,
        [.. facts.FollowUps.Select(item => new FollowUpFactDto(Map(item.Task), item.Person, StorageFormats.Instant(item.DueAtUtc)))]);

    internal static SummaryTaskDto Map(SummaryTaskFact task) => new(
        task.TaskId, task.Title, task.ContainerId, task.ContainerName, task.ContainerKind.ToString(), task.Status.ToString(), task.Priority.ToString(),
        task.PlannedDate is { } planned ? StorageFormats.Date(planned) : null,
        task.DueDate is { } due ? StorageFormats.Date(due) : null,
        task.DueTime is { } time ? time.ToString("HH:mm", CultureInfo.InvariantCulture) : null,
        task.EstimateMinutes);

    private static SummarySettingsResponse Map(SummarySettings settings) => new(
        true,
        Clock(settings.MorningTime),
        Clock(settings.EveningTime),
        settings.WeeklyDay?.ToString(),
        settings.FirstWeekday.ToString(),
        settings.Version.ToString(CultureInfo.InvariantCulture),
        settings.ZoneId,
        null);

    private static string? Clock(TimeOnly? time) => time?.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string? Optional(DateTimeOffset? value) => value is { } instant ? StorageFormats.Instant(instant) : null;

    private static SummaryKind Kind(string? value) => ReminderRpcService.Parse<SummaryKind>(value, "kind");

    private static DateOnly Date(string? value, string field) =>
        !string.IsNullOrWhiteSpace(value) && DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw AppException.Validation(field, "Enter a valid date.");

    private static TimeOnly? Time(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null
        : TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : throw AppException.Validation(field, "Enter a time such as 18:00.");
}
