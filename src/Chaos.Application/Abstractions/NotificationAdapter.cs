namespace Chaos.Application.Abstractions;

public enum NotificationPermission { Unknown = 0, Granted = 1, Denied = 2 }

/// <summary>
/// What the platform notification adapter can do (PLAN §11). Capture never waits on this;
/// the settings and notification center show it so a person knows what to expect.
/// </summary>
/// <param name="SchedulesWhileClosed">The OS delivers scheduled notifications after the app exits.</param>
/// <param name="SupportsActions">Notifications carry Open, Done and Snooze buttons.</param>
/// <param name="Description">A plain explanation shown to the person.</param>
public sealed record NotificationCapabilities(
    string AdapterName,
    bool SchedulesWhileClosed,
    bool SupportsActions,
    NotificationPermission Permission,
    string Description);

/// <summary>What the adapter needs to show or schedule one notification. No task content leaves the device.</summary>
public sealed record NotificationRequest(
    string JobId,
    long Generation,
    string TaskId,
    string Title,
    string Body,
    DateTimeOffset DeliverAtUtc,
    bool IsFollowUp);

public enum NotificationShowOutcome { System = 0, InApp = 1, Suppressed = 2 }

/// <summary>Result of asking the OS to schedule: its identifier, or why it could not.</summary>
public sealed record NotificationScheduleResult(bool Scheduled, string? OsNotificationId, string? Error);

/// <summary>
/// The platform port for notifications (PLAN §4 INotificationAdapter): capability report,
/// schedule/cancel/show. It never mutates tasks; activations come back through the reminder
/// commands, which validate the job generation and use the ordinary task commands.
/// </summary>
public interface INotificationAdapter
{
    NotificationCapabilities Capabilities { get; }

    /// <summary>Asks the OS to deliver at <see cref="NotificationRequest.DeliverAtUtc"/>. Adapters without OS scheduling return not scheduled.</summary>
    Task<NotificationScheduleResult> ScheduleAsync(NotificationRequest request, CancellationToken ct);

    Task CancelAsync(string osNotificationId, CancellationToken ct);

    /// <summary>Shows a due notification now (the app is running).</summary>
    Task<NotificationShowOutcome> ShowAsync(NotificationRequest request, CancellationToken ct);
}
