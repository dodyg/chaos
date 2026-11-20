using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Chaos.Desktop.Platform;

/// <summary>A local transport-only MCP endpoint. Agent tools and authorization arrive in Q18.</summary>
public sealed class McpRelayHost : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;

    private McpRelayHost(string instanceKey, bool singleSession)
    {
        _server = ServeAsync(PipeName(instanceKey), singleSession, _stop.Token);
    }

    public static string PipeName(string instanceKey) => "Chaos.Mcp." + instanceKey;

    public static McpRelayHost Start(string instanceKey, bool singleSession = false) => new(instanceKey, singleSession);

    public Task Completion => _server;

    private static async Task ServeAsync(string name, bool singleSession, CancellationToken ct)
    {
        do
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                await using var transport = new StreamServerTransport(pipe, pipe, "Chaos local relay");
                var options = new McpServerOptions
                {
                    ServerInfo = new Implementation { Name = "Chaos", Version = typeof(McpRelayHost).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
                    ServerInstructions = "This transport does not expose agent tools yet.",
                    ToolCollection = []
                };
                await using var server = McpServer.Create(transport, options);
                await server.RunAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (IOException error)
            {
                Trace.TraceWarning("MCP pipe disconnected: {0}", error.GetType().Name);
            }
        } while (!singleSession && !ct.IsCancellationRequested);
    }

    public static async Task<int> RelayAsync(string instanceKey, string dataRoot, Stream input, Stream output, CancellationToken ct = default)
    {
        await using var pipe = new NamedPipeClientStream(".", PipeName(instanceKey), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await pipe.ConnectAsync(200, ct);
        }
        catch (TimeoutException)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the desktop executable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--mcp-host");
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(dataRoot);
            using var host = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the MCP host.");
            try
            {
                await pipe.ConnectAsync(10_000, ct);
            }
            catch
            {
                if (!host.HasExited) host.Kill(entireProcessTree: true);
                throw;
            }
        }

        using var relayStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var inbound = input.CopyToAsync(pipe, relayStop.Token);
        var outbound = pipe.CopyToAsync(output, relayStop.Token);
        var completed = await Task.WhenAny(inbound, outbound);
        await relayStop.CancelAsync();
        await completed;
        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _server;
        _stop.Dispose();
    }
}
