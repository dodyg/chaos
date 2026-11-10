using Chaos.Application.Queries;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>Tag and query RPC against real storage: tag by path, open the tag group, search, counts and errors as data.</summary>
public sealed class TagQueryRpcTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private static TaskQueryRequest Request(string view, string? tagId = null, string? text = null, string? cursor = null) =>
        new(view, null, tagId, text, null, null, null, null, null, null, null, null, false, false, null, null, cursor);

    [Test]
    public async Task TagByPath_ThenTagGroupSearchAndCounts_RoundTripAcrossRestart()
    {
        var root = TestPaths.NewDataRoot();
        string taskId;
        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new SequentialIdGenerator(), new FixedTimeProvider(Now), CancellationToken.None))
        {
            var ids = new SequentialIdGenerator();
            var clock = new FixedTimeProvider(Now);
            var taskRpc = new TaskRpcService(new TaskCommands(storage.Store, ids, clock));
            var tagRpc = new TagRpcService(new TagCommands(storage.Store, ids, clock));
            var created = await taskRpc.CreateAsync(new("task", "Fix the gate", "Hinge is loose", null), CancellationToken.None);
            taskId = created.Task!.Id;

            var linked = await tagRpc.LinkAsync(new("link", "Task", taskId, null, "home/garden"), CancellationToken.None);
            var invalid = await tagRpc.LinkAsync(new("bad", "Diary", taskId, null, "x"), CancellationToken.None);

            await Assert.That(linked.Ok).IsTrue();
            await Assert.That(linked.TagIds.Count).IsEqualTo(1);
            await Assert.That(invalid.Ok).IsFalse();
            await Assert.That(invalid.Error!.Kind).IsEqualTo("validation");
        }

        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new SequentialIdGenerator(), new FixedTimeProvider(Now), CancellationToken.None))
        {
            var tagRpc = new TagRpcService(new TagCommands(storage.Store, new SequentialIdGenerator(), new FixedTimeProvider(Now)));
            var queryRpc = new QueryRpcService(new TaskQueries(storage.Store, new FixedTimeProvider(Now)));
            var tags = await tagRpc.ListAsync(new(), CancellationToken.None);
            var home = tags.Tags.Single(tag => tag.Path == "home");

            var group = await queryRpc.RunAsync(Request("Tag", home.Id), CancellationToken.None);
            var search = await queryRpc.RunAsync(Request("Search", text: "hinge"), CancellationToken.None);
            var counts = await queryRpc.CountsAsync(new(), CancellationToken.None);
            var badView = await queryRpc.RunAsync(Request("Kanban"), CancellationToken.None);

            await Assert.That(string.Join(",", tags.Tags.Select(tag => $"{tag.Path}:{tag.Depth}"))).IsEqualTo("home:0,home/garden:1");
            await Assert.That(group.Rows.Single().Task.Id).IsEqualTo(taskId);
            await Assert.That(group.Rows.Single().TagIds.Single()).IsEqualTo(tags.Tags.Single(tag => tag.Path == "home/garden").Id);
            await Assert.That(search.Rows.Single().Task.Title).IsEqualTo("Fix the gate");
            await Assert.That(search.Rows.Single().Matches.Single()).IsEqualTo("Task");
            await Assert.That(counts.Tags.Single(entry => entry.TagId == home.Id).Count).IsEqualTo(1);
            await Assert.That(counts.Views.Single(entry => entry.View == "AssignedToMe").Count).IsEqualTo(1);
            await Assert.That(badView.Ok).IsFalse();
            await Assert.That(badView.Error!.Kind).IsEqualTo("validation");
        }
    }
}
