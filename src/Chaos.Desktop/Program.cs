using Chaos.Application.GitHub;
using Chaos.Application.Activity;
using Chaos.Infrastructure.GitHub;
using System.Text.Json.Serialization;
using Chaos.Application.Abstractions;
using Chaos.Application.Tasks;
using Chaos.Application.Boards;
using Chaos.Application.Calendar;
using Chaos.Infrastructure.Calendar;
using Chaos.Application.Views;
using Chaos.Application.Capture;
using Chaos.Application.Containers;
using Chaos.Application.History;
using Chaos.Application.Pad;
using Chaos.Application.Pages;
using Chaos.Application.Sites;
using Chaos.Infrastructure.Sites;
using Chaos.Application.Planning;
using Chaos.Application.Queries;
using Chaos.Application.Reminders;
using Chaos.Application.Settings;
using Chaos.Application.Summaries;
using Chaos.Application.Tags;
using Chaos.Application.Transfer;
using Chaos.Desktop.Platform;
using Chaos.Desktop.Services;
using Chaos.Desktop.Windows.Activation;
using Chaos.Desktop.Windows.Notifications;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Transfer;
using NeoAstra;
using NeoAstra.Rpc;

[assembly: NeoRpcJsonContext(typeof(AppJsonContext))]

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
        using var instance = new SingleInstanceGuard(instanceKey);
        if (!instance.IsPrimaryInstance)
        {
            return LaunchRouter.SendAsync(instanceKey, args, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult() ? 0 : 3;
        }

        // Storage starts before the window: lock the data root, back up before upgrades,
        // migrate, seed (PLAN §5). This is the one synchronous storage boundary; every
        // request-time database call runs on NeoAstra's background RPC dispatch threads.
        using var storage = StorageBootstrap
            .StartAsync(StorageOptions.FromDataRoot(dataRoot), AppVersion(), new GuidIdGenerator(), TimeProvider.System, CancellationToken.None)
            .GetAwaiter().GetResult();

        // The activity log (PLAN2 §6.4): the store reports each commit that wrote activity, the publisher coalesces and pushes it to the window.
        using var activityPublisher = new ActivityPublisher(TimeProvider.System);
        storage.Store.ActivityNotifier = activityPublisher;
        var activityFeed = new Chaos.Infrastructure.Activity.ActivityFeed(storage.Store, TimeProvider.System);
        var jobRuns = new Chaos.Infrastructure.Activity.JobRuns(storage.Store, TimeProvider.System);

        var tasks = new TaskCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System);
        var boards = new BoardCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System);
        var boardQueries = new BoardQueries(storage.Store);
        var taskQueries = new TaskQueries(storage.Store, TimeProvider.System);
        var viewCommands = new SavedViewCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System);
        var viewQueries = new SavedViewQueries(storage.Store, taskQueries, TimeProvider.System);
        var viewBoardQueries = new ViewBoardQueries(storage.Store, taskQueries);
        var calendarQueries = new CalendarQueries(storage.Store, taskQueries, TimeProvider.System);
        var viewAlerts = new ViewAlertService(storage.Store, new GuidIdGenerator(), TimeProvider.System);
        var history = new TaskHistoryCommands(storage.Store, tasks);

        // Reminders are delivered after commit on a background timer (PLAN §11). The installed
        // package hands them to Windows (scheduled, with buttons); an unpackaged run shows them
        // in the app. The engine is declared after storage, so it stops before storage closes.
        var notifications = (INotificationAdapter?)WindowsToastAdapter.TryCreate(TimeProvider.System) ?? new InAppNotificationAdapter();
        var reminders = new ReminderCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System, tasks, notifications);
        // Plain summaries are generated on the same background tick at their scheduled times (PLAN §12).
        var summaries = new SummaryCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System);
        using var reminderEngine = new ReminderEngine(reminders, notifications, TimeProvider.System, summaries, viewAlerts, jobRuns);
        reminderEngine.Start();

        // Backups, restore, export, import and deletion (PLAN §14). Restore and reset pause the
        // reminder engine and take back OS notification schedules before files are replaced.
        // Attachment bytes live behind IAttachmentFileStore (PLAN2 §9.1); this is the one place the provider is chosen.
        var attachmentFiles = Chaos.Infrastructure.Attachments.AttachmentFileStoreFactory.Create(storage.Options);
        var attachmentCommands = new Chaos.Application.Attachments.AttachmentCommands(storage.Store, attachmentFiles, new GuidIdGenerator(), TimeProvider.System, storage.Options.UploadsDirectory,
            images: new Chaos.Infrastructure.Attachments.SkiaImageProcessor(), thumbnailsDirectory: storage.Options.ThumbnailsDirectory);
        var protection = new DataProtectionService(storage.Store, storage.Options,
            new BackupManager(storage.Store.Source, storage.Options.BackupsDirectory, storage.AppVersion, TimeProvider.System, null,
                attachmentFiles, Chaos.Infrastructure.Attachments.AttachmentFileStoreFactory.CreateBackupStore(storage.Options)), new GuidIdGenerator(), TimeProvider.System);
        protection.Quiesce = (work, ct) => reminderEngine.RunPausedAsync(async () =>
        {
            foreach (var osId in await protection.ListScheduledOsNotificationIdsAsync(ct))
            {
                await notifications.CancelAsync(osId, ct);
            }

            await work();
        }, ct);
        using var backupScheduler = new BackupScheduler(protection, TimeProvider.System, jobRuns, activityFeed);
        backupScheduler.Start();
        var dataRpc = new DataRpcService(protection, new DataTransfer(storage.Store, storage.AppVersion, TimeProvider.System, attachmentFiles),
            new ImportCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System), new DataPurge(storage.Store, attachmentFiles), storage.RecoveryNotice,
            async (osIds, ct) =>
            {
                foreach (var osId in osIds)
                {
                    await notifications.CancelAsync(osId, ct);
                }
            });

        // The desktop shell (PLAN §13): tray, global shortcuts, close-to-tray and activation.
        using var githubApi = new GitHubCliClient();
        using var githubIssues = new GitHubIssueFetcher(storage.Store, githubApi, new GitHubInboundCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System),
            async ct => (await protection.BackupNowAsync(Chaos.Application.Contracts.BackupReason.PreImport, ct)).FileName, TimeProvider.System);
        using var githubComments = new GitHubCommentFetcher(storage.Store, githubApi, new GitHubInboundComments(storage.Store, new GuidIdGenerator(), TimeProvider.System), TimeProvider.System);
        using var githubEngine = new GitHubSyncEngine(new GitHubSyncCommands(storage.Store, githubApi, githubIssues, githubComments, new GuidIdGenerator(), TimeProvider.System), TimeProvider.System, TimeSpan.FromSeconds(10), jobRuns);
        var pauseReminders = protection.Quiesce;
        protection.Quiesce = (work, ct) => githubEngine.RunPausedAsync(() => pauseReminders(work, ct), ct);
        var github = new GitHubConnectionCommands(storage.Store, githubApi, new GuidIdGenerator(), TimeProvider.System);
        using var githubScheduler = new GitHubSyncScheduler(githubEngine, github.ScheduleAsync, TimeProvider.System);
        githubScheduler.Start();
        var pauseGitHub = protection.Quiesce;
        protection.Quiesce = async (work, ct) =>
        {
            try { await pauseGitHub(work, ct); }
            finally { githubScheduler.Wake(); }
        };
        // Calendar feeds (PLAN2 P5): addresses live in the protected secret store, never in the database.
        var secrets = new FileSecretStore(Path.Combine(dataRoot, "secrets"), new Chaos.Desktop.Windows.Security.DpapiSecretProtector());
        using var feedClient = new CalendarFeedClient();
        // Google Calendar (PLAN2 P6): the OAuth client comes from google-oauth.json beside the exe (git-ignored); tokens go to the secret store only.
        using var googleClient = new GoogleCalendarClient(GoogleOAuthConfig.TryLoad(Path.Combine(AppContext.BaseDirectory, "google-oauth.json")), new Chaos.Desktop.Platform.ShellBrowser());
        // Online service (PLAN2 P12): the address is typed in Settings → Account (or comes from CHAOS_SERVICE_URL or chaos-service.json beside the exe); without one Settings
        // offers no sign-in. Plain http is accepted for loopback in development builds only. The refresh token goes to the secret store.
        var serviceAddress = new Chaos.Infrastructure.Service.ServiceAddressStore(
            Path.Combine(dataRoot, "service-address.json"), Path.Combine(AppContext.BaseDirectory, "chaos-service.json"), allowLoopbackHttp: IsDevelopmentBuild);
        using var serviceClient = new Chaos.Infrastructure.Service.ChaosServiceClient(
            serviceAddress.Resolve,
            new Chaos.Desktop.Platform.ShellBrowser(), secrets, new Chaos.Infrastructure.Service.SqliteDeviceIdentity(storage.Store, AppVersion()));
        // Sync (PLAN2 P13): the loop idles until SyncState.Enabled = 1 (turned on by joining an account, P14); it never touches the UI thread.
        using var syncEngine = new Chaos.Infrastructure.Sync.SyncEngine(storage.Store,
            new Chaos.Infrastructure.Sync.SyncApplier(storage.Store, TimeProvider.System, new GuidIdGenerator()), serviceClient, TimeProvider.System);
        syncEngine.Start();
        // Attachment bytes (PLAN2 P16): uploads follow the metadata, downloads follow the 5 MiB / pinned / on-open rules. A finished sync cycle wakes it.
        using var attachmentTransfer = new Chaos.Infrastructure.Attachments.AttachmentTransfer(storage.Store, attachmentFiles, serviceClient, TimeProvider.System);
        attachmentCommands.Availability = attachmentTransfer;
        syncEngine.CycleFinished += _ => attachmentTransfer.Wake();
        attachmentTransfer.Start();
        var syncStatus = new Chaos.Infrastructure.Sync.SyncStatusReader(storage.Store, syncEngine);
        var accountJoin = new Chaos.Infrastructure.Service.AccountJoinService(serviceClient, new Chaos.Infrastructure.Sync.SyncAccountState(storage.Store), protection, TimeProvider.System, syncEngine.Wake);
        var calendarRefresher = new CalendarRefresher(storage.Store, secrets, feedClient, new IcsParser(), TimeProvider.System, googleClient);
        var googleCommands = new GoogleCalendarCommands(storage.Store, secrets, googleClient, TimeProvider.System);
        googleCommands.LoadSavedClientAsync().GetAwaiter().GetResult();
        var calendarCommands = new CalendarCommands(storage.Store, secrets, new GuidIdGenerator(), TimeProvider.System, new CaptureCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System));
        using var calendarScheduler = new CalendarRefreshScheduler(calendarRefresher, TimeProvider.System, jobRuns);
        calendarScheduler.Start();
        var pauseForCalendars = protection.Quiesce;
        protection.Quiesce = (work, ct) => calendarRefresher.RunPausedAsync(() => pauseForCalendars(work, ct), ct);
        var activations = new ShellActivations((command, ct) => reminders.ActAsync(command, ct));
        var profileSettings = new ProfileSettingsCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System);
        var initialSettings = profileSettings.GetAsync(CancellationToken.None).GetAwaiter().GetResult();
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

        // "New site" (pad): pages are fetched here and summarized by a small local model (Ollama).
        using var siteFetcher = new HttpSitePageFetcher();
        // Falls back to a model-free extractive summary when Ollama is not running.
        using var ollamaSummarizer = new OllamaSiteSummarizer();
        var siteSummarizer = new FallbackSiteSummarizer(ollamaSummarizer, new ExtractiveSiteSummarizer());
        using var releaseFeed = new Chaos.Infrastructure.Updates.GitHubReleaseFeed();

        try
        {
            return RunWindow();
        }
        finally
        {
            router.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        int RunWindow() => NeoApp.Run(args, app =>
        {
            app.Title = "Chaos";
            app.UsePlugins(shell.UsePlugins);
            app.ConfigureMainWindow((application, window) => shell.ConfigureMainWindow(application, window, initialSettings));
            app.UseRpc(rpc =>
            {
                var committed = rpc.AddActivityEventsCommittedEvent();
                activityPublisher.Attach(payload => committed.PublishAsync(payload).AsTask());
                rpc.AddActivityRpcService(new ActivityRpcService(activityFeed));
                rpc.AddShellRpcService(new ShellRpcService(activations, shell, profileSettings, AppVersion(), reminderEngine.ResyncAsync));
                rpc.AddUpdatesRpcService(new UpdatesRpcService(releaseFeed, new Chaos.Desktop.Platform.ShellBrowser(), AppVersion()));
                rpc.AddTaskRpcService(new TaskRpcService(tasks, githubEngine));
                rpc.AddRecurrenceRpcService(new RecurrenceRpcService(new RecurrenceCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System), tasks));
                rpc.AddSubtaskRpcService(new SubtaskRpcService(tasks, new SubtaskCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System), githubEngine));
                rpc.AddContainerRpcService(new ContainerRpcService(new ContainerCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System), tasks));
                rpc.AddTemplateRpcService(new TemplateRpcService(new TemplateCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddTagRpcService(new TagRpcService(new TagCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System), githubEngine));
                rpc.AddQueryRpcService(new QueryRpcService(taskQueries));
                rpc.AddViewRpcService(new ViewRpcService(viewCommands, viewQueries, viewAlerts));
                rpc.AddBoardRpcService(new BoardRpcService(boards, boardQueries, viewBoardQueries, githubEngine));
                rpc.AddCalendarRpcService(new CalendarRpcService(calendarQueries));
                rpc.AddCalendarsRpcService(new CalendarsRpcService(calendarCommands, calendarRefresher));
                rpc.AddGoogleCalendarRpcService(new GoogleCalendarRpcService(googleCommands, calendarRefresher));
                rpc.AddSyncStatusRpcService(new SyncStatusRpcService(syncStatus, syncEngine));
                rpc.AddAccountRpcService(new AccountRpcService(serviceClient, serviceAddress, accountJoin));
                rpc.AddHistoryRpcService(new HistoryRpcService(history, githubEngine));
                rpc.AddDiaryRpcService(new DiaryRpcService(new DiaryCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System), githubEngine));
                rpc.AddUndoRpcService(new UndoRpcService(new UndoCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddMarkdownRpcService(new MarkdownRpcService(new ChecklistCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System), tasks, sync: githubEngine));
                rpc.AddPadRpcService(new PadRpcService(new PadCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddSiteRpcService(new SiteRpcService(new SiteNoteCommands(siteFetcher, siteSummarizer, new PadCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System), new TagCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System))));
                rpc.AddPageRpcService(new PageRpcService(new PageCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddAttachmentRpcService(new AttachmentRpcService(attachmentCommands, AttachmentRpcService.DefaultOpenedDirectory, () => dataRpc.ExportDirectory, transfer: attachmentTransfer));
                rpc.AddCaptureRpcService(new CaptureRpcService(new CaptureCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddMyDayRpcService(new MyDayRpcService(new MyDayCommands(storage.Store, TimeProvider.System)));
                rpc.AddMyWeekRpcService(new MyWeekRpcService(new MyWeekCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddReminderRpcService(new ReminderRpcService(reminders, reminderEngine));
                rpc.AddWaitingRpcService(new WaitingRpcService(new WaitingCommands(storage.Store, new GuidIdGenerator(), TimeProvider.System)));
                rpc.AddSummaryRpcService(new SummaryRpcService(summaries));
                rpc.AddDataRpcService(dataRpc);
                rpc.AddGitHubRpcService(new GitHubRpcService(github, storage.Store, githubEngine, githubApi, githubScheduler));
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

[JsonSerializable(typeof(ActivityEntityDto))]
[JsonSerializable(typeof(ActivityEntryDto))]
[JsonSerializable(typeof(ActivitySinceRequest))]
[JsonSerializable(typeof(ActivitySinceResponse))]
[JsonSerializable(typeof(ActivityHeadRequest))]
[JsonSerializable(typeof(ActivityHeadResponse))]
[JsonSerializable(typeof(ActivityJobDto))]
[JsonSerializable(typeof(ActivityJobsRequest))]
[JsonSerializable(typeof(ActivityJobsResponse))]
[JsonSerializable(typeof(ActivityCommitted))]
[JsonSerializable(typeof(GitHubAttachmentRequest))]
[JsonSerializable(typeof(GitHubAttachmentResponse))]
[JsonSerializable(typeof(GitHubTaskSyncRequest))]
[JsonSerializable(typeof(GitHubTaskSyncResponse))]
[JsonSerializable(typeof(GitHubSyncRequest))]
[JsonSerializable(typeof(GitHubSyncDto))]
[JsonSerializable(typeof(GitHubSyncResponse))]
[JsonSerializable(typeof(GitHubStatusRequest))]
[JsonSerializable(typeof(GitHubConnectRequest))]
[JsonSerializable(typeof(GitHubCandidatesRequest))]
[JsonSerializable(typeof(GitHubLookupRequest))]
[JsonSerializable(typeof(GitHubChangeRequest))]
[JsonSerializable(typeof(GitHubSettingsRequest))]
[JsonSerializable(typeof(GitHubFilterDto))]
[JsonSerializable(typeof(GitHubAddRequest))]
[JsonSerializable(typeof(GitHubUpdateRequest))]
[JsonSerializable(typeof(GitHubConnectionDto))]
[JsonSerializable(typeof(GitHubRepositoryDto))]
[JsonSerializable(typeof(GitHubCandidateDto))]
[JsonSerializable(typeof(GitHubSectionDto))]
[JsonSerializable(typeof(GitHubTargetDto))]
[JsonSerializable(typeof(GitHubStatusResponse))]
[JsonSerializable(typeof(GitHubOperationResponse))]
[JsonSerializable(typeof(GitHubCandidatesResponse))]
[JsonSerializable(typeof(ShellActivationDto))]
[JsonSerializable(typeof(ShellTakeRequest))]
[JsonSerializable(typeof(ShellTakeResponse))]
[JsonSerializable(typeof(ShortcutStatusDto))]
[JsonSerializable(typeof(ShellStatusRequest))]
[JsonSerializable(typeof(ShellStatusResponse))]
[JsonSerializable(typeof(UpdateCheckRequest))]
[JsonSerializable(typeof(UpdateCheckResponse))]
[JsonSerializable(typeof(OpenReleasesRequest))]
[JsonSerializable(typeof(OpenReleasesResponse))]
[JsonSerializable(typeof(ProfileSettingsRequest))]
[JsonSerializable(typeof(ProfileSettingsResponse))]
[JsonSerializable(typeof(ProfileSettingsUpdateRequest))]
[JsonSerializable(typeof(ZoneListRequest))]
[JsonSerializable(typeof(ZoneListResponse))]
[JsonSerializable(typeof(QuitRequest))]
[JsonSerializable(typeof(QuitResponse))]
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
[JsonSerializable(typeof(ContainerCategoryRequest))]
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
[JsonSerializable(typeof(ViewDto))]
[JsonSerializable(typeof(ViewListRequest))]
[JsonSerializable(typeof(ViewListResponse))]
[JsonSerializable(typeof(ViewGetRequest))]
[JsonSerializable(typeof(ViewSaveRequest))]
[JsonSerializable(typeof(ViewResponse))]
[JsonSerializable(typeof(ViewDuplicateRequest))]
[JsonSerializable(typeof(ViewPinRequest))]
[JsonSerializable(typeof(ViewDeleteRequest))]
[JsonSerializable(typeof(ViewReorderRequest))]
[JsonSerializable(typeof(ViewRunRequest))]
[JsonSerializable(typeof(ViewPreviewRequest))]
[JsonSerializable(typeof(ViewRunResponse))]
[JsonSerializable(typeof(SavedViewCountsRequest))]
[JsonSerializable(typeof(ViewCountEntry))]
[JsonSerializable(typeof(SavedViewCountsResponse))]
[JsonSerializable(typeof(ViewContextRequest))]
[JsonSerializable(typeof(ViewContextResponse))]
[JsonSerializable(typeof(ViewExportRequest))]
[JsonSerializable(typeof(ViewExportResponse))]
[JsonSerializable(typeof(ViewAlertsRequest))]
[JsonSerializable(typeof(ViewAlertDto))]
[JsonSerializable(typeof(ViewAlertsResponse))]
[JsonSerializable(typeof(ViewAlertAckRequest))]
[JsonSerializable(typeof(ViewAlertAckResponse))]
[JsonSerializable(typeof(ViewGitHubOptionsRequest))]
[JsonSerializable(typeof(ViewGitHubRepositoryDto))]
[JsonSerializable(typeof(ViewGitHubOptionsResponse))]
[JsonSerializable(typeof(ViewFromSearchRequest))]
[JsonSerializable(typeof(ViewFromSearchResponse))]
[JsonSerializable(typeof(BoardViewGetRequest))]
[JsonSerializable(typeof(CalendarRangeRequest))]
[JsonSerializable(typeof(CalendarRangeResponse))]
[JsonSerializable(typeof(CalendarUnscheduledRequest))]
[JsonSerializable(typeof(CalendarSourceDto))]
[JsonSerializable(typeof(GoogleAccountDto))]
[JsonSerializable(typeof(AccountStatusRequest))]
[JsonSerializable(typeof(SyncStatusRequest))]
[JsonSerializable(typeof(SyncStatusResponse))]
[JsonSerializable(typeof(SyncNowRequest))]
[JsonSerializable(typeof(SyncDismissRequest))]
[JsonSerializable(typeof(AccountStatusResponse))]
[JsonSerializable(typeof(AccountRequest))]
[JsonSerializable(typeof(AccountResponse))]
[JsonSerializable(typeof(AccountDeviceRequest))]
[JsonSerializable(typeof(ContentCountsDto))]
[JsonSerializable(typeof(JoinPreviewRequest))]
[JsonSerializable(typeof(JoinPreviewResponse))]
[JsonSerializable(typeof(JoinRequest))]
[JsonSerializable(typeof(JoinResponse))]
[JsonSerializable(typeof(LeaveRequest))]
[JsonSerializable(typeof(DeleteAccountRequest))]
[JsonSerializable(typeof(DeviceIdentityResponse))]
[JsonSerializable(typeof(ServiceAddressRequest))]
[JsonSerializable(typeof(SetServiceAddressRequest))]
[JsonSerializable(typeof(ServiceAddressResponse))]
[JsonSerializable(typeof(GoogleStatusRequest))]
[JsonSerializable(typeof(GoogleStatusResponse))]
[JsonSerializable(typeof(GoogleConnectRequest))]
[JsonSerializable(typeof(GoogleSetupRequest))]
[JsonSerializable(typeof(GoogleAccountResponse))]
[JsonSerializable(typeof(GoogleCalendarsRequest))]
[JsonSerializable(typeof(GoogleCalendarsResponse))]
[JsonSerializable(typeof(GoogleCalendarChoiceDto))]
[JsonSerializable(typeof(GoogleChooseRequest))]
[JsonSerializable(typeof(GoogleChooseResponse))]
[JsonSerializable(typeof(GoogleDisconnectRequest))]
[JsonSerializable(typeof(GoogleDisconnectResponse))]
[JsonSerializable(typeof(CalendarsListRequest))]
[JsonSerializable(typeof(CalendarsListResponse))]
[JsonSerializable(typeof(CalendarAddFeedRequest))]
[JsonSerializable(typeof(CalendarSourceResponse))]
[JsonSerializable(typeof(CalendarUpdateRequest))]
[JsonSerializable(typeof(CalendarRemoveRequest))]
[JsonSerializable(typeof(CalendarOperationResponse))]
[JsonSerializable(typeof(CalendarRefreshRequest))]
[JsonSerializable(typeof(CalendarRefreshResultDto))]
[JsonSerializable(typeof(CalendarRefreshResponse))]
[JsonSerializable(typeof(CalendarActivityRequest))]
[JsonSerializable(typeof(CalendarActivityResponse))]
[JsonSerializable(typeof(CalendarMakeTaskRequest))]
[JsonSerializable(typeof(CalendarMakeTaskResponse))]
[JsonSerializable(typeof(CalendarTaskLinksRequest))]
[JsonSerializable(typeof(CalendarTaskLinkDto))]
[JsonSerializable(typeof(CalendarTaskLinksResponse))]
[JsonSerializable(typeof(BoardGetRequest))]
[JsonSerializable(typeof(BoardSettingsDto))]
[JsonSerializable(typeof(BoardColumnResponse))]
[JsonSerializable(typeof(BoardLaneResponse))]
[JsonSerializable(typeof(BoardCardResponse))]
[JsonSerializable(typeof(BoardGetResponse))]
[JsonSerializable(typeof(BoardUpdateSettingsRequest))]
[JsonSerializable(typeof(BoardSettingsResponse))]
[JsonSerializable(typeof(BoardMoveRequest))]
[JsonSerializable(typeof(BoardMoveResponse))]
[JsonSerializable(typeof(TaskQueryRequest))]
[JsonSerializable(typeof(QueryRowResponse))]
[JsonSerializable(typeof(TaskQueryResponse))]
[JsonSerializable(typeof(ViewCountsRequest))]
[JsonSerializable(typeof(ViewCountResponse))]
[JsonSerializable(typeof(TagCountResponse))]
[JsonSerializable(typeof(ContainerCountResponse))]
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
[JsonSerializable(typeof(GitHubSelfAssignmentRequest))]
[JsonSerializable(typeof(GitHubTaskActionRequest))]
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
[JsonSerializable(typeof(SiteAddRequest))]
[JsonSerializable(typeof(SiteAddResponse))]
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
[JsonSerializable(typeof(PadCreateDrawingRequest))]
[JsonSerializable(typeof(PadDrawingGetRequest))]
[JsonSerializable(typeof(PadDrawingSaveRequest))]
[JsonSerializable(typeof(PadDrawingResponse))]
[JsonSerializable(typeof(PadMakeTaskFromRegionRequest))]
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
[JsonSerializable(typeof(PageSummaryResponse))]
[JsonSerializable(typeof(PageTaskLinkResponse))]
[JsonSerializable(typeof(PageBacklinkResponse))]
[JsonSerializable(typeof(PageResponse))]
[JsonSerializable(typeof(PageListRequest))]
[JsonSerializable(typeof(PageListResponse))]
[JsonSerializable(typeof(PageDailyListRequest))]
[JsonSerializable(typeof(PageGetRequest))]
[JsonSerializable(typeof(PageGetDailyRequest))]
[JsonSerializable(typeof(PageGetResponse))]
[JsonSerializable(typeof(PageCreateRequest))]
[JsonSerializable(typeof(PageCreateDailyRequest))]
[JsonSerializable(typeof(PageSaveRequest))]
[JsonSerializable(typeof(PageSaveResponse))]
[JsonSerializable(typeof(PageMoveRequest))]
[JsonSerializable(typeof(PageItemRequest))]
[JsonSerializable(typeof(PageOperationResponse))]
[JsonSerializable(typeof(PageRevisionsRequest))]
[JsonSerializable(typeof(PageRevisionResponse))]
[JsonSerializable(typeof(PageRevisionsResponse))]
[JsonSerializable(typeof(PageRestoreVersionRequest))]
[JsonSerializable(typeof(PageMakeTaskRequest))]
[JsonSerializable(typeof(PageMakeTaskResponse))]
[JsonSerializable(typeof(PageLocateRequest))]
[JsonSerializable(typeof(PageAnchorResponse))]
[JsonSerializable(typeof(PageLocateResponse))]
[JsonSerializable(typeof(PageMentionsRequest))]
[JsonSerializable(typeof(PageMentionsResponse))]
[JsonSerializable(typeof(PageSearchRequest))]
[JsonSerializable(typeof(PageByTagRequest))]
[JsonSerializable(typeof(PageResolveRequest))]
[JsonSerializable(typeof(PageResolveNamesRequest))]
[JsonSerializable(typeof(PageLinkTargetResponse))]
[JsonSerializable(typeof(PageResolveResponse))]
[JsonSerializable(typeof(PageAppendRequest))]
[JsonSerializable(typeof(PageFromPadRequest))]
[JsonSerializable(typeof(PageAppendPadRequest))]
[JsonSerializable(typeof(AttachmentDto))]
[JsonSerializable(typeof(AttachmentListRequest))]
[JsonSerializable(typeof(AttachmentResolveRequest))]
[JsonSerializable(typeof(AttachmentListResponse))]
[JsonSerializable(typeof(AttachmentBeginRequest))]
[JsonSerializable(typeof(AttachmentBeginResponse))]
[JsonSerializable(typeof(AttachmentChunkRequest))]
[JsonSerializable(typeof(AttachmentCommitRequest))]
[JsonSerializable(typeof(AttachmentCancelRequest))]
[JsonSerializable(typeof(AttachmentOperationResponse))]
[JsonSerializable(typeof(AttachmentSimpleResponse))]
[JsonSerializable(typeof(AttachmentRenameRequest))]
[JsonSerializable(typeof(AttachmentItemRequest))]
[JsonSerializable(typeof(AttachmentReadRequest))]
[JsonSerializable(typeof(AttachmentReadResponse))]
[JsonSerializable(typeof(AttachmentActionRequest))]
[JsonSerializable(typeof(AttachmentActionResponse))]
[JsonSerializable(typeof(AttachmentPageImageRequest))]
[JsonSerializable(typeof(AttachmentCropDto))]
[JsonSerializable(typeof(AttachmentEditImageRequest))]
[JsonSerializable(typeof(AttachmentThumbnailRequest))]
[JsonSerializable(typeof(AttachmentRetryRequest))]
[JsonSerializable(typeof(AttachmentOfflineRequest))]
[JsonSerializable(typeof(AttachmentOfflineListRequest))]
[JsonSerializable(typeof(AttachmentOfflineResponse))]
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
[JsonSerializable(typeof(MeetingFactDto))]
[JsonSerializable(typeof(SummaryFactsDto))]
[JsonSerializable(typeof(SummaryCountsDto))]
[JsonSerializable(typeof(SummaryHeaderDto))]
[JsonSerializable(typeof(SummaryRevisionDto))]
[JsonSerializable(typeof(SummaryDetailDto))]
[JsonSerializable(typeof(SummaryListRequest))]
[JsonSerializable(typeof(SummaryListResponse))]
[JsonSerializable(typeof(SummaryGetRequest))]
[JsonSerializable(typeof(SummaryFindRequest))]
[JsonSerializable(typeof(SummaryDetailResponse))]
[JsonSerializable(typeof(SummaryGenerateRequest))]
[JsonSerializable(typeof(SummaryPromptsRequest))]
[JsonSerializable(typeof(SummaryPromptsResponse))]
[JsonSerializable(typeof(SummaryDismissRequest))]
[JsonSerializable(typeof(SummaryDismissResponse))]
[JsonSerializable(typeof(CarryOverPreviewRequest))]
[JsonSerializable(typeof(CarryOverCandidateDto))]
[JsonSerializable(typeof(CarryOverPreviewResponse))]
[JsonSerializable(typeof(CarryOverRequest))]
[JsonSerializable(typeof(CarryOverResponse))]
[JsonSerializable(typeof(SummarySettingsRequest))]
[JsonSerializable(typeof(SummarySettingsResponse))]
[JsonSerializable(typeof(SummarySettingsUpdateRequest))]
[JsonSerializable(typeof(DataCountsDto))]
[JsonSerializable(typeof(BackupDto))]
[JsonSerializable(typeof(ImportBatchDto))]
[JsonSerializable(typeof(DataStatusRequest))]
[JsonSerializable(typeof(DataStatusResponse))]
[JsonSerializable(typeof(BackupNowRequest))]
[JsonSerializable(typeof(BackupNowResponse))]
[JsonSerializable(typeof(BackupInspectRequest))]
[JsonSerializable(typeof(BackupInspectResponse))]
[JsonSerializable(typeof(BackupRestoreRequest))]
[JsonSerializable(typeof(RestoreResponse))]
[JsonSerializable(typeof(ExportRequest))]
[JsonSerializable(typeof(ExportResponse))]
[JsonSerializable(typeof(OpenFolderRequest))]
[JsonSerializable(typeof(OpenFolderResponse))]
[JsonSerializable(typeof(CsvColumnDto))]
[JsonSerializable(typeof(ImportPreviewRequest))]
[JsonSerializable(typeof(ImportIssueDto))]
[JsonSerializable(typeof(ImportListPlanDto))]
[JsonSerializable(typeof(ImportItemDto))]
[JsonSerializable(typeof(ArchiveSummaryDto))]
[JsonSerializable(typeof(ImportPreviewResponse))]
[JsonSerializable(typeof(ListTargetDto))]
[JsonSerializable(typeof(ImportCommitRequest))]
[JsonSerializable(typeof(ImportCommitResponse))]
[JsonSerializable(typeof(PurgeRequest))]
[JsonSerializable(typeof(PurgeCountsDto))]
[JsonSerializable(typeof(PurgeResponse))]
[JsonSerializable(typeof(ResetRequest))]
[JsonSerializable(typeof(ResetResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class AppJsonContext : JsonSerializerContext;
