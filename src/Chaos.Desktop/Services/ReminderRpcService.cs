using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Reminders;
using Chaos.Application.Tasks;
using Chaos.Desktop.Platform;
using Chaos.Domain.Reminders;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

/// <summary>Anchor is Absolute, Planned or Due; offsets are minutes relative to the anchor (negative = before).</summary>
public sealed record ReminderRuleDto(string Anchor, int? OffsetMinutes, string? AbsoluteAtUtc, int? InsistentMinutes, bool Enabled);
public sealed record ReminderRuleResponse(
    string Id,
    string TaskId,
    string Version,
    ReminderRuleDto Rule,
    string? TargetAtUtc,
    string? NextDeliveryAtUtc,
    string Suspension,
    string? LastDeliveredAtUtc);
public sealed record ReminderListRequest(string TaskId);
public sealed record ReminderListResponse(bool Ok, IReadOnlyList<ReminderRuleResponse> Rules, TaskErrorResponse? Error);
public sealed record ReminderAddRequest(string OperationId, string TaskId, ReminderRuleDto Rule);
public sealed record ReminderUpdateRequest(string OperationId, string RuleId, string ExpectedVersion, ReminderRuleDto Rule);
public sealed record ReminderDeleteRequest(string OperationId, string RuleId, string ExpectedVersion);
public sealed record ReminderOperationResponse(bool Ok, bool Replayed, TaskErrorResponse? Error);

public sealed record NotificationCapabilitiesResponse(string AdapterName, bool SchedulesWhileClosed, bool SupportsActions, string Permission, string Description);
public sealed record ReminderSettingsRequest();
/// <summary>Times are HH:mm local to the profile zone.</summary>
public sealed record ReminderSettingsResponse(
    bool Ok,
    string? DefaultReminderTime,
    bool QuietHoursEnabled,
    string? QuietStart,
    string? QuietEnd,
    string? Version,
    string? ZoneId,
    NotificationCapabilitiesResponse? Capabilities,
    TaskErrorResponse? Error);
public sealed record ReminderSettingsUpdateRequest(string OperationId, string ExpectedVersion, string DefaultReminderTime, bool QuietHoursEnabled, string? QuietStart, string? QuietEnd);

public sealed record NotificationItemResponse(
    string JobId,
    string Generation,
    string TaskId,
    string TaskTitle,
    bool TaskOpen,
    string Source,
    string? Person,
    string TargetAtUtc,
    string? DeliveredAtUtc,
    string State,
    string Presentation,
    string? ActionTaken,
    bool Late,
    bool Repeat);
public sealed record NotificationListRequest(int? Limit);
public sealed record NotificationListResponse(bool Ok, IReadOnlyList<NotificationItemResponse> Items, TaskErrorResponse? Error);
/// <summary>Action is Open, Done, Snooze or Dismiss; Snooze is TenMinutes, OneHour, Tomorrow or Until (with SnoozeUntilUtc).</summary>
public sealed record NotificationActRequest(
    string OperationId,
    string JobId,
    string Generation,
    string Action,
    string? Snooze,
    string? SnoozeUntilUtc,
    string? OpenSubtaskChoice);
public sealed record NotificationActResponse(bool Ok, string? Outcome, string? TaskId, string? SnoozedUntilUtc, bool Replayed, TaskErrorResponse? Error);

/// <summary>Reminder rules, settings, alerts, actions and the notification center (PLAN §11, WP10).</summary>
[NeoRpcService("reminders", Version = 1)]
public sealed class ReminderRpcService(ReminderCommands reminders, ReminderEngine engine)
{
    [NeoRpcMethod("list")]
    public async ValueTask<ReminderListResponse> ListAsync(ReminderListRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await reminders.ListForTaskAsync(request.TaskId, ct)).Select(Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("add")]
    public ValueTask<ReminderOperationResponse> AddAsync(ReminderAddRequest request, CancellationToken ct) =>
        Change(async () => (await reminders.AddAsync(new AddReminderCommand(request.OperationId, request.TaskId, Input(request.Rule)), ct)).Replayed);

    [NeoRpcMethod("update")]
    public ValueTask<ReminderOperationResponse> UpdateAsync(ReminderUpdateRequest request, CancellationToken ct) =>
        Change(async () => (await reminders.UpdateAsync(new UpdateReminderCommand(request.OperationId, request.RuleId, Number(request.ExpectedVersion, "expectedVersion"), Input(request.Rule)), ct)).Replayed);

    [NeoRpcMethod("delete")]
    public ValueTask<ReminderOperationResponse> DeleteAsync(ReminderDeleteRequest request, CancellationToken ct) =>
        Change(async () => (await reminders.DeleteAsync(new DeleteReminderCommand(request.OperationId, request.RuleId, Number(request.ExpectedVersion, "expectedVersion")), ct)).Replayed);

    [NeoRpcMethod("settings")]
    public async ValueTask<ReminderSettingsResponse> SettingsAsync(ReminderSettingsRequest request, CancellationToken ct)
    {
        try { return Map(await reminders.GetSettingsAsync(ct)); }
        catch (AppException error) { return new(false, null, false, null, null, null, null, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("updateSettings")]
    public async ValueTask<ReminderSettingsResponse> UpdateSettingsAsync(ReminderSettingsUpdateRequest request, CancellationToken ct)
    {
        try
        {
            var settings = await reminders.UpdateSettingsAsync(new UpdateReminderSettingsCommand(
                request.OperationId,
                Number(request.ExpectedVersion, "expectedVersion"),
                Time(request.DefaultReminderTime, "defaultReminderTime") ?? throw AppException.Validation("defaultReminderTime", "Choose a default reminder time."),
                request.QuietHoursEnabled,
                Time(request.QuietStart, "quietStart"),
                Time(request.QuietEnd, "quietEnd")), ct);
            return Map(settings);
        }
        catch (AppException error) { return new(false, null, false, null, null, null, null, null, TaskRpcService.ToErrorResponse(error)); }
    }

    /// <summary>Delivers anything due, then lists delivered notifications still waiting for an action.</summary>
    [NeoRpcMethod("alerts")]
    public async ValueTask<NotificationListResponse> AlertsAsync(NotificationListRequest request, CancellationToken ct)
    {
        try
        {
            await engine.TickAsync(ct);
            return new(true, [.. (await reminders.AlertsAsync(ct)).Select(Map)], null);
        }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("center")]
    public async ValueTask<NotificationListResponse> CenterAsync(NotificationListRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await reminders.CenterAsync(request.Limit ?? 100, ct)).Select(Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("act")]
    public async ValueTask<NotificationActResponse> ActAsync(NotificationActRequest request, CancellationToken ct)
    {
        try
        {
            var result = await reminders.ActAsync(new NotificationActionCommand(
                request.OperationId,
                request.JobId,
                Number(request.Generation, "generation"),
                Parse<NotificationAction>(request.Action, "action"),
                request.Snooze is null ? null : Parse<SnoozeChoice>(request.Snooze, "snooze"),
                Instant(request.SnoozeUntilUtc, "snoozeUntilUtc"),
                request.OpenSubtaskChoice is null ? null : Parse<OpenSubtaskChoice>(request.OpenSubtaskChoice, "openSubtaskChoice")), ct);
            return new(true, result.Outcome.ToString(), result.TaskId, result.SnoozedUntilUtc is { } until ? StorageFormats.Instant(until) : null, result.Replayed, null);
        }
        catch (AppException error) { return new(false, null, null, null, false, TaskRpcService.ToErrorResponse(error)); }
    }

    private static async ValueTask<ReminderOperationResponse> Change(Func<Task<bool>> run)
    {
        try { return new(true, await run(), null); }
        catch (AppException error) { return new(false, false, TaskRpcService.ToErrorResponse(error)); }
    }

    internal static ReminderRuleInput Input(ReminderRuleDto dto) => new(
        Parse<ReminderAnchor>(dto.Anchor, "anchor"),
        dto.OffsetMinutes,
        Instant(dto.AbsoluteAtUtc, "absoluteAtUtc"),
        dto.InsistentMinutes,
        dto.Enabled);

    private static ReminderRuleResponse Map(ReminderRuleView view) => new(
        view.Rule.Id,
        view.Rule.TaskId,
        Text(view.Rule.Version),
        new ReminderRuleDto(view.Rule.Anchor.ToString(), view.Rule.OffsetMinutes,
            view.Rule.AbsoluteAtUtc is { } at ? StorageFormats.Instant(at) : null, view.Rule.InsistentMinutes, view.Rule.Enabled),
        Optional(view.TargetAtUtc),
        Optional(view.NextDeliveryAtUtc),
        view.Suspension.ToString(),
        Optional(view.LastDeliveredAtUtc));

    private static ReminderSettingsResponse Map(ReminderSettings settings) => new(
        true,
        settings.DefaultReminderTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        settings.QuietHoursEnabled,
        settings.QuietStart?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        settings.QuietEnd?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        Text(settings.Version),
        settings.ZoneId,
        new NotificationCapabilitiesResponse(settings.Capabilities.AdapterName, settings.Capabilities.SchedulesWhileClosed,
            settings.Capabilities.SupportsActions, settings.Capabilities.Permission.ToString(), settings.Capabilities.Description),
        null);

    private static NotificationItemResponse Map(NotificationItem item) => new(
        item.Job.Id,
        Text(item.Job.Generation),
        item.Job.TaskId,
        item.TaskTitle,
        item.TaskOpen,
        item.Job.SourceKind.ToString(),
        item.Person,
        StorageFormats.Instant(item.Job.TargetAtUtc),
        Optional(item.Job.DeliveredAtUtc),
        item.Job.State.ToString(),
        item.Job.Presentation.ToString(),
        item.Job.ActionTaken,
        item.Late,
        item.Job.Reason == NotificationReason.Repeat);

    private static string Text(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>64-bit numbers travel as decimal strings over RPC.</summary>
    internal static long Number(string? value, string field) =>
        long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : throw AppException.Validation(field, "This item is out of date. Refresh and try again.");

    private static string? Optional(DateTimeOffset? value) => value is { } instant ? StorageFormats.Instant(instant) : null;

    internal static T Parse<T>(string? value, string field) where T : struct, Enum =>
        value is not null && !int.TryParse(value, out _) && Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw AppException.Validation(field, "Choose a valid value.");

    internal static DateTimeOffset? Instant(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null
        : DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var instant)
            ? instant.ToUniversalTime()
            : throw AppException.Validation(field, "Enter a valid date and time.");

    private static TimeOnly? Time(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? null
        : TimeOnly.TryParseExact(value, ["HH:mm", "HH:mm:ss"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time)
            ? time
            : throw AppException.Validation(field, "Enter a time such as 09:00.");
}
