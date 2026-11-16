using Chaos.Application.Abstractions;
using Chaos.Application.Boards;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Application.Tests.Tasks;
using Chaos.Application.Tests.Testing;
using Chaos.Domain.Tasks;
using Chaos.Sync;

namespace Chaos.Application.Tests.Boards;

/// <summary>
/// Board command intent over fake ports (PLAN2 P2): each column type maps to the right field
/// change, one card move is one operation (one receipt, one undo entry), replays and payload
/// conflicts behave like every other command, and settings payloads are validated. Real
/// SQLite behavior, restart and undo are proven in Chaos.Infrastructure.Tests.
/// </summary>
public sealed class BoardCommandsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static BoardCommands NewCommands(InMemoryStore store) =>
        new(store, new SequentialIdGenerator(), new FixedTimeProvider(Now));

    [Test]
    public async Task MoveAsync_StatusColumnToDoneWithOpenSubtasks_RequiresSubtaskChoice()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);
        var tasks = new TaskCommands(store, new SequentialIdGenerator(), new FixedTimeProvider(Now));
        var task = (await tasks.CaptureAsync(new("capture", "", "Parent", null))).Task;
        await new SubtaskCommands(store, new SequentialIdGenerator(), new FixedTimeProvider(Now))
            .AddAsync(new("sub", task.Id, "Open child", null));

        var error = await CatchAsync(() => NewCommands(store).MoveAsync(new(
            "move", task.Id, task.Version, BoardGrouping.Status, null, nameof(TaskItemStatus.Done), null, null)));
        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That(error.Fields.Single().FieldPath).IsEqualTo("openSubtaskChoice");
    }

    [Test]
    public async Task MoveAsync_StatusColumnToDone_CompletesTaskWithOneReceiptAndOneUndoEntry()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);
        var tasks = new TaskCommands(store, new SequentialIdGenerator(), new FixedTimeProvider(Now));
        var task = (await tasks.CaptureAsync(new("capture", "", "Card", null))).Task;

        var moved = await NewCommands(store).MoveAsync(new(
            "move", task.Id, task.Version, BoardGrouping.Status, null, nameof(TaskItemStatus.Done), null, null));

        await Assert.That(moved.Task.Status).IsEqualTo(TaskItemStatus.Done);
        await Assert.That(moved.Replayed).IsFalse();
        await Assert.That(store.Receipts.Count(row => row.OperationId == "move")).IsEqualTo(1);
        await Assert.That(store.Undo.Count(row => row.OperationId == "move")).IsEqualTo(1);
        await Assert.That(store.Undo.Single(row => row.OperationId == "move").Label).StartsWith("Move task on board");
    }

    [Test]
    public async Task MoveAsync_PriorityColumn_ChangesPriority()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);
        var tasks = new TaskCommands(store, new SequentialIdGenerator(), new FixedTimeProvider(Now));
        var task = (await tasks.CaptureAsync(new("capture", "", "Card", null))).Task;

        var moved = await NewCommands(store).MoveAsync(new(
            "move", task.Id, task.Version, BoardGrouping.Priority, null, nameof(TaskPriority.Urgent), null, null));

        await Assert.That(moved.Task.Priority).IsEqualTo(TaskPriority.Urgent);
        await Assert.That((await tasks.GetAsync(task.Id))!.Priority).IsEqualTo(TaskPriority.Urgent);
    }

    [Test]
    public async Task MoveAsync_SectionColumn_MovesTaskToSection()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);
        var now = Now;
        store.Containers.Add(new ContainerRecord { Id = "list-1", SpaceId = "space-1", Kind = ContainerKind.List, Name = "Work", Position = 1, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        store.Sections.Add(new SectionRecord { Id = "section-1", ContainerId = "list-1", Title = "Doing", Position = 0, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now });
        var tasks = new TaskCommands(store, new SequentialIdGenerator(), new FixedTimeProvider(Now));
        var task = (await tasks.CaptureAsync(new("capture", "list-1", "Card", null))).Task;

        var moved = await NewCommands(store).MoveAsync(new(
            "move", task.Id, task.Version, BoardGrouping.Section, null, "section-1", null, null));

        await Assert.That(moved.Task.SectionId).IsEqualTo("section-1");
    }

    [Test]
    public async Task MoveAsync_TagColumn_SwapsColumnTagsAsOneOperation()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);
        var now = Now;
        store.Tags.Add(NewTag(store, "tag-a", "Alpha", null));
        store.Tags.Add(NewTag(store, "tag-b", "Beta", null));
        var tasks = new TaskCommands(store, new SequentialIdGenerator(), new FixedTimeProvider(Now));
        var task = (await tasks.CaptureAsync(new("capture", "", "Card", null))).Task;
        store.TaskTags.Add((task.Id, "tag-a"));

        var moved = await NewCommands(store).MoveAsync(new(
            "move", task.Id, task.Version, BoardGrouping.TagGroup, "tag-a", "tag-b", null, null));

        await Assert.That(moved.Task.Id).IsEqualTo(task.Id);
        await Assert.That(store.TaskTags.Contains((task.Id, "tag-a"))).IsFalse();
        await Assert.That(store.TaskTags.Contains((task.Id, "tag-b"))).IsTrue();
        await Assert.That(store.Undo.Count(row => row.OperationId == "move")).IsEqualTo(1);
    }

    [Test]
    public async Task MoveAsync_ReplayedOperationId_ReturnsStoredResultWithoutWriting()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);
        var tasks = new TaskCommands(store, new SequentialIdGenerator(), new FixedTimeProvider(Now));
        var task = (await tasks.CaptureAsync(new("capture", "", "Card", null))).Task;
        var commands = NewCommands(store);
        var command = new MoveBoardCardCommand("move", task.Id, task.Version, BoardGrouping.Priority, null, nameof(TaskPriority.High), null, null);

        var first = await commands.MoveAsync(command);
        var second = await commands.MoveAsync(command);

        await Assert.That(second.Replayed).IsTrue();
        await Assert.That(second.Task.Priority).IsEqualTo(first.Task.Priority);
        await Assert.That(store.Undo.Count(row => row.OperationId == "move")).IsEqualTo(1);
    }

    [Test]
    public async Task MoveAsync_SameOperationIdDifferentPayload_Conflicts()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);
        var tasks = new TaskCommands(store, new SequentialIdGenerator(), new FixedTimeProvider(Now));
        var task = (await tasks.CaptureAsync(new("capture", "", "Card", null))).Task;
        var commands = NewCommands(store);
        await commands.MoveAsync(new("move", task.Id, task.Version, BoardGrouping.Priority, null, nameof(TaskPriority.High), null, null));

        var error = await CatchAsync(() => commands.MoveAsync(new(
            "move", task.Id, task.Version, BoardGrouping.Priority, null, nameof(TaskPriority.Low), null, null)));

        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.OperationConflict);
    }

    [Test]
    public async Task MoveAsync_InvalidAnchors_AreRefused()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);
        var tasks = new TaskCommands(store, new SequentialIdGenerator(), new FixedTimeProvider(Now));
        var task = (await tasks.CaptureAsync(new("capture", "", "Card", null))).Task;
        var other = (await tasks.CaptureAsync(new("capture-2", "", "Other", null))).Task;
        var commands = NewCommands(store);
        var status = nameof(TaskItemStatus.NotStarted);

        var both = await CatchAsync(() => commands.MoveAsync(new("both", task.Id, task.Version, BoardGrouping.Status, status, status, other.Id, other.Id)));
        var self = await CatchAsync(() => commands.MoveAsync(new("self", task.Id, task.Version, BoardGrouping.Status, status, status, task.Id, null)));
        var missing = await CatchAsync(() => commands.MoveAsync(new("missing", task.Id, task.Version, BoardGrouping.Status, status, status, "no-such-task", null)));

        await Assert.That(both!.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That(self!.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That(missing!.Kind).IsEqualTo(AppErrorKind.NotFound);
    }

    [Test]
    public async Task UpdateSettingsAsync_NewTarget_CreatesDeterministicRow()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);

        var result = await NewCommands(store).UpdateSettingsAsync(new(
            "settings", BoardTargetKind.Container, "list-1", 0, BoardGrouping.Priority, BoardSwimlane.Category, null, null,
            "{\"Low\": 3}", "[]"));

        var expected = StorageFormats.Id(NameBasedId.Create(NameBasedId.Names.BoardSetting("1", "list-1")));
        await Assert.That(result.Settings.Id).IsEqualTo(expected);
        await Assert.That(result.Settings.WipLimitsJson).IsEqualTo("{\"Low\":3}");
        await Assert.That((await NewCommands(store).GetSettingsAsync(BoardTargetKind.Container, "list-1")).GroupBy)
            .IsEqualTo(BoardGrouping.Priority);
    }

    [Test]
    public async Task UpdateSettingsAsync_StaleVersion_Conflicts()
    {
        var store = new InMemoryStore();
        TaskCommandsTests.Seed(store);
        var commands = NewCommands(store);
        var created = await commands.UpdateSettingsAsync(new(
            "one", BoardTargetKind.Container, "list-1", 0, BoardGrouping.Status, BoardSwimlane.None, null, null, "{}", "[]"));

        var error = await CatchAsync(() => commands.UpdateSettingsAsync(new(
            "two", BoardTargetKind.Container, "list-1", created.Settings.Version - 1, BoardGrouping.Status, BoardSwimlane.None, null, null, "{}", "[]")));

        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.VersionConflict);
    }

    [Test]
    public async Task NormalizeWipLimits_RejectsInvalidValues()
    {
        await Assert.That(await CatchAsync(() => Task.FromResult(BoardCommands.NormalizeWipLimits("{\"Low\": 0}")))).IsNotNull();
        await Assert.That(await CatchAsync(() => Task.FromResult(BoardCommands.NormalizeWipLimits("{\"Low\": \"three\"}")))).IsNotNull();
        await Assert.That(await CatchAsync(() => Task.FromResult(BoardCommands.NormalizeHiddenColumns("[42]")))).IsNotNull();
    }

    private static TagRecord NewTag(InMemoryStore store, string id, string name, string? parent) => new()
    {
        Id = id,
        OwnerProfileId = "profile-1",
        DisplayName = name,
        NormalizedName = name.ToLowerInvariant(),
        ParentTagId = parent,
        Position = 0,
        Version = 1,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now,
    };

    private static async Task<AppException?> CatchAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (AppException caught)
        {
            return caught;
        }
    }
}
