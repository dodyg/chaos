using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Reminders;
using Chaos.Desktop.Windows.Activation;
using Chaos.Domain.Reminders;

namespace Chaos.Desktop.Platform;

public enum ShellActivationKind { Capture = 0, Pad = 1, OpenTask = 2, Notice = 3 }

/// <summary>Something the window should do once it is in front: focus quick add, open the pad or a task, or say what happened.</summary>
public sealed record ShellActivation(long Sequence, ShellActivationKind Kind, string? TaskId, string? Message);

/// <summary>
/// Turns launches into window work (PLAN §13 activation). Shortcuts, the tray, secondary
/// launches and notification buttons all arrive here as a <see cref="LaunchIntent"/>. Reminder
/// buttons run the ordinary reminder action first (its job generation decides whether it still
/// applies); everything else is queued for the frontend, which takes the queue when it is shown.
/// The queue is bounded: a flood of launches keeps the newest entries.
/// </summary>
public sealed class ShellActivations(Func<NotificationActionCommand, CancellationToken, Task<NotificationActionResult>> act)
{
    public const int Capacity = 32;

    private readonly ConcurrentQueue<ShellActivation> _queue = new();
    private long _sequence;

    /// <summary>Raised after an intent was handled; true when the window should come to the front.</summary>
    public event Action<bool>? Handled;

    public IReadOnlyList<ShellActivation> Take()
    {
        var taken = new List<ShellActivation>();
        while (_queue.TryDequeue(out var activation))
        {
            taken.Add(activation);
        }

        return taken;
    }

    public async Task HandleAsync(LaunchIntent intent, CancellationToken ct)
    {
        var bringForward = true;
        switch (intent.Kind)
        {
            case LaunchIntentKind.Capture:
                Enqueue(ShellActivationKind.Capture);
                break;
            case LaunchIntentKind.Pad:
                Enqueue(ShellActivationKind.Pad);
                break;
            case LaunchIntentKind.OpenTask:
                Enqueue(ShellActivationKind.OpenTask, intent.TaskId);
                break;
            case LaunchIntentKind.Reminder:
                bringForward = await RunReminderAsync(intent, ct);
                break;
        }

        Handled?.Invoke(bringForward);
    }

    /// <summary>
    /// The OperationId is derived from the button, so Windows delivering the same click twice (or
    /// a retried route) is a replay, never a second snooze. Done and Snooze happen without
    /// opening the window; Open, and a Done that needs a decision about open subtasks, open the task.
    /// </summary>
    private async Task<bool> RunReminderAsync(LaunchIntent intent, CancellationToken ct)
    {
        var action = intent.Action switch
        {
            ReminderActivation.Done => NotificationAction.Done,
            ReminderActivation.SnoozeTenMinutes or ReminderActivation.SnoozeOneHour => NotificationAction.Snooze,
            _ => NotificationAction.Open,
        };
        SnoozeChoice? snooze = intent.Action switch
        {
            ReminderActivation.SnoozeTenMinutes => SnoozeChoice.TenMinutes,
            ReminderActivation.SnoozeOneHour => SnoozeChoice.OneHour,
            _ => null,
        };
        var command = new NotificationActionCommand(OperationIdFor(intent), intent.JobId!, intent.Generation, action, snooze);
        try
        {
            var result = await act(command, ct);
            switch (result.Outcome)
            {
                case NotificationActionOutcome.Opened:
                    Enqueue(ShellActivationKind.OpenTask, result.TaskId);
                    return true;
                case NotificationActionOutcome.Completed:
                    Enqueue(ShellActivationKind.Notice, result.TaskId, "Marked done from the notification.");
                    return false;
                case NotificationActionOutcome.Snoozed:
                    Enqueue(ShellActivationKind.Notice, result.TaskId, "Snoozed from the notification.");
                    return false;
                case NotificationActionOutcome.Stale:
                    Enqueue(ShellActivationKind.Notice, result.TaskId, "That notification was out of date: the reminder changed after it was shown. Nothing was done.");
                    return true;
                case NotificationActionOutcome.TaskGone:
                    Enqueue(ShellActivationKind.Notice, null, "That task was deleted; its notification was cleared.");
                    return true;
                default:
                    return false;
            }
        }
        catch (AppException error) when (error.Kind == AppErrorKind.Validation && action == NotificationAction.Done)
        {
            // Open subtasks need a person's choice (PLAN §6); never complete them silently.
            var opened = await act(command with { OperationId = OperationIdFor(intent, "open"), Action = NotificationAction.Open, Snooze = null }, ct);
            Enqueue(ShellActivationKind.OpenTask, opened.TaskId, error.Message);
            return true;
        }
        catch (AppException error)
        {
            Trace.TraceWarning("Notification action failed: {0}", error.Kind);
            Enqueue(ShellActivationKind.Notice, null, error.Message);
            return true;
        }
    }

    private void Enqueue(ShellActivationKind kind, string? taskId = null, string? message = null)
    {
        _queue.Enqueue(new ShellActivation(Interlocked.Increment(ref _sequence), kind, taskId, message));
        while (_queue.Count > Capacity && _queue.TryDequeue(out _))
        {
            // Keep the newest entries; an old capture request is meaningless after many newer ones.
        }
    }

    internal static string OperationIdFor(LaunchIntent intent, string salt = "") =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"notification-button/{intent.JobId}/{intent.Generation}/{intent.Action}/{salt}"))[..16]).ToString("D");
}
