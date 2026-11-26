using Chaos.Desktop;
using Chaos.Desktop.Platform;
using Chaos.Desktop.Windows.Activation;
using NeoAstra;
using NeoAstra.Rpc;

internal static class Program
{
#if DEBUG
    private const bool IsDevelopmentBuild = true;
#else
    private const bool IsDevelopmentBuild = false;
#endif

    [STAThread]
    private static int Main(string[] args)
    {
        // One running Chaos per user and data root (PLAN §5/§13). A second launch (shortcut,
        // Start menu, notification button) hands its arguments to the running instance and exits
        // without touching storage.
        var dataRoot = ResolveDataRoot(args);
        var instanceKey = SingleInstanceGuard.KeyFor(dataRoot);
        if (args.Contains("--mcp", StringComparer.Ordinal))
        {
            // The MCP client config gives the shim its agent token in the environment (Settings → Agents).
            var token = Environment.GetEnvironmentVariable("CHAOS_AGENT_TOKEN");
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("CHAOS_AGENT_TOKEN is not set. Copy the MCP configuration from Settings → Agents.");
                return 2;
            }

            return McpRelayHost.RelayAsync(instanceKey, dataRoot, token, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error).GetAwaiter().GetResult();
        }
        if (args.Contains("--mcp-host", StringComparer.Ordinal))
        {
            // Started by the shim when Chaos is not running: it owns the storage lock like the app, serves one assistant
            // session without a window, and exits when that session ends. If the app is already running, it has the pipe.
            using var hostGuard = new SingleInstanceGuard(instanceKey);
            if (!hostGuard.IsPrimaryInstance)
            {
                return 0;
            }

            using var hostComposition = DesktopComposition.Create(dataRoot, AppVersion(), IsDevelopmentBuild);
            var host = McpRelayHost.Start(instanceKey, hostComposition.McpGate, singleSession: true);
            try { host.Completion.GetAwaiter().GetResult(); }
            finally { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            return 0;
        }
        using var instance = new SingleInstanceGuard(instanceKey);
        if (!instance.IsPrimaryInstance)
        {
            return LaunchRouter.SendAsync(instanceKey, args, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult() ? 0 : 3;
        }

        // Storage, every service and every background worker (see DesktopComposition).
        using var composition = DesktopComposition.Create(dataRoot, AppVersion(), IsDevelopmentBuild);
        var mcpRelay = McpRelayHost.Start(instanceKey, composition.McpGate);

        // The desktop shell (PLAN §13): tray, global shortcuts, close-to-tray and activation.
        var activations = composition.Activations;
        var initialSettings = composition.ProfileSettings.GetAsync(CancellationToken.None).GetAwaiter().GetResult();
        var shell = new DesktopShell(activations, AppVersion(), Path.Combine(dataRoot, "desktop"));
        var router = LaunchRouter.Listen(instanceKey, launch =>
        {
            _ = activations.HandleAsync(LaunchIntents.FromArguments(launch), CancellationToken.None);
            return true;
        });
        var initialIntent = LaunchIntents.FromArguments(args);
        if (initialIntent.Kind != LaunchIntentKind.Open)
        {
            // Queued until the page takes it; a reminder button that started Chaos acts now.
            _ = activations.HandleAsync(initialIntent, CancellationToken.None);
        }

        try
        {
            return NeoApp.Run(args, app =>
            {
                app.Title = "Chaos";
                app.UsePlugins(shell.UsePlugins);
                app.ConfigureMainWindow((application, window) => shell.ConfigureMainWindow(application, window, initialSettings));
                app.UseRpc(rpc => composition.AddRpcServices(rpc, shell));
            });
        }
        finally
        {
            router.DisposeAsync().AsTask().GetAwaiter().GetResult();
            mcpRelay.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>Default data root per platform layout (PLAN §5); development and tests pass --data-dir.</summary>
    private static string ResolveDataRoot(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index] is "--data-dir" or "--dataDir")
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Chaos");
    }

    private static string AppVersion() => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
