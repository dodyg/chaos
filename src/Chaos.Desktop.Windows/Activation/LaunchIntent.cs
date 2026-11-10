using System.Globalization;

namespace Chaos.Desktop.Windows.Activation;

public enum LaunchIntentKind { Open = 0, Capture = 1, Pad = 2, OpenTask = 3, Reminder = 4 }

/// <summary>What a reminder button asks for; the host maps these onto the reminder commands.</summary>
public enum ReminderActivation { Open = 0, Done = 1, SnoozeTenMinutes = 2, SnoozeOneHour = 3 }

/// <summary>
/// Why Chaos was started or brought forward (PLAN §13 activation): plain open, the capture or
/// pad shortcut, a task link, or a reminder button. A secondary launch forwards its intent to the
/// running instance; a notification button arrives as a <c>chaos:</c> URI.
/// </summary>
public sealed record LaunchIntent(
    LaunchIntentKind Kind,
    string? TaskId = null,
    string? JobId = null,
    long Generation = 0,
    ReminderActivation Action = ReminderActivation.Open)
{
    public static readonly LaunchIntent Open = new(LaunchIntentKind.Open);
    public static readonly LaunchIntent Capture = new(LaunchIntentKind.Capture);
    public static readonly LaunchIntent Pad = new(LaunchIntentKind.Pad);
}

/// <summary>
/// The <c>chaos:</c> URIs that notification buttons and secondary launches carry, and the
/// command-line switches. Parsing is strict: anything unexpected is ignored rather than guessed,
/// and IDs must be GUIDs, so a crafted URI can name a task or job but never a path or a command.
/// </summary>
public static class LaunchIntents
{
    public const string Scheme = "chaos";
    public const int MaxArguments = 8;
    public const int MaxArgumentLength = 512;

    private static readonly IReadOnlyDictionary<string, ReminderActivation> Actions = new Dictionary<string, ReminderActivation>(StringComparer.Ordinal)
    {
        ["open"] = ReminderActivation.Open,
        ["done"] = ReminderActivation.Done,
        ["snooze-10m"] = ReminderActivation.SnoozeTenMinutes,
        ["snooze-1h"] = ReminderActivation.SnoozeOneHour,
    };

    public static string CaptureUri => $"{Scheme}:capture";
    public static string PadUri => $"{Scheme}:pad";

    public static string TaskUri(string taskId) => $"{Scheme}:task/{Guid.Parse(taskId):D}";

    public static string ReminderUri(string jobId, long generation, ReminderActivation action) =>
        string.Create(CultureInfo.InvariantCulture, $"{Scheme}:reminder/{Guid.Parse(jobId):D}/{generation}/{Actions.Single(pair => pair.Value == action).Key}");

    /// <summary>
    /// Reads the process arguments: <c>--capture</c>, <c>--pad</c> or one <c>chaos:</c> URI.
    /// Unknown arguments (such as <c>--data-dir</c>) are skipped; no intent means plain open.
    /// </summary>
    public static LaunchIntent FromArguments(IReadOnlyList<string> args)
    {
        foreach (var arg in args.Take(MaxArguments * 2))
        {
            if (arg.Length > MaxArgumentLength)
            {
                continue;
            }

            switch (arg)
            {
                case "--capture":
                    return LaunchIntent.Capture;
                case "--pad":
                    return LaunchIntent.Pad;
            }

            if (TryParseUri(arg, out var intent))
            {
                return intent;
            }
        }

        return LaunchIntent.Open;
    }

    public static bool TryParseUri(string text, out LaunchIntent intent)
    {
        intent = LaunchIntent.Open;
        if (!text.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase) || text.Length > MaxArgumentLength)
        {
            return false;
        }

        // Windows passes protocol activations as the full URI; tolerate "chaos://x" as well as "chaos:x".
        var path = text[(Scheme.Length + 1)..].TrimStart('/').TrimEnd('/');
        var parts = path.Split('/');
        switch (parts)
        {
            case ["capture"]:
                intent = LaunchIntent.Capture;
                return true;
            case ["pad"]:
                intent = LaunchIntent.Pad;
                return true;
            case ["open"]:
                intent = LaunchIntent.Open;
                return true;
            case ["task", var taskId] when Guid.TryParseExact(taskId, "D", out var task):
                intent = new LaunchIntent(LaunchIntentKind.OpenTask, TaskId: task.ToString("D"));
                return true;
            case ["reminder", var jobId, var generation, var action]
                when Guid.TryParseExact(jobId, "D", out var job)
                    && long.TryParse(generation, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    && Actions.TryGetValue(action, out var activation):
                intent = new LaunchIntent(LaunchIntentKind.Reminder, JobId: job.ToString("D"), Generation: number, Action: activation);
                return true;
            default:
                return false;
        }
    }
}
