using System.Diagnostics;
using Chaos.Application.Settings;
using Chaos.Desktop.Windows.Activation;
using Chaos.Desktop.Windows.Notifications;
using NeoAstra;
using NeoAstra.Desktop;
using NeoAstra.Desktop.Menus;
using NeoAstra.Desktop.Tray;

namespace Chaos.Desktop.Platform;

/// <summary>Whether one global shortcut is active: Registered, Off, Conflict, Unsupported or Failed.</summary>
public sealed record ShortcutStatus(string Id, string? Accelerator, string State, string Message);

public sealed record ShellStatus(bool TrayAvailable, bool KeepRunningInTray, bool Packaged, IReadOnlyList<ShortcutStatus> Shortcuts);

/// <summary>What the settings page and quit button need from the desktop host.</summary>
public interface IDesktopShell
{
    ShellStatus Status { get; }

    /// <summary>Re-registers the global shortcuts and the close-to-tray choice after a settings change.</summary>
    Task ApplyAsync(ProfileSettings settings, CancellationToken ct);

    /// <summary>Ends Chaos, including the tray process.</summary>
    void RequestQuit();
}

/// <summary>
/// The Windows shell around the window (PLAN §13): notification-area icon with Open, Quick add,
/// New pad note and Quit; global capture and pad shortcuts with conflict reporting; closing the
/// window keeps Chaos in the tray while that is chosen and a tray icon exists, so reminders keep
/// running; and bringing the window forward for shortcuts, the tray, secondary launches and
/// notification buttons. NeoAstra's native tray, menu and hot-key services do the OS work.
/// </summary>
public sealed class DesktopShell(ShellActivations activations, string applicationVersion, string privateDataDirectory) : IDesktopShell
{
    public const string ApplicationId = "dev.chaos.planner";
    private const string TrayId = "chaos";
    private const string CaptureId = "capture";
    private const string PadId = "pad";

    private readonly object _gate = new();
    private NeoApplication? _application;
    private NeoWindow? _window;
    private NeoDesktopServices? _desktop;
    private ProfileSettings? _settings;
    private bool _initialized;
    private bool _trayReady;
    private volatile bool _quitting;
    private ShortcutStatus[] _shortcuts = [];

    public ShellStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new ShellStatus(_trayReady, _settings?.KeepRunningInTray ?? true, PackageIdentity.IsPackaged, _shortcuts);
            }
        }
    }

    /// <summary>The tray and notification-area icon file shipped next to the executable.</summary>
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Icons", "chaos.ico");

    /// <summary>Creates NeoAstra's desktop services and hands them to the plugin host, which starts and stops them with the app.</summary>
    public void UsePlugins(NeoApplication application, NeoPluginBuilder plugins)
    {
        Directory.CreateDirectory(privateDataDirectory);
        // The URL and file scopes only govern NeoAstra's backend opener, which this shell never
        // calls and no page can reach (no desktop renderer handlers are registered); links keep
        // going through markdown.openExternal. NeoAstra requires non-empty scopes, so they name
        // the shell's own private folder and plain text only.
        var desktop = NeoDesktopServices.CreateSystem(ApplicationId, "Chaos", applicationVersion, privateDataDirectory,
            NeoAstra.Desktop.Opener.NeoUrlScope.AnyWebAddress, [privateDataDirectory], [privateDataDirectory],
            [NeoAstra.Desktop.Opener.NeoOpenFileIntent.TextDocument], application.Dispatcher);
        plugins.AddNeoAstraDesktop(desktop);
        lock (_gate)
        {
            _application = application;
            _desktop = desktop;
        }
    }

    public void ConfigureMainWindow(NeoApplication application, NeoWindow window, ProfileSettings settings)
    {
        lock (_gate)
        {
            _application = application;
            _window = window;
            _settings = settings;
        }

        window.CloseRequested += OnCloseRequestedAsync;
        application.LaunchReceived += OnLaunchAsync;
        activations.Handled += bringForward =>
        {
            if (bringForward)
            {
                BringForward();
            }
        };
    }

    public async Task ApplyAsync(ProfileSettings settings, CancellationToken ct)
    {
        lock (_gate)
        {
            _settings = settings;
        }

        await RegisterShortcutsAsync(settings, ct);
    }

    public void RequestQuit()
    {
        _quitting = true;
        var application = _application;
        application?.Dispatcher.Post(() => _ = application.RequestQuitAsync());
    }

    /// <summary>Shows, restores and activates the window on the UI thread. Windows may still refuse the foreground.</summary>
    public void BringForward()
    {
        var (application, window) = (_application, _window);
        if (application is null || window is null)
        {
            return;
        }

        application.Dispatcher.Post(() =>
        {
            window.Show();
            if (window.State == NeoWindowState.Minimized)
            {
                window.Restore();
            }

            window.Activate();
        });
    }

    private async ValueTask OnLaunchAsync(NeoLaunchEvent launch)
    {
        if (launch.Reason != NeoLaunchReason.Initial || _initialized)
        {
            return;
        }

        _initialized = true;
        await InitializeTrayAsync(CancellationToken.None);
        if (_settings is { } settings)
        {
            await RegisterShortcutsAsync(settings, CancellationToken.None);
        }
    }

    private ValueTask OnCloseRequestedAsync(NeoWindowCloseRequest request)
    {
        // Close-to-tray only for the person's own close, only while a tray icon exists to bring
        // the window back, and never during Quit or Windows shutting down.
        if (!_quitting && request.Reason == NeoWindowCloseReason.User && request.CanCancel && _trayReady && (_settings?.KeepRunningInTray ?? false))
        {
            request.Cancel();
            _window?.Hide();
        }

        return ValueTask.CompletedTask;
    }

    private async Task InitializeTrayAsync(CancellationToken ct)
    {
        var desktop = _desktop;
        if (desktop is null || !desktop.Tray.Support.IsSupported || !File.Exists(IconPath))
        {
            return;
        }

        var commands = desktop.Menus.Commands;
        commands.Register("shell.open", _ => { BringForward(); return ValueTask.CompletedTask; });
        commands.Register("shell.capture", token => new ValueTask(activations.HandleAsync(LaunchIntent.Capture, token)));
        commands.Register("shell.pad", token => new ValueTask(activations.HandleAsync(LaunchIntent.Pad, token)));
        commands.Register("shell.quit", _ => { RequestQuit(); return ValueTask.CompletedTask; });
        desktop.Tray.Activated += (_, activation) =>
        {
            if (!activation.Secondary)
            {
                BringForward();
            }
        };
        try
        {
            await desktop.Tray.SetAsync(new NeoTrayItemOptions
            {
                Id = TrayId,
                ToolTip = "Chaos",
                IconPath = IconPath,
                Menu =
                [
                    NeoMenuItem.Command("open", "Open Chaos", "shell.open"),
                    NeoMenuItem.Command("capture", "Quick add", "shell.capture"),
                    NeoMenuItem.Command("pad", "New pad note", "shell.pad"),
                    NeoMenuItem.Separator("separator"),
                    NeoMenuItem.Command("quit", "Quit Chaos", "shell.quit"),
                ],
            }, ct);
            lock (_gate)
            {
                _trayReady = true;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            // Without a tray icon, closing the window quits as usual (nothing hides it for good).
            Trace.TraceWarning("Tray icon unavailable: {0}", error.GetType().Name);
        }
    }

    private async Task RegisterShortcutsAsync(ProfileSettings settings, CancellationToken ct)
    {
        var desktop = _desktop;
        if (desktop is null || !_initialized)
        {
            return;
        }

        var shortcuts = desktop.GlobalShortcuts;
        if (!shortcuts.Support.IsSupported)
        {
            lock (_gate)
            {
                _shortcuts =
                [
                    new ShortcutStatus(CaptureId, settings.CaptureShortcut, "Unsupported", "Global shortcuts are not available here. Ctrl+N works inside Chaos."),
                    new ShortcutStatus(PadId, settings.PadShortcut, "Unsupported", "Global shortcuts are not available here. Ctrl+Shift+P works inside Chaos."),
                ];
            }

            return;
        }

        if (Interlocked.Exchange(ref _shortcutHandlerAttached, 1) == 0)
        {
            shortcuts.Activated += (_, id) => _ = activations.HandleAsync(id == PadId ? LaunchIntent.Pad : LaunchIntent.Capture, CancellationToken.None);
        }

        var results = new List<ShortcutStatus>();
        foreach (var (id, accelerator) in new[] { (CaptureId, settings.CaptureShortcut), (PadId, settings.PadShortcut) })
        {
            await shortcuts.UnregisterAsync(id, ct);
            results.Add(accelerator is null
                ? new ShortcutStatus(id, null, "Off", "Turned off.")
                : await RegisterAsync(shortcuts, id, accelerator, ct));
        }

        lock (_gate)
        {
            _shortcuts = [.. results];
        }
    }

    private int _shortcutHandlerAttached;

    private static async Task<ShortcutStatus> RegisterAsync(NeoAstra.Desktop.GlobalShortcuts.NeoGlobalShortcutService shortcuts, string id, string accelerator, CancellationToken ct)
    {
        try
        {
            return await shortcuts.RegisterAsync(id, accelerator, ct) switch
            {
                NeoDesktopStatus.Success => new ShortcutStatus(id, accelerator, "Registered", $"{accelerator} works from any app."),
                NeoDesktopStatus.Conflict => new ShortcutStatus(id, accelerator, "Conflict", $"{accelerator} is already used by another app. Choose another shortcut."),
                NeoDesktopStatus.Unsupported => new ShortcutStatus(id, accelerator, "Unsupported", "Global shortcuts are not available here."),
                var other => new ShortcutStatus(id, accelerator, "Failed", $"Windows did not register {accelerator} ({other})."),
            };
        }
        catch (ArgumentException)
        {
            return new ShortcutStatus(id, accelerator, "Failed", $"{accelerator} is reserved by Windows. Choose another shortcut.");
        }
    }
}
