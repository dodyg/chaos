using System.Text.Json.Serialization;
using Chaos.Application.Abstractions;
using Chaos.Application.Tasks;
using Chaos.Desktop.Services;
using Chaos.Infrastructure.Bootstrap;
using NeoAstra;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(AppJsonContext))]

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Storage starts before the window: lock the data root, back up before upgrades,
        // migrate, seed (PLAN §5). This is the one synchronous storage boundary; every
        // request-time database call runs on NeoAstra's background RPC dispatch threads.
        using var storage = StorageBootstrap
            .StartAsync(StorageOptions.FromDataRoot(ResolveDataRoot(args)), AppVersion(), new GuidIdGenerator(), TimeProvider.System, CancellationToken.None)
            .GetAwaiter().GetResult();

        var tasks = new TaskCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System);

        return NeoApp.Run(args, app =>
        {
            app.Title = "Chaos";
            app.UseRpc(rpc =>
            {
                rpc.AddGreetingService(new GreetingService());
                rpc.AddTaskRpcService(new TaskRpcService(tasks));
            });
        });
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

[NeoRpcService("greeting", Version = 1)]
public sealed class GreetingService
{
    [NeoRpcMethod("hello")]
    public ValueTask<GreetingResponse> HelloAsync(GreetingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new GreetingResponse($"Hello, {request.Name}!"));
    }
}

public sealed record GreetingRequest(string Name);
public sealed record GreetingResponse(string Message);

[JsonSerializable(typeof(GreetingRequest))]
[JsonSerializable(typeof(GreetingResponse))]
[JsonSerializable(typeof(CreateTaskRequest))]
[JsonSerializable(typeof(GetTaskRequest))]
[JsonSerializable(typeof(TaskOperationResponse))]
[JsonSerializable(typeof(TaskGetResponse))]
[JsonSerializable(typeof(TaskResponse))]
[JsonSerializable(typeof(TaskErrorResponse))]
[JsonSerializable(typeof(TaskErrorFieldResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AppJsonContext : JsonSerializerContext;
