using System.Collections.Concurrent;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Attachments;
using Chaos.Application.Boards;
using Chaos.Application.Capture;
using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Application.History;
using Chaos.Application.Pad;
using Chaos.Application.Pages;
using Chaos.Application.Planning;
using Chaos.Application.Reminders;
using Chaos.Application.Settings;
using Chaos.Application.Spaces;
using Chaos.Application.Summaries;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Application.Transfer;
using Chaos.Domain.Capture;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Summaries;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Activity;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Infrastructure.Transfer;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Sync;

/// <summary>
/// One test per synced catalog kind (PLAN2 §7.2/§7.3): the cases are generated from
/// <see cref="SyncCatalog"/>, and each case drives a representative change through the real
/// Application command classes with sync enabled, then asserts the flush wrote that entity to
/// SyncOutbox — with the exact EntityKey where the scenario knows it. The scenarios share one
/// DataHost per group, so every group runs its commands once against its own data root.
/// </summary>
public sealed class SyncCatalogFlushCoverageTests
{
    private const string Structure = "structure";
    private const string Work = "work";
    private const string Recurrence = "recurrence";
    private const string Planning = "planning";
    private const string Records = "records";
    private const string Pages = "pages";
    private const string GitHub = "github";
    private const string Board = "board";
    private const string Calendars = "calendars";

    /// <summary>Which scenario group produces each kind. Every non-LocalOnly catalog kind is
    /// either here or in <see cref="UnreachableKinds"/> (the guard test below enforces that).</summary>
    private static readonly IReadOnlyDictionary<string, string> ScenarioByKind = new Dictionary<string, string>
    {
        ["profile"] = Structure,
        ["container"] = Structure,
        ["section"] = Structure,
        ["space_state"] = Structure,
        ["tag"] = Structure,
        ["task_template"] = Structure,
        ["subtask_tag"] = Structure,

        ["task"] = Work,
        ["task_assignee"] = "assignment",
        ["task_delegation"] = "delegation",
        ["nudge"] = "delegation",
        ["space_notification_setting"] = "assignment",
        ["subtask"] = Work,
        ["task_tag"] = Work,
        ["task_history"] = Work,
        ["diary_entry"] = Work,
        ["pad"] = Work,
        ["pad_tag"] = Work,
        ["pad_drawing"] = Work,
        ["scratch_task_link"] = Work,
        ["reminder_rule"] = Work,
        ["reminder_acknowledgement"] = "reminder_ack",
        ["waiting_detail"] = Work,
        ["follow_up"] = Work,

        ["recurrence_series"] = Recurrence,
        ["recurrence_occurrence"] = Recurrence,
        ["recurrence_exception"] = Recurrence,

        ["day_plan"] = Planning,
        ["day_plan_item"] = Planning,
        ["week_plan"] = Planning,
        ["week_goal"] = Planning,
        ["week_goal_task"] = Planning,
        ["week_assignment"] = Planning,
        ["availability_rule"] = Planning,
        ["week_availability_override"] = Planning,

        ["summary"] = Records,
        ["import_batch"] = Records,
        ["imported_item"] = Records,

        ["page"] = Pages,
        ["page_tag"] = Pages,
        ["page_task_link"] = Pages,
        ["attachment"] = Pages,

        ["github_connection"] = GitHub,
        ["github_repository"] = GitHub,
        ["github_issue_link"] = GitHub,
        ["github_checklist_item"] = GitHub,
        ["github_comment_link"] = GitHub,
        ["github_label_tag"] = GitHub,
        ["github_milestone_project"] = GitHub,

        ["board_setting"] = Board,
        ["saved_view"] = Board,
        ["view_alert_setting"] = Board,

        ["calendar_source"] = Calendars,
        ["calendar_event_link"] = Calendars,
        ["google_account"] = Calendars,
    };

    /// <summary>Kinds no real command can reach today, with the exact blocker. Not faked here;
    /// the guard test keeps them visible until product code reaches them.</summary>
    private static readonly IReadOnlyDictionary<string, string> UnreachableKinds = new Dictionary<string, string>
    {
        ["space"] = "No Application command writes Spaces: StorageBootstrap creates the personal space once at first start, "
            + "while sync is still disabled, and every later use only reads it (session.Spaces.Get*).",
        ["space_member"] = "Membership is server-written and is never captured into the local outbox.",
    };

    public static IEnumerable<string> CoveredKinds() =>
        [.. ScenarioByKind.Keys.Order(StringComparer.Ordinal)];

    private static readonly ConcurrentDictionary<string, Lazy<Task<ScenarioRun>>> Runs = new(StringComparer.Ordinal);

    [Test]
    [MethodDataSource(nameof(CoveredKinds))]
    public async Task A_real_command_change_flushes_the_kind_into_the_outbox(string kind)
    {
        var run = await Runs.GetOrAdd(ScenarioByKind[kind], group => new Lazy<Task<ScenarioRun>>(() => DriveAsync(group))).Value;
        var keys = run.OutboxKeys.GetValueOrDefault(kind) ?? [];
        await Assert.That(keys.Count > 0).IsTrue();
        if (run.ExpectedKeys.TryGetValue(kind, out var key))
        {
            await Assert.That(keys).Contains(key);
        }
    }

    /// <summary>The catalog drives the case list: a new synced table fails until a scenario (or a
    /// documented blocker) covers it, and a kind can never be both.</summary>
    [Test]
    public async Task Every_synced_catalog_kind_is_covered_by_a_scenario_or_documented_as_unreachable()
    {
        var catalog = SyncCatalog.Tables.Where(table => table.Mode != SyncMode.LocalOnly).Select(table => table.Kind)
            .ToHashSet(StringComparer.Ordinal);
        var covered = ScenarioByKind.Keys.ToHashSet(StringComparer.Ordinal);
        var documented = UnreachableKinds.Keys.ToHashSet(StringComparer.Ordinal);

        var both = covered.Intersect(documented, StringComparer.Ordinal).ToList();
        var missing = catalog.Except(covered.Concat(documented), StringComparer.Ordinal).Order().ToList();
        var removed = covered.Concat(documented).Except(catalog, StringComparer.Ordinal).Order().ToList();
        await Assert.That(both).IsEmpty();
        await Assert.That(missing).IsEmpty();
        await Assert.That(removed).IsEmpty();
    }

    private sealed class CoveragePresenter : INotificationAdapter
    {
        public NotificationCapabilities Capabilities { get; } = new("test", false, true, NotificationPermission.Granted, "test");
        public Task<NotificationScheduleResult> ScheduleAsync(NotificationRequest request, CancellationToken ct) => Task.FromResult(new NotificationScheduleResult(false, null, null));
        public Task CancelAsync(string osNotificationId, CancellationToken ct) => Task.CompletedTask;
        public Task<NotificationShowOutcome> ShowAsync(NotificationRequest request, CancellationToken ct) => Task.FromResult(NotificationShowOutcome.InApp);
    }

    private static async Task<ScenarioRun> DriveAsync(string group)
    {
        using var host = await DataHost.StartAsync();
        await SyncTestHelpers.EnableSyncAsync(host);
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (group)
        {
            case "assignment":
                var profile = await host.Store.QueryAsync((session, token) => session.Profiles.GetPrimaryAsync(token));
                var (space, container) = await SharedTeamAsync(host, profile!.Id, "Me");
                var assignedTask = (await host.Tasks.CaptureAsync(new(Op(), container, "Assign", null))).Task;
                await host.Tasks.ChangeAssigneeAsync(new(Op(), assignedTask.Id, assignedTask.Version, profile!.Id, true));
                await new SpaceNotificationPreferences(host.Store, host.Clock).SetAsync(space, new SpaceNotificationPreference(Nothing: true));
                expected["space_notification_setting"] = $"spacenotify:{profile.Id}:{space}";
                expected["task_assignee"] = $"{assignedTask.Id}|{profile.Id}";
                break;
            case "delegation":
            {
                var me = await host.Store.QueryAsync((session, token) => session.Profiles.GetPrimaryAsync(token));
                var omar = Guid.NewGuid().ToString("D");
                var (_, list) = await SharedTeamAsync(host, me!.Id, omar);
                var delegated = (await host.Tasks.CaptureAsync(new(Op(), list, "Send the quote", null))).Task;
                var delegations = new DelegationCommands(host.Store, host.Ids, host.Clock);
                var result = await delegations.DelegateAsync(new(Op(), delegated.Id, delegated.Version, omar, "Please", null, null, false,
                    new FollowUpSpec(Chaos.Domain.Reminders.FollowUpKind.Fixed, host.Clock.Now.AddDays(1), null, null)));
                await delegations.NudgeAsync(new(Op(), result.Delegation.Id, result.Delegation.Version, null));
                expected["task_delegation"] = result.Delegation.Id;
                break;
            }
            case "reminder_ack":
                var task = await SyncTestHelpers.CaptureTaskAsync(host);
                var reminders = new ReminderCommands(host.Store, host.Ids, host.Clock, host.Tasks, new CoveragePresenter());
                await reminders.AddAsync(new(Op(), task.Id, new(ReminderAnchor.Absolute, null, host.Clock.Now, null)));
                var delivery = (await reminders.DeliverDueAsync()).Single();
                await reminders.ActAsync(new(Op(), delivery.Job.Id, delivery.Job.Generation, NotificationAction.Dismiss));
                expected["reminder_acknowledgement"] = (await TextsAsync(host, "SELECT Id FROM ReminderAcknowledgements"))[0];
                break;
            case Structure:
                await StructureScenarioAsync(host, expected);
                break;
            case Work:
                await WorkScenarioAsync(host, expected);
                break;
            case Recurrence:
                await RecurrenceScenarioAsync(host, expected);
                break;
            case Planning:
                await PlanningScenarioAsync(host, expected);
                break;
            case Records:
                await RecordsScenarioAsync(host, expected);
                break;
            case Pages:
                await PagesScenarioAsync(host, expected);
                break;
            case GitHub:
                await GitHubScenarioAsync(host, expected);
                break;
            case Board:
                await BoardScenarioAsync(host, expected);
                break;
            case Calendars:
                await CalendarsScenarioAsync(host, expected);
                break;
            default:
                throw new InvalidOperationException($"Unknown scenario group: {group}");
        }

        return await CollectAsync(host, expected);
    }

    // ---- Scenarios --------------------------------------------------------------------------

    /// <summary>A shared space the profile owns, with one more editor and a list; sync is on, so every write flushes.</summary>
    private static async Task<(string Space, string Container)> SharedTeamAsync(DataHost host, string ownerId, string editorId)
    {
        var space = Guid.NewGuid().ToString("D");
        var container = Guid.NewGuid().ToString("D");
        await host.Store.ExecuteAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ($space,$owner,2,'Team',$now,$now);
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES ($row,$space,$owner,1,'Me',1,$now);
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES ($row2,$space,$editor,1,'Editor',2,$now);
                INSERT INTO Containers (Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ($container,$space,1,'Team','team',0,$now,$now);
                """;
            command.Parameters.AddWithValue("$space", space);
            command.Parameters.AddWithValue("$owner", ownerId);
            command.Parameters.AddWithValue("$editor", editorId);
            command.Parameters.AddWithValue("$row", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$row2", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$container", container);
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(host.Clock.Now));
            await command.ExecuteNonQueryAsync(token);
            return 0;
        });
        return (space, container);
    }

    /// <summary>Profile settings, a project with a section, nested tags, a template, and a
    /// subtask tag link: the structure a single person sets up once.</summary>
    private static async Task StructureScenarioAsync(DataHost host, Dictionary<string, string> expected)
    {
        var profile = new ProfileSettingsCommands(host.Store, host.Ids, host.Clock);
        var settings = await profile.GetAsync();
        // The name is a synced column; the shortcut alone is device-local and would flush nothing.
        await profile.UpdateAsync(new UpdateProfileSettingsCommand(Op(), settings.Version, settings.DisplayName + " (flush)", settings.ZoneId,
            settings.DateFormat, "Ctrl+Shift+K", null, settings.KeepRunningInTray));
        expected["profile"] = (await TextsAsync(host, "SELECT Id FROM Profiles;"))[0];

        var containers = new ContainerCommands(host.Store, host.Ids, host.Clock);
        var personal = await host.Store.QueryAsync(async (session, token) =>
        {
            var person = await session.Profiles.GetPrimaryAsync(token);
            return await session.Spaces.GetPersonalByOwnerAsync(person!.Id, token);
        });
        expected["space_state"] = (await new SpaceStateCommands(host.Store, host.Ids, host.Clock)
            .CreateAsync(new CreateSpaceStateCommand(Op(), personal!.Id, "In review", TaskItemStatus.NeedsReview))).State.Id;
        var project = (await containers.CreateAsync(new CreateContainerCommand(Op(), "Flush project", ContainerKind.Project, null))).Container;
        var section = (await containers.CreateSectionAsync(new CreateSectionCommand(Op(), project.Id, "Planning"))).Section;
        expected["container"] = project.Id;
        expected["section"] = section.Id;

        var tags = new TagCommands(host.Store, host.Ids, host.Clock);
        expected["tag"] = (await tags.CreateAsync(new CreateTagCommand(Op(), "flush/structure", null, null))).Tag.Id;

        var template = (await new TemplateCommands(host.Store, host.Ids, host.Clock)
            .CreateAsync(new CreateTemplateCommand(Op(), "Flush template", new TemplatePayload { Title = "Template task", Subtasks = [] }, null))).Template;
        expected["task_template"] = template.Id;

        var capture = new CaptureCommands(host.Store, host.Ids, host.Clock);
        var task = (await capture.CreateAsync(new CreateCapturedTasksCommand(Op(),
            [new CaptureTaskDraft { Title = "Structure drive", Subtasks = [new CaptureSubtask("One step", false)] }]))).Tasks.Single();
        var subtaskId = (await TextsAsync(host, $"SELECT Id FROM Subtasks WHERE TaskId = '{task.Id}';"))[0];
        var linked = await tags.LinkAsync(new LinkTagCommand(Op(), TagTargetKind.Subtask, subtaskId, null, "flush"));
        expected["subtask_tag"] = $"{subtaskId}|{linked.TagId}";
    }

    /// <summary>A captured task with tags, subtasks, reminders and due date, a waiting task with
    /// a follow-up, a diary entry, a pad note with a tag, and a task made from the pad text.</summary>
    private static async Task WorkScenarioAsync(DataHost host, Dictionary<string, string> expected)
    {
        var capture = new CaptureCommands(host.Store, host.Ids, host.Clock);
        var created = (await capture.CreateAsync(new CreateCapturedTasksCommand(Op(),
        [
            new CaptureTaskDraft
            {
                Title = "Flush work",
                DueDate = new DateOnly(2026, 11, 14),
                ZoneId = "UTC",
                TagPaths = ["work/flush"],
                Subtasks = [new CaptureSubtask("Step one", false)],
                Reminders = [new CaptureReminder(CaptureReminderAnchor.Due, -60, null)],
            },
            new CaptureTaskDraft { Title = "Flush waiting", WaitingOn = "Sam", FollowUpDate = new DateOnly(2026, 11, 12) },
        ]))).Tasks;
        var work = created[0];
        var waiting = created[1];

        expected["task"] = work.Id;
        expected["subtask"] = (await TextsAsync(host, $"SELECT Id FROM Subtasks WHERE TaskId = '{work.Id}';"))[0];
        expected["task_tag"] = (await TextsAsync(host, $"SELECT TaskId || '|' || TagId FROM TaskTags WHERE TaskId = '{work.Id}';"))[0];
        expected["reminder_rule"] = (await TextsAsync(host, $"SELECT Id FROM ReminderRules WHERE TaskId = '{work.Id}';"))[0];
        expected["waiting_detail"] = (await TextsAsync(host, $"SELECT Id FROM WaitingDetails WHERE TaskId = '{waiting.Id}';"))[0];
        expected["follow_up"] = (await TextsAsync(host, $"SELECT Id FROM FollowUps WHERE TaskId = '{waiting.Id}';"))[0];
        // task_history: the capture recorded history rows keyed by their own ids; kind presence is enough.

        var entry = (await new DiaryCommands(host.Store, host.Ids, host.Clock)
            .AddAsync(new AddDiaryEntryCommand(Op(), work.Id, "Bought the **blue** sample.", null))).Entry;
        expected["diary_entry"] = entry.Id;

        var pad = new PadCommands(host.Store, host.Ids, host.Clock);
        var item = (await pad.CreateAsync(new CreatePadItemCommand(Op(), "buy milk"))).Item;
        expected["pad"] = item.Id;
        var drawing = (await pad.CreateDrawingAsync(new CreatePadDrawingCommand(Op()))).Item;
        await pad.SaveDrawingAsync(new SavePadDrawingCommand(drawing.Id, 1, "{\"v\":1,\"strokes\":[{\"points\":[[1,2,0.5]]}]}"));
        expected["pad_drawing"] = drawing.Id;
        const string quote = "buy milk";
        var made = await pad.MakeTaskAsync(new MakeTaskFromPadCommand(Op(), item.Id, item.DraftVersion, 0, quote.Length, quote,
            "Buy milk", null, null, null, null, []));
        expected["scratch_task_link"] = $"{item.Id}|{made.Task.Id}";
        var tags = new TagCommands(host.Store, host.Ids, host.Clock);
        var linked = await tags.LinkAsync(new LinkTagCommand(Op(), TagTargetKind.Pad, item.Id, null, "work"));
        expected["pad_tag"] = $"{item.Id}|{linked.TagId}";
    }

    /// <summary>A series started on a captured task, the occurrence moved (an exception), and the
    /// occurrence completed (the successor materialized).</summary>
    private static async Task RecurrenceScenarioAsync(DataHost host, Dictionary<string, string> expected)
    {
        var tasks = new TaskCommands(host.Store, host.Ids, host.Clock);
        var task = (await tasks.CaptureAsync(new(Op(), string.Empty, "Flush recurrence", null))).Task;
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Day, 1, new(2026, 11, 10), null, "UTC", [],
            null, null, null, null, RecurrenceEndKind.Never, null, null);
        var recurrence = new RecurrenceCommands(host.Store, host.Ids, host.Clock);
        var started = await recurrence.StartAsync(new(Op(), task.Id, task.Version, rule));
        expected["recurrence_series"] = started.Series.Id;
        expected["recurrence_occurrence"] = started.Occurrence.Id;

        var moved = await recurrence.MoveAsync(new MoveOccurrenceCommand(Op(), task.Id, (await tasks.GetAsync(task.Id))!.Version, new DateOnly(2026, 11, 12), null));
        expected["recurrence_exception"] = $"{started.Series.Id}|{started.Occurrence.NominalKey}";
        await tasks.SetStatusAsync(new(Op(), task.Id, moved.Task.Version, TaskItemStatus.Done, null));
    }

    /// <summary>A day plan with an item, a confirmed week with a goal (its task), an assignment,
    /// an availability override, and the standing weekly availability.</summary>
    private static async Task PlanningScenarioAsync(DataHost host, Dictionary<string, string> expected)
    {
        var tasks = new TaskCommands(host.Store, host.Ids, host.Clock);
        var task = (await tasks.CaptureAsync(new(Op(), string.Empty, "Flush planning", null))).Task;

        var days = new MyDayCommands(host.Store, host.Clock);
        var day = await days.OpenAsync(null);
        await days.AddAsync(new DayItemCommand(Op(), day.Date, task.Id));
        expected["day_plan"] = (await TextsAsync(host, "SELECT Id FROM DayPlans;"))[0];
        expected["day_plan_item"] = (await TextsAsync(host, $"SELECT Id FROM DayPlanItems WHERE TaskId = '{task.Id}';"))[0];

        var weeks = new MyWeekCommands(host.Store, host.Ids, host.Clock);
        var weekStart = (await weeks.OpenAsync(null)).WeekStart;
        var started = (await weeks.StartDraftAsync(new WeekCommand(Op(), weekStart))).Week;
        var draft = new WeekDraft(
            [new WeekDraftGoal(Guid.NewGuid().ToString(), "Ship the flush", null, [task.Id], WeekGoalOutcome.Pending)],
            [new WeekDraftAssignment(task.Id, day.Date)],
            [],
            [new WeekDraftOverride(day.Date, 300, 240)],
            Step: 3);
        var saved = (await weeks.SaveDraftAsync(new WeekDraftCommand(Op(), weekStart, started.DraftRevision, draft))).Week;
        await weeks.ConfirmAsync(new WeekRevisionCommand(Op(), weekStart, saved.DraftRevision));

        expected["week_plan"] = (await TextsAsync(host, "SELECT Id FROM WeekPlans;"))[0];
        expected["week_goal"] = (await TextsAsync(host, "SELECT Id FROM WeekGoals;"))[0];
        expected["week_goal_task"] = (await TextsAsync(host, "SELECT GoalId || '|' || TaskId FROM WeekGoalTasks;"))[0];
        expected["week_assignment"] = (await TextsAsync(host, $"SELECT Id FROM WeekAssignments WHERE TaskId = '{task.Id}';"))[0];
        expected["week_availability_override"] = (await TextsAsync(host, "SELECT ProfileId || '|' || LocalDate FROM WeekAvailabilityOverrides;"))[0];

        await weeks.SetAvailabilityAsync(new WeekAvailabilityCommand(Op(), [new AvailabilityDay(day.Date.DayOfWeek, 480, 300)]));
        expected["availability_rule"] = (await TextsAsync(host, "SELECT ProfileId || '|' || Weekday FROM AvailabilityRules;"))[0];
    }

    /// <summary>A generated daily summary and a committed CSV import (batch and items).</summary>
    private static async Task RecordsScenarioAsync(DataHost host, Dictionary<string, string> expected)
    {
        var view = await new SummaryCommands(host.Store, host.Ids, host.Clock)
            .GenerateAsync(new GenerateSummaryCommand(Op(), SummaryKind.Daily, new DateOnly(2026, 11, 10)));
        expected["summary"] = view.Summary.Id;

        const string csv = "Title,List,Tags,id\nImported thing,Travel,imported; home,flush-1\n";
        var source = DataTransfer.Read(ImportSourceKind.Csv, "flush.csv", Encoding.UTF8.GetBytes(csv), null);
        await new ImportCommands(host.Store, host.Ids, host.Clock)
            .CommitAsync(source.Draft, new ImportCommitOptions(new Dictionary<string, string>(), null, null));
        expected["import_batch"] = (await TextsAsync(host, "SELECT Id FROM ImportBatches;"))[0];
        expected["imported_item"] = (await TextsAsync(host, "SELECT ItemFingerprint FROM ImportedItems;"))[0];
    }

    /// <summary>A page with front-matter tags (a page-tag link) and a task made from a selection
    /// of its text (a page-task anchor).</summary>
    private static async Task PagesScenarioAsync(DataHost host, Dictionary<string, string> expected)
    {
        var tags = new TagCommands(host.Store, host.Ids, host.Clock);
        await tags.CreateAsync(new CreateTagCommand(Op(), "pages/flush", null, null));

        var text = "---\ntags: [pages/flush]\n---\n\n# Flush page\n\nCall the bank.";
        var page = (await new PageCommands(host.Store, host.Ids, host.Clock)
            .CreateAsync(new CreatePageCommand(Op(), null, "Flush page", text))).Page;
        expected["page"] = page.Id;
        expected["page_tag"] = (await TextsAsync(host, $"SELECT PageId || '|' || TagId FROM PageTags WHERE PageId = '{page.Id}';"))[0];

        const string quote = "Call the bank.";
        var start = text.IndexOf(quote, StringComparison.Ordinal);
        var made = await new PageCommands(host.Store, host.Ids, host.Clock).MakeTaskAsync(
            new MakeTaskFromPageCommand(Op(), page.Id, 0, start, start + quote.Length, quote, new CaptureTaskDraft { Title = "Call the bank" }));
        expected["page_task_link"] = $"{page.Id}|{made.Task.Id}";

        var attachments = host.Attachments;
        var upload = await attachments.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Page, page.Id, "flush.txt", 5));
        await attachments.ChunkAsync(upload.UploadId, 0, "hello"u8.ToArray());
        expected["attachment"] = (await attachments.CommitAsync(new CommitAttachmentCommand(Op(), upload.UploadId))).Attachment.Id;
    }

    /// <summary>A connection, a repository with milestones-as-projects, an imported issue with
    /// labels (label mapping), a checklist (checklist items) and a milestone (project mapping),
    /// and an inbound comment (comment link) — all against the FakeGitHub API, never the network.</summary>
    private static async Task GitHubScenarioAsync(DataHost host, Dictionary<string, string> expected)
    {
        var api = new FakeGitHub();
        api.PutRepository(new(10, "R10", "repo", "person/repo", new(1, "person"), "https://github.com/person/repo", false, true));
        var commands = new GitHubConnectionCommands(host.Store, api, host.Ids, host.Clock);
        var connection = await commands.ConnectAsync(new(DataHost.Op()));
        expected["github_connection"] = connection.Id;

        var inbox = await host.Store.QueryAsync(async (s, ct) => (await s.Containers.GetInboxAsync(connection.SpaceId, ct))!.Id);
        var repo = await commands.AddRepositoryAsync(new(Op(), connection.Id, connection.Version, "person", "repo", 10, inbox, null, GitHubFilter.Empty, true));
        expected["github_repository"] = repo.Id;

        var issue = new GitHubIssue(1, "I_1", 1, "https://github.com/person/repo/issues/1", "Flush issue", "Body\r\n- [ ] First\r\n- [x] Second",
            "open", null, new(1, "person"), [new GitHubLabel(11, "work/bug", "abcdef")], [],
            new GitHubMilestone(22, 1, "Release", "open", DataHost.Start.AddDays(7)), null, DataHost.Start, DataHost.Start, null, 0, null);
        await new GitHubInboundCommands(host.Store, host.Ids, host.Clock).ApplyAsync(repo.Id, repo.Version, [issue]);

        var link = (await host.Store.QueryAsync((s, ct) => s.GitHub.FindLinkByIssueAsync(connection.Id, 1, ct)))!;
        expected["github_issue_link"] = link.Id;
        expected["github_checklist_item"] = (await TextsAsync(host, $"SELECT SubtaskId FROM GitHubChecklistItems WHERE IssueLinkId = '{link.Id}';"))[0];
        expected["github_label_tag"] = (await TextsAsync(host, "SELECT RepositorySubscriptionId || '|' || LabelId FROM GitHubLabelTags;"))[0];
        expected["github_milestone_project"] = (await TextsAsync(host, "SELECT RepositorySubscriptionId || '|' || MilestoneNumber FROM GitHubMilestoneProjects;"))[0];

        var comment = new GitHubComment(5, "A comment", new(2, "other"), DataHost.Start, DataHost.Start, "https://github.com/person/repo/issues/1#issuecomment-5");
        await new GitHubInboundComments(host.Store, host.Ids, host.Clock).ApplyAsync(link.Id, link.Version, [comment], true);
        expected["github_comment_link"] = (await TextsAsync(host, "SELECT DiaryEntryId FROM GitHubCommentLinks;"))[0];
    }

    /// <summary>A list whose board is configured with a grouping and a WIP limit.</summary>
    private static async Task BoardScenarioAsync(DataHost host, Dictionary<string, string> expected)
    {
        var containers = new ContainerCommands(host.Store, host.Ids, host.Clock);
        var list = (await containers.CreateAsync(new CreateContainerCommand(Op(), "Flush board", ContainerKind.List, null))).Container;

        var boards = new BoardCommands(host.Store, host.Ids, host.Clock);
        var settings = await boards.UpdateSettingsAsync(new UpdateBoardSettingsCommand(
            Op(), BoardTargetKind.Container, list.Id, ExpectedVersion: 0,
            BoardGrouping.Priority, BoardSwimlane.None, null, null, "{\"Low\": 3}", "[]"));
        expected["board_setting"] = settings.Settings.Id;

        // A saved view (P3) rides in the same scenario: one pinned view with a definition.
        var view = await new Chaos.Application.Views.SavedViewCommands(host.Store, host.Ids, host.Clock).SaveAsync(new(
            Op(), null, 0, "Flush view", "bug", "blue",
            "{\"v\":1,\"items\":[{\"field\":\"priority\",\"op\":\"atLeast\",\"value\":\"High\"}]}", null, Pinned: true, AlertsEnabled: false));
        expected["saved_view"] = view.View.Id;
        expected["view_alert_setting"] = (await TextsAsync(host, "SELECT Id FROM ViewAlertSettings"))[0];
    }

    /// <summary>A calendar feed source and a task made from one of its meetings (P5).</summary>
    private static async Task CalendarsScenarioAsync(DataHost host, Dictionary<string, string> expected)
    {
        var calendars = new Chaos.Application.Calendar.CalendarCommands(host.Store, new NoSecrets(), host.Ids, host.Clock, new CaptureCommands(host.Store, host.Ids, host.Clock));
        var source = (await calendars.AddFeedAsync(new("https://calendar.example.org/flush.ics", "Flush feed", "#3366cc", true, null))).Source;
        expected["calendar_source"] = source.Id;

        // The meeting cache is device-local; seed one row the way a refresh would.
        await host.Store.ExecuteAsync(async (session, token) =>
        {
            await session.Calendars.ReplaceEventsAsync(source.Id,
                [new CalendarEventRecord(source.Id, "evt-1", DataHost.Start.AddDays(2), DataHost.Start.AddDays(2).AddHours(1), false, null, null, "Flush meeting", null, null, null, true, DataHost.Start)], token);
            return 0;
        });
        // A connected Google account (P6): the row syncs, the token stays in the secret store.
        var google = new Chaos.Application.Calendar.GoogleCalendarCommands(host.Store, new NoSecrets(), new FlushGoogle(), host.Clock);
        var account = await google.ConnectAsync();
        expected["google_account"] = account.Account.Id;

        var made = await calendars.MakeTaskAsync(new(Op(), source.Id, "evt-1", null));
        expected["calendar_event_link"] = Chaos.Application.Abstractions.StorageFormats.NameBasedId(Chaos.Sync.NameBasedId.Names.CalendarEventLink(made.Task.Id));
    }

    private sealed class FlushGoogle : IGoogleCalendarClient
    {
        public bool IsConfigured => true;
        public Task<GoogleSignIn> SignInAsync(CancellationToken ct) => Task.FromResult(new GoogleSignIn("flush-subject", "flush@example.com", "token"));
        public Task<IReadOnlyList<GoogleCalendarInfo>> ListCalendarsAsync(string refreshToken, CancellationToken ct) => Task.FromResult<IReadOnlyList<GoogleCalendarInfo>>([]);
        public Task<IReadOnlyList<ParsedMeeting>> ListEventsAsync(string refreshToken, string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) => Task.FromResult<IReadOnlyList<ParsedMeeting>>([]);
        public Task<bool> RevokeAsync(string refreshToken, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class NoSecrets : ISecretStore
    {
        public Task SetAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---- Helpers ---------------------------------------------------------------------------

    /// <summary>The outbox entity keys of the scenario's own data root, plus the keys the
    /// scenario knows exactly; every other kind is asserted by presence alone.</summary>
    private sealed record ScenarioRun(IReadOnlyDictionary<string, IReadOnlyList<string>> OutboxKeys, IReadOnlyDictionary<string, string> ExpectedKeys);

    private static string Op() => Guid.NewGuid().ToString();

    private static async Task<ScenarioRun> CollectAsync(DataHost host, Dictionary<string, string> expected)
    {
        var rows = await host.Store.QueryAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "SELECT Kind, EntityKey FROM SyncOutbox;";
            using var reader = await command.ExecuteReaderAsync(token);
            var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            while (await reader.ReadAsync(token))
            {
                var kind = reader.GetString(0);
                if (!grouped.TryGetValue(kind, out var keys))
                {
                    grouped[kind] = keys = [];
                }

                keys.Add(reader.GetString(1));
            }

            return grouped.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal);
        }, CancellationToken.None);
        return new ScenarioRun(rows, expected);
    }

    /// <summary>The first column of every row a query returns, as text.</summary>
    private static async Task<IReadOnlyList<string>> TextsAsync(DataHost host, string sql)
    {
        var texts = new List<string>();
        await host.Store.QueryAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = sql;
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                texts.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            }

            return 0;
        }, CancellationToken.None);
        return texts;
    }
}
