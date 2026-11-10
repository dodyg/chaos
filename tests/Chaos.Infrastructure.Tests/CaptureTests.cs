using Chaos.Application.Abstractions;
using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Pad;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Domain.Capture;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests;

/// <summary>
/// PLAN §15 WP7 against real SQLite: quick-add previews never write, confirmed drafts are
/// created atomically with subtasks, nested tags, reminders and recurrence, a failing line
/// rolls back the whole batch, retries replay, one undo removes the batch, and tasks typed
/// in each of the six languages persist across restart.
/// </summary>
public sealed class CaptureTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class Host : IDisposable
    {
        public required ChaosStorageHandle Storage { get; init; }
        public required CaptureCommands Capture { get; init; }
        public required TaskCommands Tasks { get; init; }
        public required TagCommands Tags { get; init; }
        public required UndoCommands Undo { get; init; }
        public required PadCommands Pad { get; init; }
        public required SequentialIdGenerator Ids { get; init; }

        public static async Task<Host> StartAsync(string root)
        {
            var clock = new FixedTimeProvider(Now);
            var ids = new SequentialIdGenerator();
            var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
            return new Host
            {
                Storage = storage,
                Capture = new CaptureCommands(storage.Store, new GuidIds(), clock),
                Tasks = new TaskCommands(storage.Store, new GuidIds(), clock),
                Tags = new TagCommands(storage.Store, new GuidIds(), clock),
                Undo = new UndoCommands(storage.Store, new GuidIds(), clock),
                Pad = new PadCommands(storage.Store, new GuidIds(), clock),
                Ids = ids,
            };
        }

        public void Dispose() => Storage.Dispose();

        /// <summary>Previews the text and saves exactly what the preview proposes, as the UI does.</summary>
        public async Task<(CapturePreview Preview, CapturedTasksResult Result)> AddAsync(string text, string? operationId = null)
        {
            var preview = await Capture.PreviewAsync(text, []);
            var result = await Capture.CreateAsync(new(operationId ?? Op(), [.. preview.Items.Select(ToDraft)]));
            return (preview, result);
        }

        public async Task<IReadOnlyList<TaskRecord>> InboxAsync() => await Tasks.ListInboxAsync();
    }

    private sealed class GuidIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private static string Op() => Guid.NewGuid().ToString();

    private static CaptureTaskDraft ToDraft(CaptureItemPreview item)
    {
        var draft = item.Parse.Draft;
        return new CaptureTaskDraft
        {
            Title = draft.Title,
            ContainerId = draft.ContainerId,
            Priority = draft.Priority ?? TaskPriority.Normal,
            EstimateMinutes = draft.EstimateMinutes,
            PlannedDate = draft.PlannedDate,
            PlannedTime = draft.PlannedTime,
            DueDate = draft.DueDate,
            DueTime = draft.DueTime,
            ZoneId = draft.ZoneId,
            Recurrence = draft.Recurrence,
            Reminders = draft.Reminders,
            TagPaths = [.. draft.Tags.Select(tag => tag.Path)],
            WaitingOn = draft.WaitingOn,
            FollowUpDate = draft.FollowUpDate,
            FollowUpTime = draft.FollowUpTime,
            Subtasks = item.Subtasks,
        };
    }

    private static DateOnly TodayIn(string zoneId) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Now, TimeZoneInfo.FindSystemTimeZoneById(zoneId)).DateTime);

    [Test]
    public async Task Preview_RecognizesNewTag_WritesNothing()
    {
        using var host = await Host.StartAsync(TestPaths.NewDataRoot());
        var preview = await host.Capture.PreviewAsync("Plan trip tomorrow #travel/2027", []);
        await Assert.That(preview.Items.Single().Parse.Draft.Tags.Single().Existing).IsFalse();
        await Assert.That((await host.Tags.ListAsync()).Count).IsEqualTo(0);
        await Assert.That((await host.InboxAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Create_PastedListWithMetadata_PersistsEverythingAcrossRestart()
    {
        var root = TestPaths.NewDataRoot();
        string zone;
        using (var host = await Host.StartAsync(root))
        {
            var (preview, result) = await host.AddAsync("Pay rent every month on the 1st #home/bills !high\nPack bag for 1h\n  - socks\n  - [x] charger");
            zone = preview.ZoneId;
            await Assert.That(preview.IsBatch).IsTrue();
            await Assert.That(result.Tasks.Count).IsEqualTo(2);
        }

        using var reopened = await Host.StartAsync(root);
        var tasks = await reopened.InboxAsync();
        var rent = tasks.Single(task => task.Title == "Pay rent");
        var pack = tasks.Single(task => task.Title == "Pack bag");
        await Assert.That(rent.Priority).IsEqualTo(TaskPriority.High);
        await Assert.That(rent.RecurrenceOccurrenceKey).IsNotNull();
        var series = await new RecurrenceCommands(reopened.Storage.Store, reopened.Ids, new FixedTimeProvider(Now)).GetForTaskAsync(rent.Id);
        await Assert.That(series!.Rule.MonthDay).IsEqualTo(1);
        await Assert.That(series.Rule.Frequency).IsEqualTo(RecurrenceFrequency.Month);
        await Assert.That(rent.PlannedDate!.Value.Day).IsEqualTo(1);
        await Assert.That(rent.PlannedDate.Value > TodayIn(zone)).IsTrue();
        var tagPaths = (await reopened.Tags.ListAsync()).Select(tag => tag.Path).ToList();
        await Assert.That(tagPaths).Contains("home/bills");
        await Assert.That((await reopened.Tags.ListTargetTagsAsync(TagTargetKind.Task, rent.Id)).Count).IsEqualTo(1);
        await Assert.That(pack.EstimateMinutes).IsEqualTo(60);
        var subtasks = (await reopened.Tasks.GetDetailAsync(pack.Id)).Subtasks;
        await Assert.That(subtasks.Select(subtask => subtask.Title).ToArray()).IsEquivalentTo(new[] { "socks", "charger" });
        await Assert.That(subtasks.Single(subtask => subtask.Title == "charger").Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That(subtasks.Single(subtask => subtask.Title == "socks").Status).IsEqualTo(TaskItemStatus.NotStarted);
    }

    [Test]
    public async Task Create_InvalidLastDraft_SavesNothing()
    {
        using var host = await Host.StartAsync(TestPaths.NewDataRoot());
        var drafts = new List<CaptureTaskDraft>
        {
            new() { Title = "First", TagPaths = ["errands"] },
            new() { Title = "Second" },
            new() { Title = "Third", PlannedTime = new TimeOnly(9, 0) }, // a time without a date
        };

        AppException? error = null;
        try { await host.Capture.CreateAsync(new(Op(), drafts)); }
        catch (AppException caught) { error = caught; }

        await Assert.That(error!.Fields.Single().FieldPath).IsEqualTo("drafts[2].plannedTime");
        await Assert.That((await host.InboxAsync()).Count).IsEqualTo(0);
        await Assert.That((await host.Tags.ListAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Create_UnknownListInLastDraft_RollsBackEarlierTasksAndTags()
    {
        using var host = await Host.StartAsync(TestPaths.NewDataRoot());
        var drafts = new List<CaptureTaskDraft> { new() { Title = "First", TagPaths = ["errands"] }, new() { Title = "Second", ContainerId = Guid.NewGuid().ToString() } };
        await Assert.That(async () => await host.Capture.CreateAsync(new(Op(), drafts))).Throws<AppException>();
        await Assert.That((await host.InboxAsync()).Count).IsEqualTo(0);
        await Assert.That((await host.Tags.ListAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Create_SameOperationTwice_ReplaysWithoutDuplicates()
    {
        using var host = await Host.StartAsync(TestPaths.NewDataRoot());
        var operation = Op();
        var first = await host.Capture.CreateAsync(new(operation, [new() { Title = "Once" }]));
        var again = await host.Capture.CreateAsync(new(operation, [new() { Title = "Once" }]));
        await Assert.That(again.Replayed).IsTrue();
        await Assert.That(again.Tasks.Single().Id).IsEqualTo(first.Tasks.Single().Id);
        await Assert.That((await host.InboxAsync()).Count).IsEqualTo(1);
        await Assert.That(async () => await host.Capture.CreateAsync(new(operation, [new() { Title = "Different" }]))).Throws<AppException>();
    }

    [Test]
    public async Task Undo_PastedBatch_RemovesEveryTaskInOneStep()
    {
        using var host = await Host.StartAsync(TestPaths.NewDataRoot());
        var operation = Op();
        await host.AddAsync("Alpha\nBeta\n  - child\nGamma", operation);
        await Assert.That((await host.InboxAsync()).Count).IsEqualTo(3);
        var entry = (await host.Undo.ListAsync(10)).Single();
        await Assert.That(entry.OperationId).IsEqualTo(operation);
        await host.Undo.UndoAsync(new UndoOperationCommand(Op(), operation));
        await Assert.That((await host.InboxAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Create_ReminderAndWaiting_StoresRuleStatusAndWaitingRecords()
    {
        using var host = await Host.StartAsync(TestPaths.NewDataRoot());
        var (_, dentist) = await host.AddAsync("Dentist tomorrow 10:30 remind me 1h before");
        var rules = await host.Storage.Store.QueryAsync((session, ct) => session.ReminderRules.ListByTaskAsync(dentist.Tasks.Single().Id, ct));
        await Assert.That(rules.Single().Anchor).IsEqualTo(ReminderAnchor.Planned);
        await Assert.That(rules.Single().OffsetMinutes).IsEqualTo(-60);
        await Assert.That(dentist.Tasks.Single().PlannedTime).IsEqualTo(new TimeOnly(10, 30));
        await Assert.That(dentist.Tasks.Single().PlannedZoneId).IsNotNull();

        var (preview, contract) = await host.AddAsync("Contract waiting on Omar follow up Friday");
        var task = contract.Tasks.Single();
        await Assert.That(task.Status).IsEqualTo(TaskItemStatus.WaitingOnSomeone);
        await Assert.That(task.Title).IsEqualTo("Contract");
        await Assert.That(task.Notes).IsEqualTo(string.Empty);
        await Assert.That(preview.Items.Single().Parse.Warnings.Select(warning => warning.Code)).Contains("waitingPrivate");
        var (waiting, followUp) = await host.Storage.Store.QueryAsync(async (session, ct) =>
            (await session.Waiting.FindActiveAsync(task.Id, ct), await session.Waiting.FindActiveFollowUpAsync(task.Id, ct)));
        await Assert.That(waiting!.Person).IsEqualTo("Omar");
        await Assert.That(followUp!.Kind).IsEqualTo(Chaos.Domain.Reminders.FollowUpKind.Fixed);
        await Assert.That(followUp.DueAtUtc).IsEqualTo(followUp.FixedAtUtc);
    }

    [Test]
    [Arguments("Call plumber tomorrow at 3pm #home")]
    [Arguments("Appeler le plombier demain à 15h #maison")]
    [Arguments("Chiamare l'idraulico domani alle 15 #casa")]
    [Arguments("Llamar al fontanero mañana a las 15:00 #casa")]
    [Arguments("Telepon tukang ledeng besok jam 15.00 #rumah")]
    [Arguments("اتصل بالسباك غدًا الساعة ١٥:٠٠ #البيت")]
    public async Task Create_EachLanguage_PersistsTomorrowAtFifteenAcrossRestart(string text)
    {
        var root = TestPaths.NewDataRoot();
        string zone;
        string id;
        using (var host = await Host.StartAsync(root))
        {
            var (preview, result) = await host.AddAsync(text);
            zone = preview.ZoneId;
            id = result.Tasks.Single().Id;
            await Assert.That(preview.Items.Single().Parse.Draft.Title.Contains('#')).IsFalse();
        }

        using var reopened = await Host.StartAsync(root);
        var saved = (await reopened.Tasks.GetAsync(id))!;
        await Assert.That(saved.PlannedDate).IsEqualTo(TodayIn(zone).AddDays(1));
        await Assert.That(saved.PlannedTime).IsEqualTo(new TimeOnly(15, 0));
        await Assert.That((await reopened.Tags.ListTargetTagsAsync(TagTargetKind.Task, id)).Count).IsEqualTo(1);
    }

    [Test]
    public async Task MakeTaskFromPad_ParsedDraft_CreatesTaskWithDueTagAndBacklink()
    {
        using var host = await Host.StartAsync(TestPaths.NewDataRoot());
        var item = (await host.Pad.CreateAsync(new(Op(), string.Empty))).Item;
        const string text = "Ideas\nsend invoice by Friday #work";
        await host.Pad.SaveTextAsync(new(item.Id, 1, text));
        var quote = "send invoice by Friday #work";
        var preview = await host.Capture.PreviewAsync(quote, []);
        var draft = ToDraft(preview.Items.Single());
        var start = text.IndexOf(quote, StringComparison.Ordinal);

        var made = await host.Pad.MakeTaskAsync(new(Op(), item.Id, 1, start, start + quote.Length, quote, draft.Title, null, null, null, null, [], draft));

        await Assert.That(made.Task.Title).IsEqualTo("send invoice");
        await Assert.That(made.Task.DueDate).IsNotNull();
        await Assert.That(made.Task.DueDate!.Value.DayOfWeek).IsEqualTo(DayOfWeek.Friday);
        await Assert.That((await host.Tags.ListTargetTagsAsync(TagTargetKind.Task, made.Task.Id)).Count).IsEqualTo(1);
        await Assert.That(made.Link.Quote).IsEqualTo(quote);
        await Assert.That((await host.Pad.GetAsync(item.Id)).Item.RawMarkdown).IsEqualTo(text);
    }
}
