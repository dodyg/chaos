using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;

// Isolated relay prototype. This is not Chaos.Desktop and does not authenticate an agent.
const string pipeName = "chaos-phase3-spike-relay";
if (args is ["--host"])
{
    using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await server.WaitForConnectionAsync();
    using var reader = new StreamReader(server, leaveOpen: true);
    using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };
    var request = await reader.ReadLineAsync();
    await writer.WriteLineAsync(request);
    return;
}

using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
try
{
    await client.ConnectAsync(200);
}
catch (TimeoutException)
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No process path.");
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--host");
    using var host = Process.Start(start) ?? throw new InvalidOperationException("Host did not start.");
    await client.ConnectAsync(10_000);
}
using var input = new StreamReader(client, leaveOpen: true);
using var output = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
var line = await Console.In.ReadLineAsync() ?? throw new InvalidOperationException("Expected one input line.");
await output.WriteLineAsync(line);
Console.WriteLine(await input.ReadLineAsync());
