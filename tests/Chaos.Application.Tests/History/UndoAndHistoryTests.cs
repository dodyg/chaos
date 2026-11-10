using Chaos.Application.Abstractions;
using Chaos.Application.History;
using Chaos.Application.Tasks;
using Chaos.Application.Tests.Tasks;
using Chaos.Application.Tests.Testing;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Tests.History;

/// <summary>
/// Recorder and undo intent over fake ports (PLAN §9/§16): one history entry per changed
/// task with tag-aware snapshots, one undo entry per operation, all-or-nothing undo.
/// </summary>
public sealed class UndoAndHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task CompleteWithSubtasks_WritesOneHistoryEntryAndOneUndoEntry()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);
        var ids = new SequentialIdGenerator();
        var clock = new FixedTimeProvider(Now);
        var tasks = new TaskCommands(store, ids, clock);
        var subtasks = new SubtaskCommands(store, ids, clock);
        var task = (await tasks.CaptureAsync(new("capture", "", "Parent", null))).Task;
        await subtasks.AddAsync(new("sub-1", task.Id, "One", null));
        await subtasks.AddAsync(new("sub-2", task.Id, "Two", null));
        store.History.Clear();
        store.Undo.Clear();

        await tasks.SetStatusAsync(new("complete", task.Id, task.Version, TaskItemStatus.Done, OpenSubtaskChoice.CompleteAll));

        var entry = store.History.Single();
        await Assert.That(entry.EventKind).IsEqualTo("task.completed");
        await Assert.That(entry.ChangedFields).Contains("Status");
        await Assert.That(entry.ChangedFields).Contains("Subtasks");
        var after = StoredJsonFormats.ReadSnapshot<TaskAggregateSnapshot>(entry.SnapshotAfter!)!;
        await Assert.That(after.Subtasks.All(subtask => subtask.Status == TaskItemStatus.Done)).IsTrue();
        await Assert.That(after.TagIds!).IsEmpty();
        var undo = store.Undo.Single();
        await Assert.That(undo.OperationId).IsEqualTo("complete");
        var changes = StoredJsonFormats.ReadSnapshot<ChangeSet>(undo.InversePayload)!;
        await Assert.That(changes.Entities.Count).IsEqualTo(3);
    }

    [Test]
    public async Task Undo_OneOfSeveralRowsChangedLater_RefusesAndLeavesEveryRowAsItWas()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);
        var ids = new SequentialIdGenerator();
        var clock = new FixedTimeProvider(Now);
        var tasks = new TaskCommands(store, ids, clock);
        var subtasks = new SubtaskCommands(store, ids, clock);
        var undo = new UndoCommands(store, ids, clock);
        var task = (await tasks.CaptureAsync(new("capture", "", "Parent", null))).Task;
        var one = (await subtasks.AddAsync(new("sub-1", task.Id, "One", null))).Subtask;
        await tasks.SetStatusAsync(new("complete", task.Id, task.Version, TaskItemStatus.Done, OpenSubtaskChoice.CompleteAll));
        var completedOne = store.Subtasks.Single(row => row.Id == one.Id);
        await subtasks.UpdateAsync(new("rename", one.Id, completedOne.Version, "One, renamed", null));
        var snapshot = (Tasks: store.Tasks.ToList(), Subtasks: store.Subtasks.ToList(), Undo: store.Undo.ToList());

        AppException? error = null;
        try { await undo.UndoAsync(new("undo", "complete")); }
        catch (AppException caught) { error = caught; }

        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.VersionConflict);
        await Assert.That(error.Message).Contains("One, renamed");
        // The fake store has no rollback, so the undo must check every row before writing any.
        await Assert.That(store.Tasks.Single().Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That(store.Tasks.Single().Version).IsEqualTo(snapshot.Tasks.Single().Version);
    }

    [Test]
    public async Task Label_NamesTheChangedTask()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);
        var tasks = new TaskCommands(store, new SequentialIdGenerator(), new FixedTimeProvider(Now));
        await tasks.CaptureAsync(new("capture", "", "Water the plants", null));
        await Assert.That(store.Undo.Single().Label).IsEqualTo("Add task “Water the plants”");
    }
}
