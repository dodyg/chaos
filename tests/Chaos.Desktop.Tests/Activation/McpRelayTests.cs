using System.IO.Pipes;
using System.Text.Json;
using Chaos.Desktop.Platform;

namespace Chaos.Desktop.Tests.Activation;

public sealed class McpRelayTests
{
    [Test]
    public async Task Pipe_InitializedClient_ExposesNoAgentTools()
    {
        var key = Guid.NewGuid().ToString("N");
        await using var host = McpRelayHost.Start(key);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await CheckSessionAsync(key, deadline.Token);
        await CheckSessionAsync(key, deadline.Token);
    }

    private static async Task CheckSessionAsync(string key, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", McpRelayHost.PipeName(key), PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(ct);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""");
        using var initialized = JsonDocument.Parse(await reader.ReadLineAsync(ct) ?? throw new IOException("MCP initialization returned EOF."));
        await Assert.That(initialized.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString()).IsEqualTo("2025-03-26");
        await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await writer.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");
        using var tools = JsonDocument.Parse(await reader.ReadLineAsync(ct) ?? throw new IOException("MCP tools list returned EOF."));
        await Assert.That(tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength()).IsEqualTo(0);
    }
}
