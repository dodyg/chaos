using Chaos.Application.Abstractions;
using Chaos.Application.Boards;
using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Settings;
using Chaos.Application.Spaces;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests.Boards;

/// <summary>
/// Board behaviour against real SQLite (PLAN2 P2): a card move is one operation that survives
/// restart, undo restores the card, settings round-trip with their deterministic id, and the
/// board read assigns columns, lanes, tags, subtask progress and the GitHub mark.
/// </summary>
public sealed class BoardTests
{
    [Test]
    public async Task DeleteState_MovesTasksToBaseAndUndoRestoresBoth()
    {
        using var host = await DataHost.StartAsync();
        var task = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Review", null))).Task;
        var states = new SpaceStateCommands(host.Store, host.Ids, host.Clock);
        var state = (await states.CreateAsync(new(DataHost.Op(), task.SpaceId, "In review", TaskItemStatus.NeedsReview))).State;
        await host.Tasks.SetCustomStateAsync(new(DataHost.Op(), task.Id, task.Version, state.Id));
        var operation = DataHost.Op();
        await states.DeleteAsync(new(operation, state.Id, state.Version));
        var moved = (await host.Tasks.GetAsync(task.Id))!;
        await Assert.That(moved.CustomStateId).IsNull();
        await Assert.That(moved.Status).IsEqualTo(TaskItemStatus.NeedsReview);
        await new UndoCommands(host.Store, host.Ids, host.Clock).UndoAsync(new(DataHost.Op(), operation));
        await Assert.That((await host.Tasks.GetAsync(task.Id))!.CustomStateId).IsEqualTo(state.Id);
        await Assert.That((await states.ListAsync(task.SpaceId)).Select(row => row.Id)).Contains(state.Id);
    }

    [Test]
    public async Task CustomState_ColumnFollowsBaseAndCardMovesIntoIt()
    {
        using var host = await DataHost.StartAsync();
        var task = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Review", null))).Task;
        var state = (await new SpaceStateCommands(host.Store, host.Ids, host.Clock)
            .CreateAsync(new(DataHost.Op(), task.SpaceId, "In review", TaskItemStatus.NeedsReview))).State;
        var board = await new BoardQueries(host.Store).GetAsync(task.ContainerId);
        var keys = board.Columns.Select(column => column.Key).ToList();
        await Assert.That(keys.IndexOf("state:" + state.Id)).IsEqualTo(keys.IndexOf(nameof(TaskItemStatus.NeedsReview)) + 1);
        var moved = await new BoardCommands(host.Store, host.Ids, host.Clock).MoveAsync(new(DataHost.Op(), task.Id,
            task.Version, BoardGrouping.Status, nameof(TaskItemStatus.NotStarted), "state:" + state.Id, null, null));
        await Assert.That(moved.Task.Status).IsEqualTo(TaskItemStatus.NeedsReview);
        var updated = await new BoardQueries(host.Store).GetAsync(task.ContainerId);
        await Assert.That(updated.Cards.Single(card => card.Task.Id == task.Id).ColumnKey).IsEqualTo("state:" + state.Id);
    }

    [Test]
    public async Task MoveAsync_StatusColumnToDone_PersistsAcrossRestartAndUndoRestores()
    {
        var root = TestPaths.NewDataRoot();
        string taskId;
        string operationId;
        using (var host = await DataHost.StartAsync(root))
        {
            var task = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Board card", null))).Task;
            taskId = task.Id;
            operationId = DataHost.Op();
            var moved = await new BoardCommands(host.Store, host.Ids, host.Clock).MoveAsync(new(
                operationId, task.Id, task.Version, BoardGrouping.Status, null, nameof(TaskItemStatus.Done), null, null));
            await Assert.That(moved.Task.Status).IsEqualTo(TaskItemStatus.Done);
        }

        using (var host = await DataHost.StartAsync(root))
        {
            var stored = (await host.Tasks.GetAsync(taskId))!;
            await Assert.That(stored.Status).IsEqualTo(TaskItemStatus.Done);

            var undo = new UndoCommands(host.Store, host.Ids, host.Clock);
            await undo.UndoAsync(new(DataHost.Op(), operationId));
            await Assert.That((await host.Tasks.GetAsync(taskId))!.Status).IsEqualTo(TaskItemStatus.NotStarted);
        }
    }

    [Test]
    public async Task MoveAsync_BeforeAnchor_LandsJustBeforeIt()
    {
        using var host = await DataHost.StartAsync();
        var boards = new BoardCommands(host.Store, host.Ids, host.Clock);
        var first = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "First", null))).Task;
        var second = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Second", null))).Task;
        var third = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Third", null))).Task;

        // Drop "Third" above "First" in the Not started column.
        var status = nameof(TaskItemStatus.NotStarted);
        var moved = await boards.MoveAsync(new(
            DataHost.Op(), third.Id, third.Version, BoardGrouping.Status, status, status, first.Id, null));

        var order = string.Join(",", (await host.Tasks.ListAsync(first.ContainerId)).Select(task => task.Id));
        await Assert.That(order).IsEqualTo(string.Join(",", third.Id, first.Id, second.Id));
        await Assert.That(moved.Task.Id).IsEqualTo(third.Id);
    }

    [Test]
    public async Task MoveAsync_AfterAnchorInAnotherColumn_ChangesColumnAndLandsJustAfterIt()
    {
        using var host = await DataHost.StartAsync();
        var boards = new BoardCommands(host.Store, host.Ids, host.Clock);
        var doing = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Doing", null))).Task;
        var between = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Between", null))).Task;
        var card = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Card", null))).Task;
        doing = (await host.Tasks.SetStatusAsync(new(DataHost.Op(), doing.Id, doing.Version, TaskItemStatus.InProgress, null))).Task;

        // Drop "Card" below "Doing", the last card of In progress: "Between" (another column)
        // must not shift where it lands.
        var moved = await boards.MoveAsync(new(
            DataHost.Op(), card.Id, card.Version, BoardGrouping.Status,
            nameof(TaskItemStatus.NotStarted), nameof(TaskItemStatus.InProgress), null, doing.Id));

        await Assert.That(moved.Task.Status).IsEqualTo(TaskItemStatus.InProgress);
        var order = string.Join(",", (await host.Tasks.ListAsync(doing.ContainerId)).Select(task => task.Id));
        await Assert.That(order).IsEqualTo(string.Join(",", doing.Id, card.Id, between.Id));
    }

    [Test]
    public async Task MoveAsync_DropOnItsOwnPlace_WritesNothing()
    {
        using var host = await DataHost.StartAsync();
        var boards = new BoardCommands(host.Store, host.Ids, host.Clock);
        var first = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "First", null))).Task;
        var second = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Second", null))).Task;

        var status = nameof(TaskItemStatus.NotStarted);
        var moved = await boards.MoveAsync(new(
            DataHost.Op(), second.Id, second.Version, BoardGrouping.Status, status, status, null, first.Id));

        await Assert.That(moved.Task.Version).IsEqualTo(second.Version);
    }

    [Test]
    public async Task UpdateSettingsAsync_RoundTripsAndUsesDeterministicId()
    {
        using var host = await DataHost.StartAsync();
        var containers = new ContainerCommands(host.Store, host.Ids, host.Clock);
        var list = (await containers.CreateAsync(new CreateContainerCommand(DataHost.Op(), "Board list", ContainerKind.List, null))).Container;

        var boards = new BoardCommands(host.Store, host.Ids, host.Clock);
        var created = await boards.UpdateSettingsAsync(new(
            DataHost.Op(), BoardTargetKind.Container, list.Id, 0, BoardGrouping.Section, BoardSwimlane.Category, null, null,
            "{\"Done\": 3}", "[\"No section\"]"));
        await Assert.That(created.Settings.Id).IsEqualTo(StorageFormats.Id(
            Chaos.Sync.NameBasedId.Create(Chaos.Sync.NameBasedId.Names.BoardSetting("1", list.Id))));

        var updated = await boards.UpdateSettingsAsync(new(
            DataHost.Op(), BoardTargetKind.Container, list.Id, created.Settings.Version, BoardGrouping.Priority,
            BoardSwimlane.None, null, null, "{}", "[]"));
        await Assert.That(updated.Settings.GroupBy).IsEqualTo(BoardGrouping.Priority);

        var read = await boards.GetSettingsAsync(BoardTargetKind.Container, list.Id);
        await Assert.That(read.GroupBy).IsEqualTo(BoardGrouping.Priority);
        await Assert.That(read.HiddenColumnsJson).IsEqualTo("[]");
    }

    [Test]
    public async Task GetAsync_AssignsColumnsLanesTagsAndProgress()
    {
        using var host = await DataHost.StartAsync();
        var containers = new ContainerCommands(host.Store, host.Ids, host.Clock);
        var project = (await containers.CreateAsync(new CreateContainerCommand(DataHost.Op(), "Board project", ContainerKind.Project, null))).Container;
        var section = (await containers.CreateSectionAsync(new CreateSectionCommand(DataHost.Op(), project.Id, "Doing"))).Section;

        var capture = new Chaos.Application.Capture.CaptureCommands(host.Store, host.Ids, host.Clock);
        var created = (await capture.CreateAsync(new Chaos.Application.Capture.CreateCapturedTasksCommand(DataHost.Op(),
        [
            new() { Title = "Plain", ContainerId = project.Id },
            new() { Title = "In section", ContainerId = project.Id },
            new() { Title = "Urgent lane", ContainerId = project.Id, Priority = TaskPriority.Urgent, TagPaths = ["board"] },
        ]))).Tasks;
        var plain = created[0];
        var inSection = created[1];
        var urgent = created[2];
        await host.Tasks.MoveAsync(new(DataHost.Op(), inSection.Id, inSection.Version, project.Id, section.Id));
        await host.Tasks.SetStatusAsync(new(DataHost.Op(), urgent.Id, urgent.Version, TaskItemStatus.Done, null));

        var boards = new BoardCommands(host.Store, host.Ids, host.Clock);
        await boards.UpdateSettingsAsync(new(
            DataHost.Op(), BoardTargetKind.Container, project.Id, 0, BoardGrouping.Section, BoardSwimlane.Priority, null, null, "{}", "[]"));

        var board = await new BoardQueries(host.Store).GetAsync(project.Id);
        await Assert.That(board.Columns.Select(column => column.Title)).Contains("Doing");
        await Assert.That(board.Columns[^1].Title).IsEqualTo("No section");
        await Assert.That(board.Lanes.Select(lane => lane.Key)).IsEquivalentTo(
            [nameof(TaskPriority.Urgent), nameof(TaskPriority.High), nameof(TaskPriority.Normal), nameof(TaskPriority.Low), nameof(TaskPriority.None)]);

        var plainCard = board.Cards.Single(card => card.Task.Id == plain.Id);
        var sectionCard = board.Cards.Single(card => card.Task.Id == inSection.Id);
        var urgentCard = board.Cards.Single(card => card.Task.Id == urgent.Id);
        await Assert.That(plainCard.ColumnKey).IsEqualTo(string.Empty);
        await Assert.That(sectionCard.ColumnKey).IsEqualTo(section.Id);
        await Assert.That(urgentCard.ColumnKey).IsEqualTo(string.Empty);
        await Assert.That(urgentCard.LaneKey).IsEqualTo(nameof(TaskPriority.Urgent));
        await Assert.That(urgentCard.TagIds.Count).IsEqualTo(1);
        await Assert.That(board.TotalCount).IsEqualTo(3);
        await Assert.That(board.Truncated).IsFalse();
    }
}
