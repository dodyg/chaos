using Chaos.Application.History;
using Chaos.Application.Tasks;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>
/// PLAN §15 WP4 exit over RPC: a mistaken deletion is recovered after a restart, the diary
/// renders from stored Markdown, and a stale undo is refused as data rather than thrown.
/// </summary>
public sealed class HistoryUndoRpcTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed record Services(TaskRpcService Tasks, SubtaskRpcService Subtasks, UndoRpcService Undo, DiaryRpcService Diary, HistoryRpcService History, MarkdownRpcService Markdown);

    private static Services Wire(ChaosStorageHandle storage)
    {
        var ids = new GuidIds();
        var clock = new FixedTimeProvider(Now);
        var tasks = new TaskCommands(storage.Store, ids, clock);
        return new Services(
            new TaskRpcService(tasks),
            new SubtaskRpcService(tasks, new SubtaskCommands(storage.Store, ids, clock)),
            new UndoRpcService(new UndoCommands(storage.Store, ids, clock)),
            new DiaryRpcService(new DiaryCommands(storage.Store, ids, clock)),
            new HistoryRpcService(new TaskHistoryCommands(storage.Store, tasks)),
            new MarkdownRpcService(new ChecklistCommands(storage.Store, ids, clock), tasks));
    }

    private sealed class GuidIds : Chaos.Application.Abstractions.IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private static string Op() => Guid.NewGuid().ToString();

    [Test]
    public async Task DeleteRestartUndo_RecoversTaskDiaryAndChecklistSubtasks()
    {
        var root = TestPaths.NewDataRoot();
        string taskId;
        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new GuidIds(), new FixedTimeProvider(Now), CancellationToken.None))
        {
            var rpc = Wire(storage);
            var created = await rpc.Tasks.CreateAsync(new(Op(), "Plan the trip", "- [ ] Book train\n- [x] Pack bag", null), CancellationToken.None);
            taskId = created.Task!.Id;
            var converted = await rpc.Markdown.ConvertChecklistAsync(new(Op(), taskId, [new("Book train#0", "Book train", false), new("Pack bag#0", "Pack bag", true)]), CancellationToken.None);
            await Assert.That(converted.Ok).IsTrue();
            await Assert.That(converted.Created.Count).IsEqualTo(2);
            var diary = await rpc.Diary.AddAsync(new(Op(), taskId, "Train is **cheaper** on Tuesdays", null), CancellationToken.None);
            await Assert.That(diary.Ok).IsTrue();

            var task = (await rpc.Tasks.GetAsync(new(taskId), CancellationToken.None)).Task!;
            var deleted = await rpc.Tasks.DeleteAsync(new(Op(), taskId, task.Version), CancellationToken.None);
            await Assert.That(deleted.Ok).IsTrue();
        }

        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new GuidIds(), new FixedTimeProvider(Now.AddMinutes(5)), CancellationToken.None))
        {
            var rpc = Wire(storage);
            var undoable = await rpc.Undo.ListAsync(new(5), CancellationToken.None);
            await Assert.That(undoable.Entries[0].Label).IsEqualTo("Delete task “Plan the trip”");

            var undone = await rpc.Undo.UndoAsync(new(Op(), undoable.Entries[0].OperationId), CancellationToken.None);
            await Assert.That(undone.Ok).IsTrue();
            await Assert.That((await rpc.Tasks.GetAsync(new(taskId), CancellationToken.None)).Task).IsNotNull();
            var subtasks = await rpc.Subtasks.ListAsync(new(taskId), CancellationToken.None);
            await Assert.That(string.Join(",", subtasks.Subtasks.Select(x => $"{x.Title}:{x.Status}"))).IsEqualTo("Book train:NotStarted,Pack bag:Done");
            var timeline = await rpc.Diary.TimelineAsync(new(taskId), CancellationToken.None);
            await Assert.That(timeline.Items.Single(x => x.Diary is not null).Diary!.Markdown).IsEqualTo("Train is **cheaper** on Tuesdays");

            // The same undo again is refused as data.
            var again = await rpc.Undo.UndoAsync(new(Op(), undoable.Entries[0].OperationId), CancellationToken.None);
            await Assert.That(again.Ok).IsFalse();
            await Assert.That(again.Error!.Kind).IsEqualTo("operationConflict");

            var history = await rpc.History.ListAsync(new(taskId), CancellationToken.None);
            var asOfCreate = await rpc.History.AsOfAsync(new(taskId, null, history.Entries[0].Id), CancellationToken.None);
            await Assert.That(asOfCreate.Existed).IsTrue();
            await Assert.That(asOfCreate.Snapshot!.Subtasks).IsEmpty();
            var beforeCreate = await rpc.History.AsOfAsync(new(taskId, "2026-11-01T00:00:00.0000000Z", null), CancellationToken.None);
            await Assert.That(beforeCreate.Existed).IsFalse();
            var links = await rpc.Markdown.ResolveLinksAsync(new([taskId, Guid.NewGuid().ToString()]), CancellationToken.None);
            await Assert.That(links.Targets.Single().Title).IsEqualTo("Plan the trip");
        }
    }
}

public sealed class OpenExternalRpcTests
{
    [Test]
    [Arguments("javascript:alert(1)")]
    [Arguments("file:///C:/Windows/System32/calc.exe")]
    [Arguments("chaos:task/123")]
    [Arguments("relative/path")]
    public async Task OpenExternal_UnsafeOrRelativeUrl_IsRefusedWithoutOpening(string url)
    {
        var opened = new List<Uri>();
        var rpc = new MarkdownRpcService(null!, null!, uri => { opened.Add(uri); return true; });
        var response = await rpc.OpenExternalAsync(new(url), CancellationToken.None);
        await Assert.That(response.Ok).IsFalse();
        await Assert.That(response.Error!.Kind).IsEqualTo("validation");
        await Assert.That(opened).IsEmpty();
    }

    [Test]
    public async Task OpenExternal_HttpsUrl_GoesToTheHostOpener()
    {
        var opened = new List<Uri>();
        var rpc = new MarkdownRpcService(null!, null!, uri => { opened.Add(uri); return true; });
        var response = await rpc.OpenExternalAsync(new("https://example.org/a?b=1"), CancellationToken.None);
        await Assert.That(response.Ok).IsTrue();
        await Assert.That(opened.Single().AbsoluteUri).IsEqualTo("https://example.org/a?b=1");
    }
}
