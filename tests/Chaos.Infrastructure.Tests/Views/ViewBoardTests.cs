using Chaos.Application.Abstractions;
using Chaos.Application.Boards;
using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Application.Views;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests.Views;

/// <summary>The board layout of a custom view against real SQLite (PLAN2 P3): cards are the view's tasks, settings belong to the view, and a drop changes the column.</summary>
public sealed class ViewBoardTests
{
    private const string OpenDefinition = "{\"v\":1,\"match\":\"all\",\"items\":[{\"field\":\"status\",\"op\":\"notIn\",\"value\":[\"Cancelled\"]}]}";

    private sealed class World(DataHost host)
    {
        public SavedViewCommands Views { get; } = new(host.Store, host.Ids, host.Clock);
        public BoardCommands Boards { get; } = new(host.Store, host.Ids, host.Clock);
        public ViewBoardQueries Queries { get; } = new(host.Store, new TaskQueries(host.Store, host.Clock));
        public ContainerCommands Containers { get; } = new(host.Store, host.Ids, host.Clock);

        public async Task<SavedViewRecord> ViewAsync(string definition = OpenDefinition) =>
            (await Views.SaveAsync(new(DataHost.Op(), null, 0, "Board view", "list", null, definition, "{\"v\":1,\"kind\":\"board\"}", true, false))).View;
    }

    [Test]
    public async Task Get_ShowsTheViewsTasksAcrossListsInStatusColumns()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var list = (await w.Containers.CreateAsync(new(DataHost.Op(), "Errands", ContainerKind.List, null))).Container;
        var inbox = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "In inbox", null))).Task;
        var errand = (await host.Tasks.CaptureAsync(new(DataHost.Op(), list.Id, "Errand", null))).Task;
        var started = (await host.Tasks.CaptureAsync(new(DataHost.Op(), list.Id, "Started", null))).Task;
        await host.Tasks.SetStatusAsync(new(DataHost.Op(), started.Id, started.Version, TaskItemStatus.InProgress, null));
        var cancelled = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Cancelled one", null))).Task;
        await host.Tasks.SetStatusAsync(new(DataHost.Op(), cancelled.Id, cancelled.Version, TaskItemStatus.Cancelled, null));
        var view = await w.ViewAsync();

        var board = await w.Queries.GetAsync(view.Id);

        await Assert.That(board.Cards.Count).IsEqualTo(3);
        await Assert.That(board.Cards.Single(card => card.Task.Id == started.Id).ColumnKey).IsEqualTo(nameof(TaskItemStatus.InProgress));
        await Assert.That(board.Cards.Single(card => card.Task.Id == inbox.Id).ColumnKey).IsEqualTo(nameof(TaskItemStatus.NotStarted));
        await Assert.That(board.Cards.Any(card => card.Task.Id == cancelled.Id)).IsFalse();
        await Assert.That(board.Settings.Version).IsEqualTo(0L);
        await Assert.That(board.Settings.TargetKind).IsEqualTo(BoardTargetKind.View);
        await Assert.That(board.TotalCount).IsEqualTo(3);
        await Assert.That(errand.Id).IsNotEmpty();

        var withCancelled = await w.Queries.GetAsync(view.Id, showCancelled: true);
        await Assert.That(withCancelled.Columns.Any(column => column.Key == nameof(TaskItemStatus.Cancelled))).IsTrue();
    }

    [Test]
    public async Task Get_OnlyCardsTheViewMatchesAppear_AndWorkFocusFilters()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var work = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Work task", null))).Task;
        await host.Tasks.UpdateDetailsAsync(new(DataHost.Op(), work.Id, work.Version, "Work task", null, null, null, null, null, null, null, TaskPriority.High, null, CapacityCategory.Work));
        await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Home task", null));
        var highOnly = await w.ViewAsync("{\"v\":1,\"match\":\"all\",\"items\":[{\"field\":\"priority\",\"op\":\"atLeast\",\"value\":\"High\"}]}");

        var board = await w.Queries.GetAsync(highOnly.Id);
        var personalFocus = await w.Queries.GetAsync(highOnly.Id, CapacityCategory.Personal);

        await Assert.That(board.Cards.Select(card => card.Task.Title).Single()).IsEqualTo("Work task");
        await Assert.That(personalFocus.Cards.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Settings_BelongToTheView_PersistAcrossRestart_AndSectionGroupingIsRefused()
    {
        var root = TestPaths.NewDataRoot();
        string viewId;
        using (var host = await DataHost.StartAsync(root))
        {
            var w = new World(host);
            viewId = (await w.ViewAsync()).Id;
            var saved = await w.Boards.UpdateSettingsAsync(new(DataHost.Op(), BoardTargetKind.View, viewId, 0, BoardGrouping.Priority, BoardSwimlane.Container, null, null, "{\"High\": 2}", "[]"));
            await Assert.That(saved.Settings.Version).IsEqualTo(1L);

            var refused = await Assert.That(async () => await w.Boards.UpdateSettingsAsync(
                new(DataHost.Op(), BoardTargetKind.View, viewId, 1, BoardGrouping.Section, BoardSwimlane.None, null, null, "{}", "[]"))).Throws<AppException>();
            await Assert.That(refused!.Kind).IsEqualTo(AppErrorKind.Validation);
        }

        using (var host = await DataHost.StartAsync(root))
        {
            var board = await new World(host).Queries.GetAsync(viewId);
            await Assert.That(board.Settings.GroupBy).IsEqualTo(BoardGrouping.Priority);
            await Assert.That(board.Settings.WipLimitsJson).IsEqualTo("{\"High\":2}");
            await Assert.That(board.Columns.First().Key).IsEqualTo(nameof(TaskPriority.Urgent));
        }
    }

    [Test]
    public async Task Lanes_ByContainer_HaveOneLanePerListAndEveryCardInItsLane()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var list = (await w.Containers.CreateAsync(new(DataHost.Op(), "Errands", ContainerKind.List, null))).Container;
        var inbox = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "In inbox", null))).Task;
        var errand = (await host.Tasks.CaptureAsync(new(DataHost.Op(), list.Id, "Errand", null))).Task;
        var view = await w.ViewAsync();
        await w.Boards.UpdateSettingsAsync(new(DataHost.Op(), BoardTargetKind.View, view.Id, 0, BoardGrouping.Status, BoardSwimlane.Container, null, null, "{}", "[]"));

        var board = await w.Queries.GetAsync(view.Id);

        await Assert.That(board.Lanes.Count).IsEqualTo(2);
        await Assert.That(board.Cards.Single(card => card.Task.Id == errand.Id).LaneKey).IsEqualTo(list.Id);
        await Assert.That(board.Cards.Single(card => card.Task.Id == inbox.Id).LaneKey).IsEqualTo(inbox.ContainerId);
        foreach (var card in board.Cards)
        {
            await Assert.That(board.Lanes.Any(lane => lane.Key == card.LaneKey)).IsTrue();
        }
    }

    [Test]
    public async Task Move_DropIntoDone_CompletesTheTaskAndTheNextReadShowsItThere()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var task = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "Finish me", null))).Task;
        var view = await w.ViewAsync();

        await w.Boards.MoveAsync(new(DataHost.Op(), task.Id, task.Version, BoardGrouping.Status, nameof(TaskItemStatus.NotStarted), nameof(TaskItemStatus.Done), null, null));
        var board = await w.Queries.GetAsync(view.Id);

        await Assert.That(board.Cards.Single().ColumnKey).IsEqualTo(nameof(TaskItemStatus.Done));
        await Assert.That(board.Cards.Single().Task.Status).IsEqualTo(TaskItemStatus.Done);
    }

    [Test]
    public async Task Get_CardsCarrySubtaskProgress()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var task = (await host.Tasks.CaptureAsync(new(DataHost.Op(), string.Empty, "With steps", null))).Task;
        var subtasks = new SubtaskCommands(host.Store, host.Ids, host.Clock);
        await subtasks.AddAsync(new(DataHost.Op(), task.Id, "One", null));
        await subtasks.AddAsync(new(DataHost.Op(), task.Id, "Two", null));
        var view = await w.ViewAsync();

        var board = await w.Queries.GetAsync(view.Id);

        await Assert.That(board.Cards.Single().SubtasksTotal).IsEqualTo(2);
        await Assert.That(board.Cards.Single().SubtasksDone).IsEqualTo(0);
    }

    [Test]
    public async Task Get_DeletedView_IsNotFound()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var view = await w.ViewAsync();
        await w.Views.DeleteAsync(DataHost.Op(), view.Id, view.Version);

        var error = await Assert.That(async () => await w.Queries.GetAsync(view.Id)).Throws<AppException>();

        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.NotFound);
    }
}
