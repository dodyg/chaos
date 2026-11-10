using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Desktop.Windows.Activation;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Chaos.Desktop.Windows.Notifications;

/// <summary>
/// Windows notifications for the installed (MSIX) package (PLAN §11 Windows implementation).
/// Reminders are handed to Windows with <c>AddToSchedule</c>, so they appear at their time even
/// after Chaos has exited, and are taken back with <c>RemoveFromSchedule</c>. Every button is a
/// <c>chaos:</c> protocol activation: Windows starts Chaos (or the running instance receives
/// it through <see cref="LaunchRouter"/>) and the reminder commands validate the job generation
/// before acting, so a stale or repeated click changes nothing.
/// <para>
/// Windows only offers this to a process with package identity; <see cref="TryCreate"/> returns
/// null otherwise and the host keeps the in-app presenter. Nothing leaves the device.
/// </para>
/// </summary>
public sealed class WindowsToastAdapter : INotificationAdapter
{
    private const string Group = "reminders";
    private static readonly TimeSpan MinimumLead = TimeSpan.FromSeconds(10);

    private readonly ToastNotifier _notifier;
    private readonly TimeProvider _clock;

    private WindowsToastAdapter(ToastNotifier notifier, TimeProvider clock)
    {
        _notifier = notifier;
        _clock = clock;
    }

    /// <summary>The adapter when this process runs from the installed package; otherwise null.</summary>
    public static WindowsToastAdapter? TryCreate(TimeProvider clock)
    {
        if (!PackageIdentity.IsPackaged)
        {
            return null;
        }

        try
        {
            return new WindowsToastAdapter(ToastNotificationManager.CreateToastNotifier(), clock);
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Trace.TraceWarning("Windows notifications are unavailable: {0}", error.GetType().Name);
            return null;
        }
    }

    public NotificationCapabilities Capabilities
    {
        get
        {
            var permission = Permission();
            return new NotificationCapabilities(
                "Windows notifications",
                SchedulesWhileClosed: permission == NotificationPermission.Granted,
                SupportsActions: true,
                permission,
                permission == NotificationPermission.Granted
                    ? "Reminders and follow-ups are handed to Windows, so they arrive at their time even when Chaos is closed. "
                      + "Open, Done and Snooze work from the notification. Windows does not deliver while the computer is off; "
                      + "anything missed is shown once when Chaos starts again."
                    : "Windows notifications are turned off for Chaos in Windows Settings > System > Notifications. "
                      + "Reminders still appear inside Chaos while it is open.");
        }
    }

    public Task<NotificationScheduleResult> ScheduleAsync(NotificationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Permission() != NotificationPermission.Granted)
        {
            return Task.FromResult(new NotificationScheduleResult(false, null, "Windows notifications are turned off for Chaos."));
        }

        if (request.DeliverAtUtc - _clock.GetUtcNow() < MinimumLead)
        {
            // Too close (or past): the running engine shows it when due instead.
            return Task.FromResult(new NotificationScheduleResult(false, null, "Due now; shown by the running app."));
        }

        var id = OsId(request);
        try
        {
            var scheduled = new ScheduledToastNotification(Content(request), request.DeliverAtUtc)
            {
                Id = id,
                Tag = id,
                Group = Group,
            };
            _notifier.AddToSchedule(scheduled);
            return Task.FromResult(new NotificationScheduleResult(true, id, null));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return Task.FromResult(new NotificationScheduleResult(false, null, "Windows did not accept the notification."));
        }
    }

    public Task CancelAsync(string osNotificationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            foreach (var scheduled in _notifier.GetScheduledToastNotifications().Where(item => item.Id == osNotificationId).ToList())
            {
                _notifier.RemoveFromSchedule(scheduled);
            }

            ToastNotificationManager.History.Remove(osNotificationId, Group);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Already delivered and dismissed, or never scheduled: nothing is left to take back.
            Trace.TraceInformation("Notification {0} was not in the Windows schedule: {1}", osNotificationId, error.GetType().Name);
        }

        return Task.CompletedTask;
    }

    public Task<NotificationShowOutcome> ShowAsync(NotificationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Permission() != NotificationPermission.Granted)
        {
            return Task.FromResult(NotificationShowOutcome.InApp);
        }

        try
        {
            var id = OsId(request);
            _notifier.Show(new ToastNotification(Content(request)) { Tag = id, Group = Group });
            return Task.FromResult(NotificationShowOutcome.System);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Trace.TraceWarning("Windows did not show a notification: {0}", error.GetType().Name);
            return Task.FromResult(NotificationShowOutcome.InApp);
        }
    }

    /// <summary>The toast XML: text only, every button a <c>chaos:</c> protocol activation.</summary>
    public static string ContentXml(NotificationRequest request)
    {
        static string Attr(string value) => SecurityElement.Escape(value) ?? "";
        var open = LaunchIntents.ReminderUri(request.JobId, request.Generation, ReminderActivation.Open);
        var builder = new StringBuilder();
        builder.Append("<toast scenario=\"reminder\" activationType=\"protocol\" launch=\"").Append(Attr(open)).Append("\">");
        builder.Append("<visual><binding template=\"ToastGeneric\">");
        builder.Append("<text>").Append(Attr(request.Title)).Append("</text>");
        if (!string.IsNullOrWhiteSpace(request.Body))
        {
            builder.Append("<text>").Append(Attr(request.Body)).Append("</text>");
        }

        builder.Append("</binding></visual><actions>");
        foreach (var (label, action) in new[]
        {
            ("Open", ReminderActivation.Open),
            ("Done", ReminderActivation.Done),
            ("Snooze 10 min", ReminderActivation.SnoozeTenMinutes),
            ("Snooze 1 hour", ReminderActivation.SnoozeOneHour),
        })
        {
            builder.Append("<action activationType=\"protocol\" content=\"").Append(Attr(label))
                .Append("\" arguments=\"").Append(Attr(LaunchIntents.ReminderUri(request.JobId, request.Generation, action))).Append("\"/>");
        }

        builder.Append("</actions></toast>");
        return builder.ToString();
    }

    /// <summary>A 16-character ID per job generation; Windows limits scheduled IDs and tags.</summary>
    public static string OsId(NotificationRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{request.JobId}/{request.Generation}")))[..16].ToLowerInvariant();

    private static XmlDocument Content(NotificationRequest request)
    {
        var document = new XmlDocument();
        document.LoadXml(ContentXml(request));
        return document;
    }

    private NotificationPermission Permission()
    {
        try
        {
            return _notifier.Setting == NotificationSetting.Enabled ? NotificationPermission.Granted : NotificationPermission.Denied;
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Trace.TraceWarning("Windows notification setting unavailable: {0}", error.GetType().Name);
            return NotificationPermission.Unknown;
        }
    }
}

/// <summary>Whether this process runs with MSIX package identity.</summary>
public static class PackageIdentity
{
    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            return global::Windows.ApplicationModel.Package.Current is not null;
        }
        catch (InvalidOperationException)
        {
            // No package identity: an ordinary unpackaged run (dotnet run, publish folder).
            return false;
        }
    }
}
