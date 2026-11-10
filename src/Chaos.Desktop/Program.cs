using System.Text.Json.Serialization;
using Chaos.Application.Abstractions;
using Chaos.Application.Tasks;
using Chaos.Application.Containers;
using Chaos.Application.Queries;
using Chaos.Application.Tags;
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
                rpc.AddSubtaskRpcService(new SubtaskRpcService(tasks, new SubtaskCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddContainerRpcService(new ContainerRpcService(new ContainerCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System), tasks));
                rpc.AddTemplateRpcService(new TemplateRpcService(new TemplateCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddTagRpcService(new TagRpcService(new TagCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddQueryRpcService(new QueryRpcService(new TaskQueries(storage.Store, TimeProvider.System)));
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
[JsonSerializable(typeof(ListTasksRequest))]
[JsonSerializable(typeof(ListDeletedTasksRequest))]
[JsonSerializable(typeof(ListTasksResponse))]
[JsonSerializable(typeof(EditTaskRequest))]
[JsonSerializable(typeof(ChangeTaskStatusRequest))]
[JsonSerializable(typeof(ReorderTaskRequest))]
[JsonSerializable(typeof(DeleteTaskRequest))]
[JsonSerializable(typeof(SubtaskListRequest))]
[JsonSerializable(typeof(SubtaskListResponse))]
[JsonSerializable(typeof(SubtaskAddRequest))]
[JsonSerializable(typeof(SubtaskEditRequest))]
[JsonSerializable(typeof(SubtaskStatusRequest))]
[JsonSerializable(typeof(SubtaskOrderRequest))]
[JsonSerializable(typeof(SubtaskRestoreRequest))]
[JsonSerializable(typeof(SubtaskOperationResponse))]
[JsonSerializable(typeof(SubtaskResponse))]
[JsonSerializable(typeof(ContainerListRequest))]
[JsonSerializable(typeof(ContainerListResponse))]
[JsonSerializable(typeof(ContainerResponse))]
[JsonSerializable(typeof(ContainerCreateRequest))]
[JsonSerializable(typeof(ContainerChangeRequest))]
[JsonSerializable(typeof(ContainerOperationResponse))]
[JsonSerializable(typeof(ContainerTasksRequest))]
[JsonSerializable(typeof(MoveTaskRequest))]
[JsonSerializable(typeof(SectionResponse))]
[JsonSerializable(typeof(SectionListResponse))]
[JsonSerializable(typeof(SectionCreateRequest))]
[JsonSerializable(typeof(SectionOperationResponse))]
[JsonSerializable(typeof(ContainerOrderRequest))]
[JsonSerializable(typeof(SectionChangeRequest))]
[JsonSerializable(typeof(DeadlineRequest))]
[JsonSerializable(typeof(ProgressResponse))]
[JsonSerializable(typeof(ContainerDeleteRequest))]
[JsonSerializable(typeof(TemplateListRequest))]
[JsonSerializable(typeof(TemplateListResponse))]
[JsonSerializable(typeof(TemplateResponse))]
[JsonSerializable(typeof(TemplatePayloadResponse))]
[JsonSerializable(typeof(TemplatePartResponse))]
[JsonSerializable(typeof(TemplateSaveRequest))]
[JsonSerializable(typeof(TemplateDeleteRequest))]
[JsonSerializable(typeof(TemplateInstantiateRequest))]
[JsonSerializable(typeof(TemplateOperationResponse))]
[JsonSerializable(typeof(TagListRequest))]
[JsonSerializable(typeof(TagSuggestRequest))]
[JsonSerializable(typeof(TagResponse))]
[JsonSerializable(typeof(TagListResponse))]
[JsonSerializable(typeof(TagCreateRequest))]
[JsonSerializable(typeof(TagRenameRequest))]
[JsonSerializable(typeof(TagColorRequest))]
[JsonSerializable(typeof(TagMoveRequest))]
[JsonSerializable(typeof(TagChangeRequest))]
[JsonSerializable(typeof(TagMergePreviewRequest))]
[JsonSerializable(typeof(TagMergeRequest))]
[JsonSerializable(typeof(TagMergePreviewResponse))]
[JsonSerializable(typeof(TagOperationResponse))]
[JsonSerializable(typeof(TagTargetRequest))]
[JsonSerializable(typeof(TagLinkRequest))]
[JsonSerializable(typeof(TagUnlinkRequest))]
[JsonSerializable(typeof(TagIdsResponse))]
[JsonSerializable(typeof(TaskQueryRequest))]
[JsonSerializable(typeof(QueryRowResponse))]
[JsonSerializable(typeof(TaskQueryResponse))]
[JsonSerializable(typeof(ViewCountsRequest))]
[JsonSerializable(typeof(ViewCountResponse))]
[JsonSerializable(typeof(TagCountResponse))]
[JsonSerializable(typeof(ViewCountsResponse))]
[JsonSerializable(typeof(RebuildSearchRequest))]
[JsonSerializable(typeof(RebuildSearchResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AppJsonContext : JsonSerializerContext;
