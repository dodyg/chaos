using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Boards;
using Chaos.Application.Calendar;
using Chaos.Application.Capture;
using Chaos.Application.Containers;
using Chaos.Application.GitHub;
using Chaos.Application.History;
using Chaos.Application.Pad;
using Chaos.Application.Pages;
using Chaos.Application.People;
using Chaos.Application.Planning;
using Chaos.Application.Queries;
using Chaos.Application.Reminders;
using Chaos.Application.Settings;
using Chaos.Application.Sites;
using Chaos.Application.Summaries;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Application.Transfer;
using Chaos.Application.Views;
using Chaos.Desktop.Platform;
using Chaos.Desktop.Services;
using Chaos.Desktop.Windows.Notifications;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Calendar;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.GitHub;
using Chaos.Infrastructure.Sites;
using Chaos.Infrastructure.Transfer;
using NeoAstra.Rpc;

namespace Chaos.Desktop;

/// <summary>
/// The desktop composition root: builds the object graph once, in dependency order, and owns every disposable in it.
/// One clock and one ID generator are shared by everything. Background workers start here, after everything they use
/// exists, and stop in reverse order, before storage closes. The pause order that restore and reset run inside is the
/// one explicit list in <see cref="ComposePausePipeline"/>.
/// </summary>
internal sealed class DesktopComposition : IDisposable
{
    private readonly Stack<IDisposable> _owned = new();

    private readonly ChaosStorageHandle _storage;
    private readonly Chaos.Infrastructure.Sqlite.SqliteApplicationStore _store;
    private readonly TimeProvider _clock;
    private readonly IIdGenerator _ids;
    private readonly string _appVersion;

    private readonly ActivityPublisher _activityPublisher;
    private readonly Chaos.Infrastructure.Activity.ActivityFeed _activityFeed;
    private readonly TaskCommands _tasks;
    private readonly BoardCommands _boards;
    private readonly BoardQueries _boardQueries;
    private readonly TaskQueries _taskQueries;
    private readonly SavedViewCommands _viewCommands;
    private readonly SavedViewQueries _viewQueries;
    private readonly ViewBoardQueries _viewBoardQueries;
    private readonly CalendarQueries _calendarQueries;
    private readonly ViewAlertService _viewAlerts;
    private readonly TaskHistoryCommands _history;
    private readonly ReminderCommands _reminders;
    private readonly ReminderEngine _reminderEngine;
    private readonly SummaryCommands _summaries;
    private readonly DataRpcService _dataRpc;
    private readonly GitHubCliClient _githubApi;
    private readonly GitHubSyncEngine _githubEngine;
    private readonly GitHubConnectionCommands _github;
    private readonly GitHubSyncScheduler _githubScheduler;
    private readonly Chaos.Infrastructure.Service.ServiceAddressStore _serviceAddress;
    private readonly Chaos.Infrastructure.Service.ChaosServiceClient _serviceClient;
    private readonly Chaos.Infrastructure.Sync.SyncEngine _syncEngine;
    private readonly Chaos.Infrastructure.Sync.SyncStatusReader _syncStatus;
    private readonly Chaos.Infrastructure.Service.AccountJoinService _accountJoin;
    private readonly Chaos.Infrastructure.Attachments.AttachmentTransfer _attachmentTransfer;
    private readonly Chaos.Application.Attachments.AttachmentCommands _attachmentCommands;
    private readonly CalendarRefresher _calendarRefresher;
    private readonly GoogleCalendarCommands _googleCommands;
    private readonly CalendarCommands _calendarCommands;
    private readonly SiteNoteCommands _siteNotes;
    private readonly Chaos.Infrastructure.Updates.GitHubReleaseFeed _releaseFeed;

    private DesktopComposition(string dataRoot, string appVersion, bool isDevelopmentBuild)
    {
        // A failure part-way through must still stop and close whatever was already started.
        try
        {
            _appVersion = appVersion;
            _clock = TimeProvider.System;
            _ids = new GuidIdGenerator();

            // Storage starts before the window: lock the data root, back up before upgrades, migrate, seed (PLAN §5).
            // This is the one synchronous storage boundary; every request-time database call runs on NeoAstra's
            // background RPC dispatch threads.
            _storage = Own(StorageBootstrap
                .StartAsync(StorageOptions.FromDataRoot(dataRoot), appVersion, _ids, _clock, CancellationToken.None)
                .GetAwaiter().GetResult());
            var store = _storage.Store;
            _store = store;
            var clock = _clock;
            var ids = _ids;

            // The activity log (PLAN2 §6.4): the store reports each commit that wrote activity, the publisher coalesces and
            // pushes it to the window. This is the only place the notifier is attached, before anything can write.
            _activityPublisher = Own(new ActivityPublisher(clock));
            store.ActivityNotifier = _activityPublisher;
            _activityFeed = new Chaos.Infrastructure.Activity.ActivityFeed(store, clock);
            var jobRuns = new Chaos.Infrastructure.Activity.JobRuns(store, clock);

            _tasks = new TaskCommands(store, ids, clock);
            _boards = new BoardCommands(store, ids, clock);
            _boardQueries = new BoardQueries(store);
            _taskQueries = new TaskQueries(store, clock);
            _viewCommands = new SavedViewCommands(store, ids, clock);
            _viewQueries = new SavedViewQueries(store, _taskQueries, clock);
            _viewBoardQueries = new ViewBoardQueries(store, _taskQueries);
            _calendarQueries = new CalendarQueries(store, _taskQueries, clock);
            _viewAlerts = new ViewAlertService(store, ids, clock);
            _history = new TaskHistoryCommands(store, _tasks);

            // Reminders are delivered after commit on a background timer (PLAN §11). The installed package hands them to
            // Windows (scheduled, with buttons); an unpackaged run shows them in the app.
            var notifications = (INotificationAdapter?)WindowsToastAdapter.TryCreate(clock) ?? new InAppNotificationAdapter();
            _reminders = new ReminderCommands(store, ids, clock, _tasks, notifications);
            // Plain summaries are generated on the same background tick at their scheduled times (PLAN §12).
            _summaries = new SummaryCommands(store, ids, clock);
            _reminderEngine = Own(new ReminderEngine(_reminders, notifications, clock, _summaries, _viewAlerts, jobRuns,
                new Chaos.Infrastructure.Activity.CollaborationNoticeDeriver(store, clock),
                new Chaos.Infrastructure.Activity.CollaborationInbox(store, clock)));

            // Backups, restore, export, import and deletion (PLAN §14). Attachment bytes live behind IAttachmentFileStore
            // (PLAN2 §9.1); this is the one place the provider is chosen.
            var attachmentFiles = Chaos.Infrastructure.Attachments.AttachmentFileStoreFactory.Create(_storage.Options);
            var protection = new DataProtectionService(store, _storage.Options,
                new BackupManager(store.Source, _storage.Options.BackupsDirectory, _storage.AppVersion, clock, null,
                    attachmentFiles, Chaos.Infrastructure.Attachments.AttachmentFileStoreFactory.CreateBackupStore(_storage.Options)), ids, clock);
            var backupScheduler = Own(new BackupScheduler(protection, clock, jobRuns, _activityFeed));
            _dataRpc = new DataRpcService(protection, new DataTransfer(store, _storage.AppVersion, clock, attachmentFiles),
                new ImportCommands(store, ids, clock), new DataPurge(store, attachmentFiles), _storage.RecoveryNotice,
                async (osIds, ct) =>
                {
                    foreach (var osId in osIds)
                    {
                        await notifications.CancelAsync(osId, ct);
                    }
                });

            _githubApi = Own(new GitHubCliClient());
            var githubIssues = Own(new GitHubIssueFetcher(store, _githubApi, new GitHubInboundCommands(store, ids, clock),
                async ct => (await protection.BackupNowAsync(Chaos.Application.Contracts.BackupReason.PreImport, ct)).FileName, clock));
            var githubComments = Own(new GitHubCommentFetcher(store, _githubApi, new GitHubInboundComments(store, ids, clock), clock));
            _githubEngine = Own(new GitHubSyncEngine(new GitHubSyncCommands(store, _githubApi, githubIssues, githubComments, ids, clock), clock, TimeSpan.FromSeconds(10), jobRuns));
            _github = new GitHubConnectionCommands(store, _githubApi, ids, clock, new MachineDeviceInfo());
            _githubScheduler = Own(new GitHubSyncScheduler(_githubEngine, _github.ScheduleAsync, clock));

            // Calendar feeds (PLAN2 P5): addresses live in the protected secret store, never in the database.
            var secrets = new FileSecretStore(Path.Combine(dataRoot, "secrets"), new Chaos.Desktop.Windows.Security.DpapiSecretProtector());
            var feedClient = Own(new CalendarFeedClient());
            // Google Calendar (PLAN2 P6): the OAuth client comes from google-oauth.json beside the exe (git-ignored); tokens go to the secret store only.
            var googleClient = Own(new GoogleCalendarClient(GoogleOAuthConfig.TryLoad(Path.Combine(AppContext.BaseDirectory, "google-oauth.json")), new ShellBrowser()));
            // Online service (PLAN2 P12): the address is typed in Settings → Account (or comes from CHAOS_SERVICE_URL or
            // chaos-service.json beside the exe). Plain http is accepted for loopback in development builds only.
            _serviceAddress = new Chaos.Infrastructure.Service.ServiceAddressStore(
                Path.Combine(dataRoot, "service-address.json"), Path.Combine(AppContext.BaseDirectory, "chaos-service.json"), allowLoopbackHttp: isDevelopmentBuild);
            _serviceClient = Own(new Chaos.Infrastructure.Service.ChaosServiceClient(
                _serviceAddress.Resolve, new ShellBrowser(), secrets, new Chaos.Infrastructure.Service.SqliteDeviceIdentity(store, appVersion)));
            // Sync (PLAN2 P13): the loop idles until SyncState.Enabled = 1 (turned on by joining an account, P14).
            _syncEngine = Own(new Chaos.Infrastructure.Sync.SyncEngine(store,
                new Chaos.Infrastructure.Sync.SyncApplier(store, clock, ids), _serviceClient, clock));
            // Attachment bytes (PLAN2 P16): uploads follow the metadata, downloads follow the 5 MiB / pinned / on-open rules.
            _attachmentTransfer = Own(new Chaos.Infrastructure.Attachments.AttachmentTransfer(store, attachmentFiles, _serviceClient, clock));
            _attachmentCommands = new Chaos.Application.Attachments.AttachmentCommands(store, attachmentFiles, ids, clock,
                new Chaos.Infrastructure.Attachments.DirectoryUploadStaging(_storage.Options.UploadsDirectory),
                images: new Chaos.Infrastructure.Attachments.SkiaImageProcessor(),
                thumbnails: new Chaos.Infrastructure.Attachments.DirectoryThumbnailCache(_storage.Options.ThumbnailsDirectory),
                availability: _attachmentTransfer);
            _syncEngine.CycleFinished += _ => _attachmentTransfer.Wake();
            _syncStatus = new Chaos.Infrastructure.Sync.SyncStatusReader(store, _syncEngine);
            _accountJoin = new Chaos.Infrastructure.Service.AccountJoinService(_serviceClient, new Chaos.Infrastructure.Sync.SyncAccountState(store), protection, clock, _syncEngine.Wake);

            _calendarRefresher = new CalendarRefresher(store, secrets, feedClient, new IcsParser(), clock, googleClient);
            _googleCommands = new GoogleCalendarCommands(store, secrets, googleClient, clock);
            _googleCommands.LoadSavedClientAsync().GetAwaiter().GetResult();
            _calendarCommands = new CalendarCommands(store, secrets, ids, clock, new CaptureCommands(store, ids, clock));
            var calendarScheduler = Own(new CalendarRefreshScheduler(_calendarRefresher, clock, jobRuns));

            // Restore and reset replace database files, so every background worker stops first.
            protection.Quiesce = ComposePausePipeline(protection, notifications, _calendarRefresher, _githubScheduler, _githubEngine, _reminderEngine);

            // "New site" (pad): pages are fetched here and summarized by a small local model (Ollama), falling back to a
            // model-free extractive summary when Ollama is not running.
            var siteFetcher = Own(new HttpSitePageFetcher());
            var ollamaSummarizer = Own(new OllamaSiteSummarizer());
            _siteNotes = Own(new SiteNoteCommands(siteFetcher, new FallbackSiteSummarizer(ollamaSummarizer, new ExtractiveSiteSummarizer()),
                new PadCommands(store, ids, clock), new TagCommands(store, ids, clock)));
            _releaseFeed = Own(new Chaos.Infrastructure.Updates.GitHubReleaseFeed());

            Activations = new ShellActivations((command, ct) => _reminders.ActAsync(command, ct));
            ProfileSettings = new ProfileSettingsCommands(store, ids, clock);

            // Every worker is built; start them now, so none runs against a half-wired graph.
            _reminderEngine.Start();
            backupScheduler.Start();
            _githubScheduler.Start();
            _syncEngine.Start();
            _attachmentTransfer.Start();
            calendarScheduler.Start();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Opens storage, wires everything and starts the background workers.</summary>
    public static DesktopComposition Create(string dataRoot, string appVersion, bool isDevelopmentBuild) =>
        new(dataRoot, appVersion, isDevelopmentBuild);

    public ShellActivations Activations { get; }

    public ProfileSettingsCommands ProfileSettings { get; }

    /// <summary>
    /// The pause order that restore, reset and joining an account run inside. Outermost first: calendars, the GitHub
    /// scheduler (wakes once the engine is back), the GitHub engine, then reminders, which also take back OS
    /// notification schedules just before the work runs.
    /// </summary>
    private static Func<Func<Task>, CancellationToken, Task> ComposePausePipeline(
        DataProtectionService protection,
        INotificationAdapter notifications,
        CalendarRefresher calendarRefresher,
        GitHubSyncScheduler githubScheduler,
        GitHubSyncEngine githubEngine,
        ReminderEngine reminderEngine) =>
        PausePipeline.Compose(
            (inner, ct) => calendarRefresher.RunPausedAsync(inner, ct),
            async (inner, ct) =>
            {
                try { await inner(); }
                finally { githubScheduler.Wake(); }
            },
            (inner, ct) => githubEngine.RunPausedAsync(inner, ct),
            (inner, ct) => reminderEngine.RunPausedAsync(async () =>
            {
                foreach (var osId in await protection.ListScheduledOsNotificationIdsAsync(ct))
                {
                    await notifications.CancelAsync(osId, ct);
                }

                await inner();
            }, ct));

    /// <summary>Registers every RPC service of the window.</summary>
    public void AddRpcServices(NeoRpcBuilder rpc, DesktopShell shell)
    {
        var store = _store;
        var ids = _ids;
        var clock = _clock;

        var committed = rpc.AddActivityEventsCommittedEvent();
        _activityPublisher.Attach(payload => committed.PublishAsync(payload).AsTask());
        rpc.AddActivityRpcService(new ActivityRpcService(_activityFeed));
        rpc.AddCollaborationRpcService(new CollaborationRpcService(
            new Chaos.Infrastructure.Activity.CollaborationInbox(_store, _clock),
            new Chaos.Infrastructure.Activity.SpaceNotificationPreferences(_store, _clock)));
        rpc.AddShellRpcService(new ShellRpcService(Activations, shell, ProfileSettings, _appVersion, _reminderEngine.ResyncAsync, store));
        rpc.AddUpdatesRpcService(new UpdatesRpcService(_releaseFeed, new ShellBrowser(), _appVersion));
        rpc.AddTaskRpcService(new TaskRpcService(_tasks, _githubEngine));
        rpc.AddRecurrenceRpcService(new RecurrenceRpcService(new RecurrenceCommands(store, ids, clock), _tasks));
        rpc.AddSubtaskRpcService(new SubtaskRpcService(_tasks, new SubtaskCommands(store, ids, clock), _githubEngine));
        rpc.AddContainerRpcService(new ContainerRpcService(new ContainerCommands(store, ids, clock), _tasks));
        rpc.AddTemplateRpcService(new TemplateRpcService(new TemplateCommands(store, ids, clock)));
        rpc.AddTagRpcService(new TagRpcService(new TagCommands(store, ids, clock), _githubEngine));
        rpc.AddPeopleRpcService(new PeopleRpcService(new PeopleQueries(store)));
        rpc.AddSpaceRpcService(new SpaceRpcService(new Chaos.Application.Spaces.SpaceQueries(store),
            new Chaos.Application.Spaces.SpaceCommands(store, _serviceClient, new Chaos.Infrastructure.Sync.SyncSpaceStreams(store, _syncEngine), ids)));
        rpc.AddQueryRpcService(new QueryRpcService(_taskQueries));
        rpc.AddViewRpcService(new ViewRpcService(_viewCommands, _viewQueries, _viewAlerts));
        rpc.AddBoardRpcService(new BoardRpcService(_boards, _boardQueries, _viewBoardQueries, _githubEngine));
        rpc.AddCalendarRpcService(new CalendarRpcService(_calendarQueries));
        rpc.AddCalendarsRpcService(new CalendarsRpcService(_calendarCommands, _calendarRefresher));
        rpc.AddGoogleCalendarRpcService(new GoogleCalendarRpcService(_googleCommands, _calendarRefresher));
        rpc.AddSyncStatusRpcService(new SyncStatusRpcService(_syncStatus, _syncEngine));
        rpc.AddAccountRpcService(new AccountRpcService(_serviceClient, _serviceAddress, _accountJoin));
        rpc.AddHistoryRpcService(new HistoryRpcService(_history, _githubEngine));
        rpc.AddDiaryRpcService(new DiaryRpcService(new DiaryCommands(store, ids, clock), _githubEngine));
        rpc.AddUndoRpcService(new UndoRpcService(new UndoCommands(store, ids, clock)));
        rpc.AddMarkdownRpcService(new MarkdownRpcService(new ChecklistCommands(store, ids, clock), _tasks, sync: _githubEngine));
        rpc.AddPadRpcService(new PadRpcService(new PadCommands(store, ids, clock)));
        rpc.AddSiteRpcService(new SiteRpcService(_siteNotes));
        rpc.AddPageRpcService(new PageRpcService(new PageCommands(store, ids, clock)));
        rpc.AddAttachmentRpcService(new AttachmentRpcService(_attachmentCommands, AttachmentRpcService.DefaultOpenedDirectory, () => _dataRpc.ExportDirectory, transfer: _attachmentTransfer));
        rpc.AddCaptureRpcService(new CaptureRpcService(new CaptureCommands(store, ids, clock)));
        rpc.AddMyDayRpcService(new MyDayRpcService(new MyDayCommands(store, clock)));
        rpc.AddMyWeekRpcService(new MyWeekRpcService(new MyWeekCommands(store, ids, clock)));
        rpc.AddReminderRpcService(new ReminderRpcService(_reminders, _reminderEngine));
        rpc.AddWaitingRpcService(new WaitingRpcService(new WaitingCommands(store, ids, clock)));
        rpc.AddSummaryRpcService(new SummaryRpcService(_summaries));
        rpc.AddDataRpcService(_dataRpc);
        rpc.AddGitHubRpcService(new GitHubRpcService(_github, store, _githubEngine, _githubApi, _githubScheduler));
    }

    /// <summary>Stops the workers in reverse start order, then closes storage last.</summary>
    public void Dispose()
    {
        while (_owned.Count > 0)
        {
            _owned.Pop().Dispose();
        }
    }

    private T Own<T>(T disposable) where T : IDisposable
    {
        _owned.Push(disposable);
        return disposable;
    }
}
