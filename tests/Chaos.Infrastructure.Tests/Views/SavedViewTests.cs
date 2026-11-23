using Chaos.Application.Abstractions;
using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Application.Spaces;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Application.Views;
using Chaos.Domain.Tasks;
using Chaos.Domain.Views;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Tests.Views;

/// <summary>
/// Custom views against real SQLite (PLAN2 P3): definitions compile to the same candidate query as
/// the built-in views, so counts equal rows and the overlapping built-ins return exactly the same
/// tasks; saved views persist, duplicate, delete and restore, reorder and pin.
/// </summary>
public sealed class SavedViewTests
{
    [Test]
    public async Task CustomStateFilter_ReturnsOnlyMatchingTasks()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var first = await w.TaskAsync("In review");
        await w.TaskAsync("Other");
        var state = (await new SpaceStateCommands(host.Store, host.Ids, host.Clock)
            .CreateAsync(new(DataHost.Op(), first.SpaceId, "Review", TaskItemStatus.NeedsReview))).State;
        await host.Tasks.SetCustomStateAsync(new(DataHost.Op(), first.Id, first.Version, state.Id));
        var definition = Def($"{{\"field\":\"customState\",\"op\":\"is\",\"value\":\"{state.Id}\"}}");
        await Assert.That(await w.IdsAsync(definition)).IsEquivalentTo(new[] { first.Id });
    }

    [Test]
    public async Task Share_CopiesIntoSpace_ConstrainsRows_AndKeepsAlertsPersonal()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var personal = await w.TaskAsync("Personal task");
        var shared = await w.TaskAsync("Shared task");
        await host.Store.ExecuteAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc)
                    SELECT 'team',Id,2,'Team','2026-11-10T09:00:00Z','2026-11-10T09:00:00Z' FROM Profiles LIMIT 1;
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc)
                    SELECT 'member','team',Id,1,'Me',2,'2026-11-10T09:00:00Z' FROM Profiles LIMIT 1;
                INSERT INTO Containers (Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc)
                    VALUES ('team-list','team',1,'Team','team',0,'2026-11-10T09:00:00Z','2026-11-10T09:00:00Z');
                UPDATE Tasks SET SpaceId='team',ContainerId='team-list' WHERE Id=$task;
                """;
            command.Parameters.AddWithValue("$task", shared.Id);
            await command.ExecuteNonQueryAsync(token);
            return true;
        });
        var source = (await w.Views.SaveAsync(NewView("Open", alerts: true))).View;
        var operation = DataHost.Op();
        var copy = (await w.Views.ShareAsync(operation, source.Id, "team")).View;
        await Assert.That((await w.Views.ShareAsync(operation, source.Id, "team")).View.Id).IsEqualTo(copy.Id);
        await Assert.That(copy.Id).IsNotEqualTo(source.Id);
        await Assert.That(copy.AlertsEnabled).IsFalse();
        await Assert.That((await w.Views.ListAsync()).Select(view => view.Id)).Contains(copy.Id);
        var run = await new SavedViewQueries(host.Store, w.Queries, host.Clock).RunAsync(copy.Id, null, 100, null);
        await Assert.That(run.Page.Rows.Select(row => row.Task.Id)).IsEquivalentTo([shared.Id]);
        await Assert.That(run.Page.Rows.Select(row => row.Task.Id)).DoesNotContain(personal.Id);
        var enabled = (await w.Views.SaveAsync(new(DataHost.Op(), copy.Id, copy.Version, copy.Name, copy.Icon, copy.Color,
            copy.DefinitionJson, copy.LayoutJson, copy.Pinned, true))).View;
        await Assert.That(enabled.AlertsEnabled).IsTrue();
        await host.Store.QueryAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "SELECT AlertsEnabled FROM SavedViews WHERE Id=$id";
            command.Parameters.AddWithValue("$id", copy.Id);
            await Assert.That(Convert.ToInt64(await command.ExecuteScalarAsync(token))).IsEqualTo(0L);
            return true;
        });
    }

    // The test clock is 2026-11-10 09:00 UTC; the profile uses the machine zone, which has the same date at that hour.
    private static readonly DateOnly Today = new(2026, 11, 10);

    private sealed class World(DataHost host)
    {
        public DataHost Host { get; } = host;
        public TaskQueries Queries { get; } = new(host.Store, host.Clock);
        public SavedViewCommands Views { get; } = new(host.Store, host.Ids, host.Clock);
        public ContainerCommands Containers { get; } = new(host.Store, host.Ids, host.Clock);
        public TagCommands Tags { get; } = new(host.Store, host.Ids, host.Clock);

        public async Task<TaskRecord> TaskAsync(
            string title, string? containerId = null, DateOnly? due = null, DateOnly? planned = null, TimeOnly? dueTime = null,
            TaskPriority priority = TaskPriority.Normal, int? estimate = null, CapacityCategory category = CapacityCategory.Personal,
            string? notes = null)
        {
            var task = (await Host.Tasks.CaptureAsync(new(DataHost.Op(), containerId ?? string.Empty, title, notes))).Task;
            return (await Host.Tasks.UpdateDetailsAsync(new(
                DataHost.Op(), task.Id, task.Version, title, notes, planned, null, planned is null ? null : "UTC",
                due, dueTime, due is null ? null : "UTC", priority, estimate, category))).Task;
        }

        public async Task<TaskRecord> StatusAsync(TaskRecord task, TaskItemStatus status) =>
            (await Host.Tasks.SetStatusAsync(new(DataHost.Op(), task.Id, task.Version, status, null))).Task;

        public async Task<IReadOnlyList<string>> IdsAsync(string definitionJson, ViewSort? sort = null)
        {
            var spec = new TaskQuerySpec { View = TaskView.Custom, Definition = ViewDefinition.Parse(definitionJson), Sort = sort };
            var page = await Queries.RunAsync(spec);
            var count = await Queries.CountAsync(spec);
            if (page.Rows.Count != count)
            {
                throw new InvalidOperationException($"Rows ({page.Rows.Count}) and count ({count}) disagree.");
            }

            return [.. page.Rows.Select(row => row.Task.Id)];
        }

        public async Task<IReadOnlyList<string>> BuiltInAsync(TaskQuerySpec spec) =>
            [.. (await Queries.RunAsync(spec)).Rows.Select(row => row.Task.Id)];
    }

    private static string Def(string items, string match = "all") => "{\"v\":1,\"match\":\"" + match + "\",\"items\":[" + items + "]}";

    private static async Task SameSetAsync(IEnumerable<string> actual, IEnumerable<string> expected) =>
        await Assert.That(string.Join(",", actual.Order(StringComparer.Ordinal))).IsEqualTo(string.Join(",", expected.Order(StringComparer.Ordinal)));

    // ---- Parity with the built-in views ----------------------------------------------------

    [Test]
    public async Task Run_OverdueDefinition_ReturnsExactlyTheBuiltInOverdueRows()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        await w.TaskAsync("Date overdue", due: Today.AddDays(-2));
        await w.TaskAsync("Due today", due: Today);
        await w.TaskAsync("Due tomorrow", due: Today.AddDays(1));
        await w.TaskAsync("Timed, passed", due: Today, dueTime: new TimeOnly(8, 0));
        await w.TaskAsync("Timed, ahead", due: Today, dueTime: new TimeOnly(18, 0));
        await w.TaskAsync("No date");
        var done = await w.TaskAsync("Done overdue", due: Today.AddDays(-5));
        await w.StatusAsync(done, TaskItemStatus.Done);

        var builtIn = await w.BuiltInAsync(new TaskQuerySpec { View = TaskView.Overdue });
        var custom = await w.IdsAsync(Def(
            "{\"field\":\"status\",\"op\":\"notIn\",\"value\":[\"Done\",\"Cancelled\"]},{\"field\":\"due\",\"op\":\"inRange\",\"value\":{\"relative\":\"overdue\"}}"));

        await Assert.That(builtIn.Count).IsEqualTo(2);
        await SameSetAsync(custom, builtIn);
    }

    [Test]
    public async Task Run_TodayDefinition_ReturnsExactlyTheBuiltInTodayRows()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        await w.TaskAsync("Planned today", planned: Today);
        await w.TaskAsync("Due today", due: Today);
        await w.TaskAsync("Planned tomorrow", planned: Today.AddDays(1));
        await w.TaskAsync("Neither");
        var done = await w.TaskAsync("Done today", due: Today);
        await w.StatusAsync(done, TaskItemStatus.Done);

        var builtIn = await w.BuiltInAsync(new TaskQuerySpec { View = TaskView.Today });
        var custom = await w.IdsAsync(Def(
            "{\"field\":\"status\",\"op\":\"notIn\",\"value\":[\"Done\",\"Cancelled\"]},"
            + "{\"match\":\"any\",\"items\":[{\"field\":\"planned\",\"op\":\"on\",\"value\":{\"relative\":\"today\"}},{\"field\":\"due\",\"op\":\"on\",\"value\":{\"relative\":\"today\"}}]}"));

        await Assert.That(builtIn.Count).IsEqualTo(2);
        await SameSetAsync(custom, builtIn);
    }

    [Test]
    public async Task Run_ContainerAndTagDefinitions_MatchTheBuiltInViews()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var list = (await w.Containers.CreateAsync(new(DataHost.Op(), "Errands", ContainerKind.List, null))).Container;
        var inList = await w.TaskAsync("In list", containerId: list.Id);
        await w.TaskAsync("In inbox");
        var parent = (await w.Tags.CreateAsync(new(DataHost.Op(), "work", null, null))).Tag;
        var child = (await w.Tags.CreateAsync(new(DataHost.Op(), "work/billing", null, null))).Tag;
        var tagged = await w.TaskAsync("Tagged child");
        await w.Tags.LinkAsync(new(DataHost.Op(), TagTargetKind.Task, tagged.Id, child.Id, null));

        var builtInList = await w.BuiltInAsync(new TaskQuerySpec { View = TaskView.Container, ContainerId = list.Id });
        var customList = await w.IdsAsync(Def("{\"field\":\"container\",\"op\":\"is\",\"value\":\"" + list.Id + "\"}"));
        var builtInTag = await w.BuiltInAsync(new TaskQuerySpec { View = TaskView.Tag, TagId = parent.Id });
        var customTag = await w.IdsAsync(Def("{\"field\":\"tag\",\"op\":\"hasAnyOf\",\"value\":[\"" + parent.Id + "\"],\"descendants\":true}"));
        var withoutDescendants = await w.IdsAsync(Def("{\"field\":\"tag\",\"op\":\"hasAnyOf\",\"value\":[\"" + parent.Id + "\"]}"));

        await SameSetAsync(customList, builtInList);
        await Assert.That(customList).Contains(inList.Id);
        await SameSetAsync(customTag, builtInTag);
        await Assert.That(customTag.Count).IsEqualTo(1);
        await Assert.That(withoutDescendants.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Run_WaitingAndRecentlyCompletedDefinitions_MatchTheBuiltInViews()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var waiting = await w.TaskAsync("Waiting on Sam");
        await w.StatusAsync(waiting, TaskItemStatus.WaitingOnSomeone);
        var done = await w.TaskAsync("Finished");
        await w.StatusAsync(done, TaskItemStatus.Done);
        await w.TaskAsync("Open");

        var builtInWaiting = await w.BuiltInAsync(new TaskQuerySpec { View = TaskView.Waiting });
        var customWaiting = await w.IdsAsync(Def("{\"field\":\"status\",\"op\":\"in\",\"value\":[\"WaitingOnSomeone\"]}"));
        var builtInDone = await w.BuiltInAsync(new TaskQuerySpec { View = TaskView.RecentlyCompleted, RecentDays = 30 });
        var customDone = await w.IdsAsync(Def(
            "{\"field\":\"status\",\"op\":\"in\",\"value\":[\"Done\"]},{\"field\":\"completed\",\"op\":\"inRange\",\"value\":{\"relative\":\"last\",\"days\":30}}"));

        await SameSetAsync(customWaiting, builtInWaiting);
        await SameSetAsync(customDone, builtInDone);
        await Assert.That(customDone.Count).IsEqualTo(1);
    }

    // ---- Field matrix -----------------------------------------------------------------------

    [Test]
    public async Task SourceAgent_UsesTheCreationActorRatherThanLaterEdits()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var agent = await w.TaskAsync("Agent work");
        var person = await w.TaskAsync("Person work");
        await host.Store.ExecuteAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "UPDATE TaskHistory SET Actor='agent:11111111-1111-1111-1111-111111111111' WHERE TaskId=$task AND EventKind='task.created';";
            command.Parameters.AddWithValue("$task", agent.Id);
            await command.ExecuteNonQueryAsync(token);
            return 0;
        });
        await SameSetAsync(await w.IdsAsync(Def("{\"field\":\"source\",\"op\":\"is\",\"value\":\"Agent\"}")), [agent.Id]);
        await SameSetAsync(await w.IdsAsync(Def("{\"field\":\"source\",\"op\":\"is\",\"value\":\"App\"}")), [person.Id]);
    }

    [Test]
    public async Task Run_EveryFieldAndOperator_SelectsTheExpectedTasks()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var a = await w.TaskAsync("Alpha report", priority: TaskPriority.High, estimate: 30, category: CapacityCategory.Work, due: Today.AddDays(3), notes: "quarterly numbers");
        var b = await w.TaskAsync("Beta call", priority: TaskPriority.Low, estimate: 90, planned: Today);
        var c = await w.TaskAsync("Gamma", priority: TaskPriority.Urgent);

        async Task Expect(string items, params TaskRecord[] expected) =>
            await SameSetAsync(await w.IdsAsync(Def(items)), expected.Select(task => task.Id));

        await Expect("{\"field\":\"priority\",\"op\":\"is\",\"value\":\"High\"}", a);
        await Expect("{\"field\":\"priority\",\"op\":\"atLeast\",\"value\":\"High\"}", a, c);
        await Expect("{\"field\":\"priority\",\"op\":\"atMost\",\"value\":\"Low\"}", b);
        await Expect("{\"field\":\"category\",\"op\":\"is\",\"value\":\"Work\"}", a);
        await Expect("{\"field\":\"estimate\",\"op\":\"lessThan\",\"value\":60}", a);
        await Expect("{\"field\":\"estimate\",\"op\":\"moreThan\",\"value\":60}", b);
        await Expect("{\"field\":\"estimate\",\"op\":\"isEmpty\"}", c);
        await Expect("{\"field\":\"due\",\"op\":\"isEmpty\"}", b, c);
        await Expect("{\"field\":\"due\",\"op\":\"isNotEmpty\"}", a);
        await Expect("{\"field\":\"due\",\"op\":\"before\",\"value\":{\"relative\":\"nextWeek\"}}", a);
        await Expect("{\"field\":\"due\",\"op\":\"after\",\"value\":\"" + Today.AddDays(2).ToString("yyyy-MM-dd") + "\"}", a);
        await Expect("{\"field\":\"due\",\"op\":\"on\",\"value\":\"" + Today.AddDays(3).ToString("yyyy-MM-dd") + "\"}", a);
        await Expect("{\"field\":\"planned\",\"op\":\"on\",\"value\":{\"relative\":\"today\"}}", b);
        await Expect("{\"field\":\"created\",\"op\":\"on\",\"value\":{\"relative\":\"today\"}}", a, b, c);
        await Expect("{\"field\":\"created\",\"op\":\"before\",\"value\":{\"relative\":\"today\"}}");
        await Expect("{\"field\":\"changed\",\"op\":\"inRange\",\"value\":{\"relative\":\"last\",\"days\":2}}", a, b, c);
        await Expect("{\"field\":\"completed\",\"op\":\"isNotEmpty\"}");
        await Expect("{\"field\":\"text\",\"op\":\"contains\",\"value\":\"report\"}", a);
        await Expect("{\"field\":\"text\",\"op\":\"contains\",\"value\":\"quarterly\"}", a);
        await Expect("{\"field\":\"text\",\"op\":\"contains\",\"value\":\"zzz\"}");
        await Expect("{\"field\":\"source\",\"op\":\"is\",\"value\":\"App\"}", a, b, c);
        await Expect("{\"field\":\"source\",\"op\":\"is\",\"value\":\"GitHub\"}");
        await Expect("{\"field\":\"recurring\",\"op\":\"is\",\"value\":false}", a, b, c);
        await Expect("{\"field\":\"hasPadLink\",\"op\":\"is\",\"value\":true}");
        await Expect("{\"field\":\"hasPageMention\",\"op\":\"is\",\"value\":false}", a, b, c);
        await Expect("{\"field\":\"waiting\",\"op\":\"isEmpty\"}", a, b, c);
        await Expect("{\"field\":\"waiting\",\"op\":\"contains\",\"value\":\"Sam\"}");
    }

    [Test]
    public async Task Run_NestedAnyAllGroups_CombineAsWritten()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var a = await w.TaskAsync("A", priority: TaskPriority.High, due: Today);
        var b = await w.TaskAsync("B", priority: TaskPriority.Low, due: Today);
        var c = await w.TaskAsync("C", priority: TaskPriority.High);
        await w.TaskAsync("D", priority: TaskPriority.Low);

        // (priority >= High) AND (due today OR estimate empty is false) -> only A and C pass the first, A passes the second.
        var ids = await w.IdsAsync(Def(
            "{\"field\":\"priority\",\"op\":\"atLeast\",\"value\":\"High\"},{\"match\":\"any\",\"items\":[{\"field\":\"due\",\"op\":\"on\",\"value\":{\"relative\":\"today\"}},{\"field\":\"recurring\",\"op\":\"is\",\"value\":true}]}"));
        var anyOf = await w.IdsAsync(Def(
            "{\"field\":\"priority\",\"op\":\"atLeast\",\"value\":\"High\"},{\"field\":\"due\",\"op\":\"on\",\"value\":{\"relative\":\"today\"}}", "any"));

        await SameSetAsync(ids, [a.Id]);
        await SameSetAsync(anyOf, [a.Id, b.Id, c.Id]);
    }

    [Test]
    public async Task Run_EmptyDefinition_ReturnsEveryActiveTask()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        await w.TaskAsync("One");
        await w.TaskAsync("Two");

        await Assert.That((await w.IdsAsync(Def(string.Empty))).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Run_Sort_PutsUndatedLastInBothDirectionsAndPagesWithoutDuplicates()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var late = await w.TaskAsync("Late", due: Today.AddDays(5));
        var soon = await w.TaskAsync("Soon", due: Today.AddDays(1));
        var none = await w.TaskAsync("None");

        var asc = await w.IdsAsync(Def(string.Empty), new ViewSort("due", false));
        var desc = await w.IdsAsync(Def(string.Empty), new ViewSort("due", true));

        await Assert.That(string.Join(",", asc)).IsEqualTo(string.Join(",", soon.Id, late.Id, none.Id));
        await Assert.That(string.Join(",", desc)).IsEqualTo(string.Join(",", late.Id, soon.Id, none.Id));

        var spec = new TaskQuerySpec { View = TaskView.Custom, Definition = ViewDefinition.Parse(Def(string.Empty)), Sort = new ViewSort("due", false), Limit = 2 };
        var first = await w.Queries.RunAsync(spec);
        var second = await w.Queries.RunAsync(spec with { Cursor = first.NextCursor });
        await Assert.That(first.Rows.Count).IsEqualTo(2);
        await Assert.That(second.Rows.Count).IsEqualTo(1);
        await Assert.That(second.NextCursor).IsNull();
        await Assert.That(first.TotalCount).IsEqualTo(3L);
    }

    [Test]
    public async Task Run_HostileTextAndIds_AreBoundAsValuesNotSql()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        await w.TaskAsync("Quote ' test", notes: "x");

        await Assert.That((await w.IdsAsync(Def("{\"field\":\"text\",\"op\":\"contains\",\"value\":\"x') OR 1=1 --\"}"))).Count).IsEqualTo(0);
        await Assert.That((await w.IdsAsync(Def("{\"field\":\"container\",\"op\":\"is\",\"value\":\"a' OR '1'='1\"}"))).Count).IsEqualTo(0);
        await Assert.That((await w.IdsAsync(Def("{\"field\":\"waiting\",\"op\":\"contains\",\"value\":\"' OR 1=1 --\"}"))).Count).IsEqualTo(0);
    }

    // ---- Saved views ------------------------------------------------------------------------

    private const string OpenDefinition = "{\"v\":1,\"match\":\"all\",\"items\":[{\"field\":\"status\",\"op\":\"notIn\",\"value\":[\"Done\",\"Cancelled\"]}]}";

    private static SaveViewCommand NewView(string name, string? definition = null, bool pinned = true, bool alerts = false) =>
        new(DataHost.Op(), null, 0, name, "bug", "blue", definition ?? OpenDefinition, null, pinned, alerts);

    [Test]
    public async Task Save_CreatePersistsAcrossRestart_WithCanonicalDefinitionAndLayout()
    {
        var root = TestPaths.NewDataRoot();
        string id;
        using (var host = await DataHost.StartAsync(root))
        {
            var created = await new World(host).Views.SaveAsync(NewView("Open bugs"));
            id = created.View.Id;
            await Assert.That(created.View.Version).IsEqualTo(1L);
            await Assert.That(created.View.Position).IsEqualTo(0);
            await Assert.That(ViewLayout.Parse(created.View.LayoutJson).Kind).IsEqualTo(ViewLayoutKind.List);
        }

        using (var host = await DataHost.StartAsync(root))
        {
            var view = await new World(host).Views.GetAsync(id);
            await Assert.That(view.Name).IsEqualTo("Open bugs");
            await Assert.That(view.Icon).IsEqualTo("bug");
            await Assert.That(view.Color).IsEqualTo("blue");
            await Assert.That(view.Pinned).IsTrue();
        }
    }

    [Test]
    public async Task Save_SameOperationIdTwice_ReplaysTheStoredResult()
    {
        using var host = await DataHost.StartAsync();
        var views = new World(host).Views;
        var command = NewView("Once");

        var first = await views.SaveAsync(command);
        var again = await views.SaveAsync(command);

        await Assert.That(again.Replayed).IsTrue();
        await Assert.That(again.View.Id).IsEqualTo(first.View.Id);
        await Assert.That((await views.ListAsync()).Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("", null, "{\"v\":1,\"items\":[]}")]
    [Arguments("Name", "no-such-icon", "{\"v\":1,\"items\":[]}")]
    [Arguments("Name", "bug", "{\"v\":1,\"items\":[{\"field\":\"nope\",\"op\":\"is\",\"value\":1}]}")]
    [Arguments("Name", "bug", "garbage")]
    public async Task Save_InvalidInput_IsRefusedWithAValidationError(string name, string? icon, string definition)
    {
        using var host = await DataHost.StartAsync();
        var views = new World(host).Views;

        var error = await Assert.That(async () => await views.SaveAsync(
            new(DataHost.Op(), null, 0, name, icon, null, definition, null, false, false))).Throws<AppException>();

        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.Validation);
    }

    [Test]
    public async Task Save_ManualSortWithoutOneContainer_IsRefused()
    {
        using var host = await DataHost.StartAsync();
        var views = new World(host).Views;
        var layout = "{\"v\":1,\"kind\":\"list\",\"sort\":{\"field\":\"manual\",\"dir\":\"asc\"}}";

        var error = await Assert.That(async () => await views.SaveAsync(
            new(DataHost.Op(), null, 0, "Manual", "list", null, OpenDefinition, layout, false, false))).Throws<AppException>();

        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.Validation);
    }

    [Test]
    public async Task Save_UpdateWithStaleVersion_IsAVersionConflict()
    {
        using var host = await DataHost.StartAsync();
        var views = new World(host).Views;
        var created = (await views.SaveAsync(NewView("Original"))).View;
        await views.SaveAsync(new(DataHost.Op(), created.Id, 1, "Renamed", "bug", null, OpenDefinition, null, true, false));

        var error = await Assert.That(async () => await views.SaveAsync(
            new(DataHost.Op(), created.Id, 1, "Stale", "bug", null, OpenDefinition, null, true, false))).Throws<AppException>();

        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.VersionConflict);
        await Assert.That((await views.GetAsync(created.Id)).Name).IsEqualTo("Renamed");
    }

    [Test]
    public async Task DuplicateDeleteRestoreReorderAndPin_FollowTheirContracts()
    {
        using var host = await DataHost.StartAsync();
        var views = new World(host).Views;
        var one = (await views.SaveAsync(NewView("One"))).View;
        var two = (await views.SaveAsync(NewView("Two"))).View;

        var copy = (await views.DuplicateAsync(DataHost.Op(), one.Id)).View;
        await Assert.That(copy.Name).IsEqualTo("One (copy)");
        await Assert.That(copy.AlertsEnabled).IsFalse();
        await Assert.That(copy.DefinitionJson).IsEqualTo(one.DefinitionJson);

        var reordered = await views.ReorderAsync(DataHost.Op(), [copy.Id, two.Id, one.Id]);
        await Assert.That(string.Join(",", reordered.Select(view => view.Id))).IsEqualTo(string.Join(",", copy.Id, two.Id, one.Id));
        await Assert.That(string.Join(",", (await views.ListAsync()).Select(view => view.Id))).IsEqualTo(string.Join(",", copy.Id, two.Id, one.Id));

        var current = await views.GetAsync(two.Id);
        var unpinned = (await views.SetPinnedAsync(DataHost.Op(), two.Id, current.Version, false)).View;
        await Assert.That(unpinned.Pinned).IsFalse();

        var deleted = (await views.DeleteAsync(DataHost.Op(), two.Id, unpinned.Version)).View;
        await Assert.That(deleted.DeletedAtUtc).IsNotNull();
        await Assert.That((await views.ListAsync()).Count).IsEqualTo(2);
        await Assert.That((await views.ListAsync(includeDeleted: true)).Count).IsEqualTo(3);

        var restored = (await views.RestoreAsync(DataHost.Op(), two.Id, deleted.Version)).View;
        await Assert.That(restored.DeletedAtUtc).IsNull();
        await Assert.That((await views.ListAsync()).Count).IsEqualTo(3);
    }

    [Test]
    public async Task Update_DeletedView_IsRefusedUntilRestored()
    {
        using var host = await DataHost.StartAsync();
        var views = new World(host).Views;
        var view = (await views.SaveAsync(NewView("Gone"))).View;
        var deleted = (await views.DeleteAsync(DataHost.Op(), view.Id, view.Version)).View;

        var error = await Assert.That(async () => await views.SaveAsync(
            new(DataHost.Op(), view.Id, deleted.Version, "Edit", "bug", null, OpenDefinition, null, true, false))).Throws<AppException>();

        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.NotFound);
    }

    // ---- Add task in context -----------------------------------------------------------------

    [Test]
    public async Task ContextDefaults_PrefillsOnlyFieldsFixedByTopLevelAllConditions()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var list = (await w.Containers.CreateAsync(new(DataHost.Op(), "Project", ContainerKind.List, null))).Container;
        var tag = (await w.Tags.CreateAsync(new(DataHost.Op(), "bug", null, null))).Tag;
        var definition = Def(
            "{\"field\":\"container\",\"op\":\"is\",\"value\":\"" + list.Id + "\"},"
            + "{\"field\":\"status\",\"op\":\"in\",\"value\":[\"InProgress\"]},"
            + "{\"field\":\"priority\",\"op\":\"is\",\"value\":\"High\"},"
            + "{\"field\":\"category\",\"op\":\"is\",\"value\":\"Work\"},"
            + "{\"field\":\"tag\",\"op\":\"hasAnyOf\",\"value\":[\"" + tag.Id + "\"]},"
            + "{\"field\":\"due\",\"op\":\"on\",\"value\":{\"relative\":\"tomorrow\"}},"
            + "{\"match\":\"any\",\"items\":[{\"field\":\"planned\",\"op\":\"on\",\"value\":\"2026-12-01\"}]}");
        var view = (await w.Views.SaveAsync(NewView("Context", definition))).View;

        var defaults = await w.Views.ContextDefaultsAsync(view.Id);

        await Assert.That(defaults.ContainerId).IsEqualTo(list.Id);
        await Assert.That(defaults.Status).IsEqualTo(TaskItemStatus.InProgress);
        await Assert.That(defaults.Priority).IsEqualTo(TaskPriority.High);
        await Assert.That(defaults.Category).IsEqualTo(CapacityCategory.Work);
        await Assert.That(string.Join(",", defaults.TagIds)).IsEqualTo(tag.Id);
        await Assert.That(defaults.DueDate).IsEqualTo(Today.AddDays(1));
        await Assert.That(defaults.PlannedDate).IsNull();
    }

    [Test]
    public async Task ContextDefaults_TopLevelAny_PrefillsNothing()
    {
        using var host = await DataHost.StartAsync();
        var w = new World(host);
        var view = (await w.Views.SaveAsync(NewView("Any", Def("{\"field\":\"priority\",\"op\":\"is\",\"value\":\"High\"}", "any")))).View;

        var defaults = await w.Views.ContextDefaultsAsync(view.Id);

        await Assert.That(defaults.Priority).IsNull();
    }
}
