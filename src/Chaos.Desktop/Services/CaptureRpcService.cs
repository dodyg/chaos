using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Capture;
using Chaos.Domain.Capture;
using Chaos.Domain.Tasks;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record CaptureSpanDto(int Start, int End);
public sealed record CapturePreviewRequest(string Text, IReadOnlyList<CaptureSpanDto>? Excluded);
public sealed record CaptureTokenDto(string Kind, int Start, int End, string Text, string? Language, string State, string Summary);
public sealed record CaptureWarningDto(string Code, string Message, int? Start, int? End);
public sealed record CaptureReminderDto(string Anchor, int? OffsetMinutes, string? AtUtc);
public sealed record CaptureTagDto(string Path, bool Existing);
public sealed record CaptureContainerDto(string Id, string Name);
public sealed record CaptureSubtaskDto(string Title, bool Done);

/// <summary>Editable task fields: dates yyyy-MM-dd, times HH:mm:ss, enums by name.</summary>
public sealed record CaptureDraftDto(
    string Title,
    string? Notes,
    string? ContainerId,
    string Priority,
    int? EstimateMinutes,
    string? PlannedDate,
    string? PlannedTime,
    string? DueDate,
    string? DueTime,
    string? ZoneId,
    RecurrenceRuleDto? Recurrence,
    IReadOnlyList<CaptureReminderDto> Reminders,
    IReadOnlyList<string> TagIds,
    IReadOnlyList<string> TagPaths,
    string? WaitingOn,
    string? FollowUpDate,
    string? FollowUpTime,
    IReadOnlyList<CaptureSubtaskDto> Subtasks);

/// <summary>One task line of the preview. Token offsets are relative to <see cref="Text"/>, which starts at <see cref="SourceStart"/> in the typed input.</summary>
public sealed record CaptureItemDto(
    int LineNumber,
    int SourceStart,
    string Text,
    CaptureDraftDto Draft,
    IReadOnlyList<CaptureTokenDto> Tokens,
    IReadOnlyList<CaptureWarningDto> Warnings,
    IReadOnlyList<string> Languages,
    bool NeedsTitle,
    IReadOnlyList<CaptureTagDto> Tags,
    IReadOnlyList<CaptureContainerDto> ContainerCandidates,
    string? FirstOccurrence);

public sealed record CaptureBatchErrorDto(int LineNumber, string Message);
public sealed record CapturePreviewResponse(bool Ok, bool IsBatch, IReadOnlyList<CaptureItemDto> Items, IReadOnlyList<CaptureBatchErrorDto> Errors, string? CapturedAtUtc, TaskErrorResponse? Error);
public sealed record CaptureCreateRequest(string OperationId, IReadOnlyList<CaptureDraftDto> Drafts);
public sealed record CaptureCreateResponse(bool Ok, IReadOnlyList<TaskResponse> Tasks, TaskErrorResponse? Error);

/// <summary>
/// Quick add over RPC (PLAN §13 Capture/tasks): a read-only preview of typed or pasted text,
/// and an atomic create of the drafts the person confirmed. Preview never writes.
/// </summary>
[NeoRpcService("capture", Version = 1)]
public sealed class CaptureRpcService(CaptureCommands capture)
{
    [NeoRpcMethod("preview")]
    public async ValueTask<CapturePreviewResponse> PreviewAsync(CapturePreviewRequest request, CancellationToken ct)
    {
        try
        {
            var excluded = (request.Excluded ?? []).Take(200).Select(span => new CaptureSpan(span.Start, span.End)).ToList();
            var preview = await capture.PreviewAsync(request.Text ?? string.Empty, excluded, ct);
            return new(true, preview.IsBatch, [.. preview.Items.Select(Map)], [.. preview.Errors.Select(error => new CaptureBatchErrorDto(error.LineNumber, error.Message))],
                StorageFormats.Instant(preview.CapturedAtUtc), null);
        }
        catch (AppException error) { return new(false, false, [], [], null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("create")]
    public async ValueTask<CaptureCreateResponse> CreateAsync(CaptureCreateRequest request, CancellationToken ct)
    {
        try
        {
            if (request.Drafts is null || request.Drafts.Count > 500) throw AppException.Validation("drafts", "Send between 1 and 500 tasks.");
            var drafts = request.Drafts.Select((draft, index) => ToDraft(draft, $"drafts[{index}]")).ToList();
            var result = await capture.CreateAsync(new(request.OperationId, drafts), ct);
            return new(true, [.. result.Tasks.Select(TaskRpcService.ToResponse)], null);
        }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    internal static CaptureDraftDto MapDraft(QuickAddDraft draft, IReadOnlyList<CaptureSubtask> subtasks) => new(
        draft.Title,
        null,
        draft.ContainerId,
        (draft.Priority ?? TaskPriority.Normal).ToString(),
        draft.EstimateMinutes,
        draft.PlannedDate is { } planned ? StorageFormats.Date(planned) : null,
        draft.PlannedTime?.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        draft.DueDate is { } due ? StorageFormats.Date(due) : null,
        draft.DueTime?.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        draft.ZoneId,
        draft.Recurrence is { } rule ? RecurrenceRpcService.Map(rule) : null,
        [.. draft.Reminders.Select(reminder => new CaptureReminderDto(reminder.Anchor.ToString(), reminder.OffsetMinutes, reminder.AtUtc is { } at ? StorageFormats.Instant(at) : null))],
        [],
        [.. draft.Tags.Select(tag => tag.Path)],
        draft.WaitingOn,
        draft.FollowUpDate is { } follow ? StorageFormats.Date(follow) : null,
        draft.FollowUpTime?.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        [.. subtasks.Select(subtask => new CaptureSubtaskDto(subtask.Title, subtask.Done))]);

    internal static CaptureTaskDraft ToDraft(CaptureDraftDto dto, string path) => new()
    {
        Title = dto.Title,
        Notes = dto.Notes,
        ContainerId = string.IsNullOrWhiteSpace(dto.ContainerId) ? null : dto.ContainerId,
        Priority = Enum.TryParse<TaskPriority>(dto.Priority, out var priority) && Enum.IsDefined(priority) ? priority : throw AppException.Validation($"{path}.priority", "Choose a valid priority."),
        EstimateMinutes = dto.EstimateMinutes,
        PlannedDate = Date(dto.PlannedDate, $"{path}.plannedDate"),
        PlannedTime = Time(dto.PlannedTime, $"{path}.plannedTime"),
        DueDate = Date(dto.DueDate, $"{path}.dueDate"),
        DueTime = Time(dto.DueTime, $"{path}.dueTime"),
        ZoneId = string.IsNullOrWhiteSpace(dto.ZoneId) ? null : dto.ZoneId,
        Recurrence = dto.Recurrence is { } rule ? RecurrenceRpcService.Parse(rule) : null,
        Reminders = [.. (dto.Reminders ?? []).Select(reminder => new CaptureReminder(
            Enum.TryParse<CaptureReminderAnchor>(reminder.Anchor, out var anchor) && Enum.IsDefined(anchor) ? anchor : throw AppException.Validation($"{path}.reminders", "Choose a valid reminder anchor."),
            reminder.OffsetMinutes,
            reminder.AtUtc is null ? null : DateTimeOffset.TryParse(reminder.AtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUniversalTime() : throw AppException.Validation($"{path}.reminders", "Enter a valid reminder time.")))],
        TagIds = dto.TagIds ?? [],
        TagPaths = dto.TagPaths ?? [],
        WaitingOn = dto.WaitingOn,
        FollowUpDate = Date(dto.FollowUpDate, $"{path}.followUpDate"),
        FollowUpTime = Time(dto.FollowUpTime, $"{path}.followUpTime"),
        Subtasks = [.. (dto.Subtasks ?? []).Select(subtask => new CaptureSubtask(subtask.Title, subtask.Done))],
    };

    private static CaptureItemDto Map(CaptureItemPreview item)
    {
        var parse = item.Parse;
        return new(item.LineNumber, item.SourceStart, parse.Source, MapDraft(parse.Draft, item.Subtasks),
            [.. parse.Tokens.Select(token => new CaptureTokenDto(token.Kind.ToString(), token.Start, token.End, token.Text, token.Language, token.State.ToString(), token.Summary))],
            [.. parse.Warnings.Select(warning => new CaptureWarningDto(warning.Code, warning.Message, warning.Start, warning.End))],
            parse.Languages, parse.NeedsTitle,
            [.. parse.Draft.Tags.Select(tag => new CaptureTagDto(tag.Path, tag.Existing))],
            [.. parse.Draft.ContainerCandidates.Select(container => new CaptureContainerDto(container.Id, container.Name))],
            parse.Draft.FirstOccurrence?.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
    }

    private static DateOnly? Date(string? value, string field) => string.IsNullOrEmpty(value) ? null
        : DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw AppException.Validation(field, "Enter a valid date.");

    private static TimeOnly? Time(string? value, string field) => string.IsNullOrEmpty(value) ? null
        : TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out var time) ? time : throw AppException.Validation(field, "Enter a valid time.");
}
