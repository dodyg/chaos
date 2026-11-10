using System.Text.Json.Serialization;
using Chaos.Application.Abstractions;
using Chaos.Application.Tasks;
using Chaos.Application.Capture;
using Chaos.Application.Containers;
using Chaos.Application.History;
using Chaos.Application.Pad;
using Chaos.Application.Planning;
using Chaos.Application.Queries;
using Chaos.Application.Reminders;
using Chaos.Application.Summaries;
using Chaos.Application.Tags;
using Chaos.Desktop.Platform;
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
        var history = new TaskHistoryCommands(storage.Store, tasks);

        // Reminders are delivered after commit on a background timer (PLAN §11). The engine is
        // declared after storage, so it stops before storage closes.
        var notifications = new InAppNotificationAdapter();
        var reminders = new ReminderCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System, tasks, notifications);
        // Plain summaries are generated on the same background tick at their scheduled times (PLAN §12).
        var summaries = new SummaryCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System);
        using var reminderEngine = new ReminderEngine(reminders, notifications, TimeProvider.System, summaries);
        reminderEngine.Start();

        return NeoApp.Run(args, app =>
        {
            app.Title = "Chaos";
            app.UseRpc(rpc =>
            {
                rpc.AddGreetingService(new GreetingService());
                rpc.AddTaskRpcService(new TaskRpcService(tasks));
                rpc.AddRecurrenceRpcService(new RecurrenceRpcService(new RecurrenceCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System), tasks));
                rpc.AddSubtaskRpcService(new SubtaskRpcService(tasks, new SubtaskCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddContainerRpcService(new ContainerRpcService(new ContainerCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System), tasks));
                rpc.AddTemplateRpcService(new TemplateRpcService(new TemplateCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddTagRpcService(new TagRpcService(new TagCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddQueryRpcService(new QueryRpcService(new TaskQueries(storage.Store, TimeProvider.System)));
                rpc.AddHistoryRpcService(new HistoryRpcService(history));
                rpc.AddDiaryRpcService(new DiaryRpcService(new DiaryCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddUndoRpcService(new UndoRpcService(new UndoCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddMarkdownRpcService(new MarkdownRpcService(new ChecklistCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System), tasks));
                rpc.AddPadRpcService(new PadRpcService(new PadCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddCaptureRpcService(new CaptureRpcService(new CaptureCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddMyDayRpcService(new MyDayRpcService(new MyDayCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddMyWeekRpcService(new MyWeekRpcService(new MyWeekCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddReminderRpcService(new ReminderRpcService(reminders, reminderEngine));
                rpc.AddWaitingRpcService(new WaitingRpcService(new WaitingCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddJournalRpcService(new JournalRpcService(summaries));
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
[JsonSerializable(typeof(RecurrenceRuleDto))]
[JsonSerializable(typeof(RecurrenceGetRequest))]
[JsonSerializable(typeof(RecurrenceOccurrenceDto))]
[JsonSerializable(typeof(RecurrenceGetResponse))]
[JsonSerializable(typeof(RecurrenceRuleRequest))]
[JsonSerializable(typeof(RecurrenceActionRequest))]
[JsonSerializable(typeof(RecurrenceMoveRequest))]
[JsonSerializable(typeof(RecurrencePreviewRequest))]
[JsonSerializable(typeof(RecurrencePreviewResponse))]
[JsonSerializable(typeof(UpcomingRecurrenceRequest))]
[JsonSerializable(typeof(UpcomingRecurrenceDto))]
[JsonSerializable(typeof(UpcomingRecurrenceResponse))]
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
[JsonSerializable(typeof(TemplateReminderOffsetDto))]
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
[JsonSerializable(typeof(HistoryTaskRequest))]
[JsonSerializable(typeof(HistoryEntryResponse))]
[JsonSerializable(typeof(HistoryListResponse))]
[JsonSerializable(typeof(HistoryAsOfRequest))]
[JsonSerializable(typeof(SnapshotSubtaskResponse))]
[JsonSerializable(typeof(TaskSnapshotResponse))]
[JsonSerializable(typeof(HistoryAsOfResponse))]
[JsonSerializable(typeof(RestoreFieldsRequest))]
[JsonSerializable(typeof(DiaryEntryResponse))]
[JsonSerializable(typeof(DiaryListRequest))]
[JsonSerializable(typeof(DiaryListResponse))]
[JsonSerializable(typeof(TimelineItemResponse))]
[JsonSerializable(typeof(TimelineResponse))]
[JsonSerializable(typeof(DiaryAddRequest))]
[JsonSerializable(typeof(DiaryEditRequest))]
[JsonSerializable(typeof(DiaryChangeRequest))]
[JsonSerializable(typeof(DiaryOperationResponse))]
[JsonSerializable(typeof(DiaryRevisionsRequest))]
[JsonSerializable(typeof(DiaryRevisionResponse))]
[JsonSerializable(typeof(DiaryRevisionsResponse))]
[JsonSerializable(typeof(UndoListRequest))]
[JsonSerializable(typeof(UndoEntryResponse))]
[JsonSerializable(typeof(UndoListResponse))]
[JsonSerializable(typeof(UndoRequest))]
[JsonSerializable(typeof(UndoResponse))]
[JsonSerializable(typeof(ChecklistItemRequest))]
[JsonSerializable(typeof(ChecklistConvertRequest))]
[JsonSerializable(typeof(ChecklistConvertResponse))]
[JsonSerializable(typeof(LinkResolveRequest))]
[JsonSerializable(typeof(LinkTargetResponse))]
[JsonSerializable(typeof(LinkResolveResponse))]
[JsonSerializable(typeof(OpenExternalRequest))]
[JsonSerializable(typeof(OpenExternalResponse))]
[JsonSerializable(typeof(PadListRequest))]
[JsonSerializable(typeof(PadTaskLinkResponse))]
[JsonSerializable(typeof(PadItemResponse))]
[JsonSerializable(typeof(PadListResponse))]
[JsonSerializable(typeof(PadGetRequest))]
[JsonSerializable(typeof(PadCreateRequest))]
[JsonSerializable(typeof(PadSaveRequest))]
[JsonSerializable(typeof(PadSaveResponse))]
[JsonSerializable(typeof(PadItemChangeRequest))]
[JsonSerializable(typeof(PadItemsRequest))]
[JsonSerializable(typeof(PadItemRequest))]
[JsonSerializable(typeof(PadReorderRequest))]
[JsonSerializable(typeof(PadOperationResponse))]
[JsonSerializable(typeof(PadMakeTaskRequest))]
[JsonSerializable(typeof(PadMakeTaskResponse))]
[JsonSerializable(typeof(PadLocateRequest))]
[JsonSerializable(typeof(PadAnchorResponse))]
[JsonSerializable(typeof(PadLocateResponse))]
[JsonSerializable(typeof(PadSearchRequest))]
[JsonSerializable(typeof(PadResolveRequest))]
[JsonSerializable(typeof(PadLinkTargetResponse))]
[JsonSerializable(typeof(PadResolveResponse))]
[JsonSerializable(typeof(PadReviewStatusRequest))]
[JsonSerializable(typeof(PadReviewStatusResponse))]
[JsonSerializable(typeof(PadReviewRequest))]
[JsonSerializable(typeof(CaptureSpanDto))]
[JsonSerializable(typeof(CapturePreviewRequest))]
[JsonSerializable(typeof(CaptureTokenDto))]
[JsonSerializable(typeof(CaptureWarningDto))]
[JsonSerializable(typeof(CaptureReminderDto))]
[JsonSerializable(typeof(CaptureTagDto))]
[JsonSerializable(typeof(CaptureContainerDto))]
[JsonSerializable(typeof(CaptureSubtaskDto))]
[JsonSerializable(typeof(CaptureDraftDto))]
[JsonSerializable(typeof(CaptureItemDto))]
[JsonSerializable(typeof(CaptureBatchErrorDto))]
[JsonSerializable(typeof(CapturePreviewResponse))]
[JsonSerializable(typeof(CaptureCreateRequest))]
[JsonSerializable(typeof(CaptureCreateResponse))]
[JsonSerializable(typeof(DayClockResponse))]
[JsonSerializable(typeof(DayClockRequest))]
[JsonSerializable(typeof(DayClockResult))]
[JsonSerializable(typeof(DaySummaryResponse))]
[JsonSerializable(typeof(DayOpenRequest))]
[JsonSerializable(typeof(DayOpenResponse))]
[JsonSerializable(typeof(DaySuggestionsRequest))]
[JsonSerializable(typeof(DaySuggestionResponse))]
[JsonSerializable(typeof(DaySuggestionsResponse))]
[JsonSerializable(typeof(DayItemRequest))]
[JsonSerializable(typeof(DayMoveRequest))]
[JsonSerializable(typeof(DayCarryOverRequest))]
[JsonSerializable(typeof(DayChangeResponse))]
[JsonSerializable(typeof(WeekTaskDto))]
[JsonSerializable(typeof(WeekPlacementDto))]
[JsonSerializable(typeof(WeekGoalDto))]
[JsonSerializable(typeof(WeekDayDto))]
[JsonSerializable(typeof(WeekLoadDto))]
[JsonSerializable(typeof(WeekBoardDto))]
[JsonSerializable(typeof(WeekDraftGoalDto))]
[JsonSerializable(typeof(WeekDraftAssignmentDto))]
[JsonSerializable(typeof(WeekDraftLeftoverDto))]
[JsonSerializable(typeof(WeekDraftOverrideDto))]
[JsonSerializable(typeof(WeekDraftDto))]
[JsonSerializable(typeof(WeekLookBackDto))]
[JsonSerializable(typeof(WeekCandidateDto))]
[JsonSerializable(typeof(WeekGoalSuggestionDto))]
[JsonSerializable(typeof(DayReconciliationDto))]
[JsonSerializable(typeof(WeekSlipDto))]
[JsonSerializable(typeof(WeekCheckInDto))]
[JsonSerializable(typeof(WeekOverviewDto))]
[JsonSerializable(typeof(WeekOpenRequest))]
[JsonSerializable(typeof(WeekOpenResponse))]
[JsonSerializable(typeof(WeekStartDraftRequest))]
[JsonSerializable(typeof(WeekSaveDraftRequest))]
[JsonSerializable(typeof(WeekRevisionRequest))]
[JsonSerializable(typeof(WeekReconcileRequest))]
[JsonSerializable(typeof(WeekGoalOutcomeRequest))]
[JsonSerializable(typeof(AvailabilityDayDto))]
[JsonSerializable(typeof(WeekAvailabilityRequest))]
[JsonSerializable(typeof(WeekChangeResponse))]
[JsonSerializable(typeof(WeekStartPreviewRequest))]
[JsonSerializable(typeof(WeekStartConflictDto))]
[JsonSerializable(typeof(WeekStartPreviewResponse))]
[JsonSerializable(typeof(WeekStartChangeRequest))]
[JsonSerializable(typeof(WeekStartChangeResponse))]
[JsonSerializable(typeof(ReminderRuleDto))]
[JsonSerializable(typeof(ReminderRuleResponse))]
[JsonSerializable(typeof(ReminderListRequest))]
[JsonSerializable(typeof(ReminderListResponse))]
[JsonSerializable(typeof(ReminderAddRequest))]
[JsonSerializable(typeof(ReminderUpdateRequest))]
[JsonSerializable(typeof(ReminderDeleteRequest))]
[JsonSerializable(typeof(ReminderOperationResponse))]
[JsonSerializable(typeof(NotificationCapabilitiesResponse))]
[JsonSerializable(typeof(ReminderSettingsRequest))]
[JsonSerializable(typeof(ReminderSettingsResponse))]
[JsonSerializable(typeof(ReminderSettingsUpdateRequest))]
[JsonSerializable(typeof(NotificationItemResponse))]
[JsonSerializable(typeof(NotificationListRequest))]
[JsonSerializable(typeof(NotificationListResponse))]
[JsonSerializable(typeof(NotificationActRequest))]
[JsonSerializable(typeof(NotificationActResponse))]
[JsonSerializable(typeof(FollowUpDto))]
[JsonSerializable(typeof(WaitingRequest))]
[JsonSerializable(typeof(WaitingResponse))]
[JsonSerializable(typeof(WaitingSetRequest))]
[JsonSerializable(typeof(WaitingClearRequest))]
[JsonSerializable(typeof(WaitingLogRequest))]
[JsonSerializable(typeof(FollowUpSnoozeRequest))]
[JsonSerializable(typeof(FollowUpDueRequest))]
[JsonSerializable(typeof(FollowUpItemResponse))]
[JsonSerializable(typeof(FollowUpDueResponse))]
[JsonSerializable(typeof(SummaryTaskDto))]
[JsonSerializable(typeof(CompletionDto))]
[JsonSerializable(typeof(ProjectProgressDto))]
[JsonSerializable(typeof(UnfinishedDto))]
[JsonSerializable(typeof(WaitingFactDto))]
[JsonSerializable(typeof(DiaryFactDto))]
[JsonSerializable(typeof(SuggestionDto))]
[JsonSerializable(typeof(GoalFactDto))]
[JsonSerializable(typeof(WeekProgressDto))]
[JsonSerializable(typeof(FollowUpFactDto))]
[JsonSerializable(typeof(SummaryFactsDto))]
[JsonSerializable(typeof(SummaryCountsDto))]
[JsonSerializable(typeof(SummaryHeaderDto))]
[JsonSerializable(typeof(SummaryRevisionDto))]
[JsonSerializable(typeof(SummaryDetailDto))]
[JsonSerializable(typeof(JournalListRequest))]
[JsonSerializable(typeof(JournalListResponse))]
[JsonSerializable(typeof(JournalGetRequest))]
[JsonSerializable(typeof(JournalFindRequest))]
[JsonSerializable(typeof(JournalDetailResponse))]
[JsonSerializable(typeof(JournalGenerateRequest))]
[JsonSerializable(typeof(JournalPromptsRequest))]
[JsonSerializable(typeof(JournalPromptsResponse))]
[JsonSerializable(typeof(JournalDismissRequest))]
[JsonSerializable(typeof(JournalDismissResponse))]
[JsonSerializable(typeof(CarryOverPreviewRequest))]
[JsonSerializable(typeof(CarryOverCandidateDto))]
[JsonSerializable(typeof(CarryOverPreviewResponse))]
[JsonSerializable(typeof(CarryOverRequest))]
[JsonSerializable(typeof(CarryOverResponse))]
[JsonSerializable(typeof(SummarySettingsRequest))]
[JsonSerializable(typeof(SummarySettingsResponse))]
[JsonSerializable(typeof(SummarySettingsUpdateRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AppJsonContext : JsonSerializerContext;
