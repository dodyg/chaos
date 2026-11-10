using Chaos.Application.Abstractions;
using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Queries;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests;

/// <summary>
/// PLAN §15 WP4 against real SQLite: as-of before create/edit/delete, atomic undo with
/// conflict detection across aggregates, diary revisions/search, checklist conversion twice.
/// </summary>
public sealed class HistoryUndoTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(int minutes = 1) => Now = Now.AddMinutes(minutes);
    }

    private sealed class GuidIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class Host : IDisposable
    {
        public required string Root { get; init; }
        public required ChaosStorageHandle Storage { get; init; }
        public required ManualClock Clock { get; init; }
        public required TaskCommands Tasks { get; init; }
        public required SubtaskCommands Subtasks { get; init; }
        public required ContainerCommands Containers { get; init; }
        public required TagCommands Tags { get; init; }
        public required TaskQueries Queries { get; init; }
        public required UndoCommands Undo { get; init; }
        public required TaskHistoryCommands History { get; init; }
        public required DiaryCommands Diary { get; init; }
        public required ChecklistCommands Checklists { get; init; }

        public static async Task<Host> StartAsync(string? root = null, ManualClock? clock = null)
        {
            root ??= TestPaths.NewDataRoot();
            clock ??= new ManualClock(Start);
            var ids = new GuidIds();
            var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
            var tasks = new TaskCommands(storage.Store, ids, clock);
            return new Host
            {
                Root = root,
                Storage = storage,
                Clock = clock,
                Tasks = tasks,
                Subtasks = new SubtaskCommands(storage.Store, ids, clock),
                Containers = new ContainerCommands(storage.Store, ids, clock),
                Tags = new TagCommands(storage.Store, ids, clock),
                Queries = new TaskQueries(storage.Store, clock),
                Undo = new UndoCommands(storage.Store, ids, clock),
                History = new TaskHistoryCommands(storage.Store, tasks),
                Diary = new DiaryCommands(storage.Store, ids, clock),
                Checklists = new ChecklistCommands(storage.Store, ids, clock),
            };
        }

        public void Dispose() => Storage.Dispose();

        public async Task<UndoResult> UndoLatestAsync()
        {
            var latest = (await Undo.ListAsync(1)).Single();
            Clock.Advance();
            return await Undo.UndoAsync(new UndoOperationCommand(Op(), latest.OperationId));
        }
    }

    private static string Op() => Guid.NewGuid().ToString();

    private static async Task<AppException> ThrowsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (AppException error)
        {
            return error;
        }

        throw new InvalidOperationException("Expected an AppException.");
    }

    [Test]
    public async Task Undo_MistakenDeletion_RestoresTaskSubtasksTagsAndSearch()
    {
        using var host = await Host.StartAsync();
        var task = (await host.Tasks.CaptureAsync(new(Op(), "", "Call the plumber", "Ask about the boiler"))).Task;
        var first = (await host.Subtasks.AddAsync(new(Op(), task.Id, "Find number", null))).Subtask;
        var second = (await host.Subtasks.AddAsync(new(Op(), task.Id, "Pick a slot", null))).Subtask;
        var link = await host.Tags.LinkAsync(new(Op(), TagTargetKind.Task, task.Id, null, "home/repairs"));
        await host.Tags.LinkAsync(new(Op(), TagTargetKind.Subtask, second.Id, link.TagId, null));
        var current = (await host.Tasks.GetAsync(task.Id))!;
        host.Clock.Advance();
        await host.Tasks.DeleteAsync(new(Op(), task.Id, current.Version));
        await Assert.That(await host.Tasks.GetAsync(task.Id)).IsNull();

        var latest = (await host.Undo.ListAsync(5))[0];
        await Assert.That(latest.Label).IsEqualTo("Delete task “Call the plumber”");
        var result = await host.UndoLatestAsync();

        await Assert.That(result.Label).IsEqualTo(latest.Label);
        var detail = await host.Tasks.GetDetailAsync(task.Id);
        await Assert.That(detail.Task).IsNotNull();
        await Assert.That(string.Join(",", detail.Subtasks.Select(x => x.Id))).IsEqualTo($"{first.Id},{second.Id}");
        await Assert.That(await host.Tags.ListTargetTagsAsync(TagTargetKind.Task, task.Id)).Contains(link.TagId!);
        await Assert.That(await host.Tags.ListTargetTagsAsync(TagTargetKind.Subtask, second.Id)).Contains(link.TagId!);
        var found = await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Search, Text = "boiler" });
        await Assert.That(found.Rows.Select(x => x.Task.Id)).Contains(task.Id);

        // The undo itself is history, and is not offered for undo again.
        var history = await host.History.ListAsync(task.Id);
        await Assert.That(history[^1].EventKind).IsEqualTo("undo");
        await Assert.That((await host.Undo.ListAsync(5)).Select(x => x.OperationId)).DoesNotContain(latest.OperationId);
        var again = await ThrowsAsync(() => host.Undo.UndoAsync(new(Op(), latest.OperationId)));
        await Assert.That(again.Kind).IsEqualTo(AppErrorKind.OperationConflict);
    }

    [Test]
    public async Task Undo_LaterEditToOneAffectedTask_ConflictsAndAppliesNothing()
    {
        using var host = await Host.StartAsync();
        var list = (await host.Containers.CreateAsync(new(Op(), "Errands", ContainerKind.List, null))).Container;
        var a = (await host.Tasks.CaptureAsync(new(Op(), list.Id, "A", null))).Task;
        var b = (await host.Tasks.CaptureAsync(new(Op(), list.Id, "B", null))).Task;
        host.Clock.Advance();
        await host.Containers.DeleteAsync(new DeleteContainerCommand(Op(), list.Id, list.Version, ContainerContentsChoice.MoveToInbox));
        var deletion = (await host.Undo.ListAsync(1)).Single();

        // A later, separate edit to one of the moved tasks.
        var movedB = (await host.Tasks.GetAsync(b.Id))!;
        host.Clock.Advance();
        await host.Tasks.UpdateDetailsAsync(new(Op(), b.Id, movedB.Version, "B renamed", "", null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));

        var error = await ThrowsAsync(() => host.Undo.UndoAsync(new(Op(), deletion.OperationId)));
        await Assert.That(error.Kind).IsEqualTo(AppErrorKind.VersionConflict);
        await Assert.That(error.Message).Contains("B renamed");

        // Nothing was applied: the list is still deleted and both tasks are still in the Inbox.
        await Assert.That((await host.Containers.ListDeletedPersonalAsync()).Select(x => x.Id)).Contains(list.Id);
        var inbox = await host.Tasks.ListInboxAsync();
        await Assert.That(inbox.Select(x => x.Id)).Contains(a.Id);
        await Assert.That(inbox.Single(x => x.Id == b.Id).Title).IsEqualTo("B renamed");
        await Assert.That((await host.Undo.ListAsync(5)).Select(x => x.OperationId)).Contains(deletion.OperationId);

        // Undoing the later edit first makes the older undo exact again.
        await host.UndoLatestAsync();
        host.Clock.Advance();
        await host.Undo.UndoAsync(new(Op(), deletion.OperationId));
        var restored = await host.Tasks.ListAsync(list.Id);
        await Assert.That(string.Join(",", restored.Select(x => x.Title))).IsEqualTo("A,B");
    }

    [Test]
    public async Task Undo_TagMergeAndTagDelete_RestoreTagsAndLinks()
    {
        using var host = await Host.StartAsync();
        var task = (await host.Tasks.CaptureAsync(new(Op(), "", "Fix the bike", null))).Task;
        var source = (await host.Tags.CreateAsync(new(Op(), "errand", null, null))).Tag;
        var target = (await host.Tags.CreateAsync(new(Op(), "errands", null, null))).Tag;
        await host.Tags.LinkAsync(new(Op(), TagTargetKind.Task, task.Id, source.Id, null));
        host.Clock.Advance();
        await host.Tags.MergeAsync(new(Op(), source.Id, source.Version, target.Id, target.Version));
        await Assert.That(await host.Tags.ListTargetTagsAsync(TagTargetKind.Task, task.Id)).IsEquivalentTo(new[] { target.Id });

        await host.UndoLatestAsync();
        await Assert.That(await host.Tags.ListTargetTagsAsync(TagTargetKind.Task, task.Id)).IsEquivalentTo(new[] { source.Id });
        await Assert.That((await host.Tags.ListAsync()).Select(x => x.Tag.Id)).Contains(source.Id);

        var live = (await host.Tags.ListAsync()).Single(x => x.Tag.Id == source.Id).Tag;
        host.Clock.Advance();
        await host.Tags.DeleteAsync(new(Op(), live.Id, live.Version));
        await Assert.That(await host.Tags.ListTargetTagsAsync(TagTargetKind.Task, task.Id)).IsEmpty();
        await host.UndoLatestAsync();
        await Assert.That(await host.Tags.ListTargetTagsAsync(TagTargetKind.Task, task.Id)).IsEquivalentTo(new[] { source.Id });

        // Tag changes are task history: the link shows in the snapshot.
        var history = await host.History.ListAsync(task.Id);
        await Assert.That(history.Select(x => x.EventKind)).Contains("task.tags-changed");
    }

    [Test]
    public async Task AsOf_BeforeCreateAfterEditAndAfterDelete_ReturnsEachState()
    {
        using var host = await Host.StartAsync();
        var beforeCreate = host.Clock.Now.AddMinutes(-5);
        var task = (await host.Tasks.CaptureAsync(new(Op(), "", "Draft letter", null))).Task;
        var created = host.Clock.Now;
        host.Clock.Advance(10);
        var edited = (await host.Tasks.UpdateDetailsAsync(new(Op(), task.Id, task.Version, "Send letter", "Stamp first", null, null, null, null, null, null, TaskPriority.High, 15, CapacityCategory.Personal))).Task;
        var afterEdit = host.Clock.Now;
        host.Clock.Advance(10);
        await host.Tags.LinkAsync(new(Op(), TagTargetKind.Task, task.Id, null, "post"));
        host.Clock.Advance(10);
        var withTag = (await host.Tasks.GetAsync(task.Id))!;
        await host.Tasks.DeleteAsync(new(Op(), task.Id, withTag.Version));
        var afterDelete = host.Clock.Now;

        var none = await host.History.AsOfAsync(task.Id, beforeCreate);
        await Assert.That(none.Existed).IsFalse();
        var first = await host.History.AsOfAsync(task.Id, created);
        await Assert.That(first.Snapshot!.Task.Title).IsEqualTo("Draft letter");
        var second = await host.History.AsOfAsync(task.Id, afterEdit.AddMinutes(5));
        await Assert.That(second.Snapshot!.Task.Title).IsEqualTo("Send letter");
        await Assert.That(second.Snapshot.Task.Priority).IsEqualTo(TaskPriority.High);
        await Assert.That(second.Snapshot.TagIds!).IsEmpty();
        var tagged = await host.History.AsOfAsync(task.Id, afterDelete.AddMinutes(-1));
        await Assert.That(tagged.Snapshot!.TagIds!.Count).IsEqualTo(1);
        var gone = await host.History.AsOfAsync(task.Id, afterDelete);
        await Assert.That(gone.Snapshot!.Task.DeletedAtUtc).IsNotNull();
        await Assert.That(gone.Entry!.EventKind).IsEqualTo("task.deleted");

        // Restoring an earlier value is a new recorded (and undoable) edit.
        var restoredTask = (await host.Tasks.RestoreAsync(new(Op(), task.Id, gone.Snapshot.Task.Version))).Task;
        var version1 = (await host.History.ListAsync(task.Id))[0];
        host.Clock.Advance();
        var back = (await host.History.RestoreFieldsAsync(new(Op(), task.Id, restoredTask.Version, version1.Id, [RestorableTaskField.Title]))).Task;
        await Assert.That(back.Title).IsEqualTo("Draft letter");
        await Assert.That(back.Notes).IsEqualTo(edited.Notes);
        await Assert.That(back.Priority).IsEqualTo(TaskPriority.High);
        await host.UndoLatestAsync();
        await Assert.That((await host.Tasks.GetAsync(task.Id))!.Title).IsEqualTo("Send letter");
    }

    [Test]
    public async Task Diary_AddEditDeleteUndo_KeepsRevisionsTimelineAndSearch()
    {
        using var host = await Host.StartAsync();
        var task = (await host.Tasks.CaptureAsync(new(Op(), "", "Renew passport", null))).Task;
        host.Clock.Advance();
        var entry = (await host.Diary.AddAsync(new(Op(), task.Id, "Booked an **appointment** at the consulate", null))).Entry;
        host.Clock.Advance();
        var edited = (await host.Diary.EditAsync(new(Op(), entry.Id, entry.Version, "Booked an appointment for Thursday"))).Entry;
        await Assert.That(edited.Revision).IsEqualTo(2L);
        await Assert.That(edited.EntryAtUtc).IsEqualTo(entry.EntryAtUtc);
        await Assert.That(edited.EditedAtUtc).IsNotNull();

        var revisions = await host.Diary.RevisionsAsync(entry.Id);
        await Assert.That(revisions.Select(x => x.RawMarkdown)).IsEquivalentTo(new[] { "Booked an **appointment** at the consulate", "Booked an appointment for Thursday" });
        var thursday = await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Search, Text = "thursday" });
        await Assert.That(thursday.Rows.Select(x => x.Task.Id)).Contains(task.Id);
        var consulate = await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Search, Text = "consulate" });
        await Assert.That(consulate.Rows).IsEmpty();

        var timeline = await host.Diary.TimelineAsync(task.Id);
        await Assert.That(timeline[0].Event!.EventKind).IsEqualTo("task.created");
        await Assert.That(timeline.Count(x => x.Diary is not null)).IsEqualTo(1);
        await Assert.That(timeline.Select(x => x.Event?.EventKind)).Contains("diary.edited");

        host.Clock.Advance();
        await host.Diary.DeleteAsync(new(Op(), entry.Id, edited.Version));
        await Assert.That(await host.Diary.ListAsync(task.Id, includeDeleted: false)).IsEmpty();
        await Assert.That((await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Search, Text = "thursday" })).Rows).IsEmpty();
        await host.UndoLatestAsync();
        var back = (await host.Diary.ListAsync(task.Id, includeDeleted: false)).Single();
        await Assert.That(back.RawMarkdown).IsEqualTo("Booked an appointment for Thursday");
        await Assert.That(back.Revision).IsEqualTo(4L);
        await Assert.That((await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Search, Text = "thursday" })).Rows.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ChecklistConversion_Twice_CreatesSubtasksOnceAndUndoRemovesThem()
    {
        using var host = await Host.StartAsync();
        var task = (await host.Tasks.CaptureAsync(new(Op(), "", "Pack", "- [ ] Passport\n- [x] Charger\n- [ ] Passport"))).Task;
        ChecklistItem[] items = [new("Passport#0", "Passport", false), new("Charger#0", "Charger", true), new("Passport#1", "Passport", false)];

        var first = await host.Checklists.ConvertAsync(new(Op(), task.Id, items));
        await Assert.That(first.Created.Count).IsEqualTo(3);
        await Assert.That(first.Created[1].Status).IsEqualTo(TaskItemStatus.Done);
        var second = await host.Checklists.ConvertAsync(new(Op(), task.Id, items));
        await Assert.That(second.Created).IsEmpty();
        await Assert.That(second.AlreadyConverted).IsEqualTo(3);
        var detail = await host.Tasks.GetDetailAsync(task.Id);
        await Assert.That(string.Join(",", detail.Subtasks.Select(x => x.Title))).IsEqualTo("Passport,Charger,Passport");
        await Assert.That(detail.Task!.Notes).IsEqualTo("- [ ] Passport\n- [x] Charger\n- [ ] Passport");

        // One undo removes the whole conversion; converting again then works.
        var undo = (await host.Undo.ListAsync(5)).Single(x => x.Label.StartsWith("Convert checklist", StringComparison.Ordinal));
        host.Clock.Advance();
        await host.Undo.UndoAsync(new(Op(), undo.OperationId));
        await Assert.That((await host.Tasks.GetDetailAsync(task.Id)).Subtasks).IsEmpty();
        var third = await host.Checklists.ConvertAsync(new(Op(), task.Id, items[..1]));
        await Assert.That(third.Created.Count).IsEqualTo(1);
    }

    [Test]
    public async Task UndoEntries_PersistAcrossRestartAndKeepTheLatestHundred()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new ManualClock(Start);
        string taskId;
        using (var host = await Host.StartAsync(root, clock))
        {
            var task = (await host.Tasks.CaptureAsync(new(Op(), "", "Keep me", null))).Task;
            taskId = task.Id;
            for (var index = 0; index < 105; index++)
            {
                clock.Advance();
                var current = (await host.Tasks.GetAsync(task.Id))!;
                await host.Tasks.UpdateDetailsAsync(new(Op(), task.Id, current.Version, $"Keep me {index}", "", null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
            }

            clock.Advance();
            await host.Tasks.DeleteAsync(new(Op(), task.Id, (await host.Tasks.GetAsync(task.Id))!.Version));
        }

        using (var host = await Host.StartAsync(root, clock))
        {
            await Assert.That((await host.Undo.ListAsync(500)).Count).IsEqualTo(RecordingDataSession.UndoLimit);
            await host.UndoLatestAsync();
            await Assert.That((await host.Tasks.GetAsync(taskId))!.Title).IsEqualTo("Keep me 104");
        }
    }

    [Test]
    public async Task ReplayedOperation_WritesNoSecondHistoryOrUndoEntry()
    {
        using var host = await Host.StartAsync();
        var command = new CaptureTaskCommand(Op(), "", "Once", null);
        var task = (await host.Tasks.CaptureAsync(command)).Task;
        var replay = await host.Tasks.CaptureAsync(command);
        await Assert.That(replay.Replayed).IsTrue();
        await Assert.That((await host.History.ListAsync(task.Id)).Count).IsEqualTo(1);
        await Assert.That((await host.Undo.ListAsync(10)).Count(x => x.OperationId == command.OperationId)).IsEqualTo(1);
    }
}
