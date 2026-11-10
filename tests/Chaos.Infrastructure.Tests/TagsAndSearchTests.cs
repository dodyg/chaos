using Chaos.Application.Abstractions;
using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Tests.Testing;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests;

/// <summary>Package 3 behavior on real file-backed SQLite: tag integrity, built-in views, search, counts and paging.</summary>
public sealed class TagsAndSearchTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    /// <summary>One storage session plus the commands under test; the profile zone is pinned to UTC for deterministic dates.</summary>
    private sealed class Host : IDisposable
    {
        public required string Root { get; init; }
        public required ChaosStorageHandle Storage { get; init; }
        public required TaskCommands Tasks { get; init; }
        public required SubtaskCommands Subtasks { get; init; }
        public required ContainerCommands Containers { get; init; }
        public required TagCommands Tags { get; init; }
        public required TaskQueries Queries { get; init; }

        public static async Task<Host> StartAsync(string? root = null, DateTimeOffset? now = null)
        {
            root ??= TestPaths.NewDataRoot();
            var clock = new FixedTimeProvider(now ?? Now);
            var ids = new GuidIds();
            var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
            Execute(storage.DatabasePath, "UPDATE Profiles SET ZoneId = 'UTC';");
            return new Host
            {
                Root = root,
                Storage = storage,
                Tasks = new TaskCommands(storage.Store, ids, clock),
                Subtasks = new SubtaskCommands(storage.Store, ids, clock),
                Containers = new ContainerCommands(storage.Store, ids, clock),
                Tags = new TagCommands(storage.Store, ids, clock),
                Queries = new TaskQueries(storage.Store, clock),
            };
        }

        public async Task<TaskRecord> TaskAsync(string title, string? containerId = null, string? notes = null) =>
            (await Tasks.CaptureAsync(new(Op(), containerId ?? string.Empty, title, notes))).Task;

        public async Task<TaskRecord> DatesAsync(TaskRecord task, DateOnly? planned, DateOnly? due, TimeOnly? dueTime = null, string? dueZone = null, TaskPriority priority = TaskPriority.Normal) =>
            (await Tasks.UpdateDetailsAsync(new(Op(), task.Id, task.Version, task.Title, task.Notes, planned, null, null, due, dueTime, dueZone, priority, null, CapacityCategory.Personal))).Task;

        public async Task<TagRecord> TagAsync(string path) => (await Tags.CreateAsync(new(Op(), path, null, null))).Tag;

        public async Task LinkAsync(TaskRecord task, string tagId) => await Tags.LinkAsync(new(Op(), TagTargetKind.Task, task.Id, tagId, null));

        public async Task<IReadOnlyList<string>> TitlesAsync(TaskQuerySpec spec) =>
            [.. (await Queries.RunAsync(spec)).Rows.Select(row => row.Task.Title)];

        public void Dispose() => Storage.Dispose();
    }

    private sealed class GuidIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private static string Op() => Guid.NewGuid().ToString();

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task<AppException> ThrowsAppAsync(Func<Task> action)
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
    public async Task CreatePath_NestedSegments_ReusesExistingSegmentsAndRejectsExistingLeaf()
    {
        using var host = await Host.StartAsync();

        var garden = await host.Tags.CreateAsync(new(Op(), "Home/Garden", null, "green"));
        var kitchen = await host.Tags.CreateAsync(new(Op(), "home/Kitchen", null, null));
        var duplicate = await ThrowsAppAsync(() => host.Tags.CreateAsync(new(Op(), "HOME/garden", null, null)));

        var tags = await host.Tags.ListAsync();
        await Assert.That(string.Join(",", tags.Select(row => row.Path))).IsEqualTo("Home,Home/Garden,Home/Kitchen");
        await Assert.That(garden.Created.Count).IsEqualTo(2);
        await Assert.That(kitchen.Created.Count).IsEqualTo(1);
        await Assert.That(garden.Tag.ColorKey).IsEqualTo("green");
        await Assert.That(duplicate.Kind).IsEqualTo(AppErrorKind.Validation);
    }

    [Test]
    [Arguments("work", "Work")]
    [Arguments("work", "ｗｏｒｋ")]
    [Arguments("Straße", "STRASSE")]
    public async Task Rename_ToSiblingIdentity_IsRejectedButAllowedUnderAnotherParent(string existing, string candidate)
    {
        using var host = await Host.StartAsync();
        await host.TagAsync(existing);
        var other = await host.TagAsync("other");

        var sameIdentity = TagIdentity(existing) == TagIdentity(candidate);
        var rename = await ThrowsOrNullAsync(() => host.Tags.RenameAsync(new(Op(), other.Id, other.Version, candidate)));
        var nested = await host.Tags.CreateAsync(new(Op(), candidate, other.Id, null));

        await Assert.That(rename is not null).IsEqualTo(sameIdentity);
        await Assert.That(nested.Tag.ParentTagId).IsEqualTo(other.Id);
    }

    private static string TagIdentity(string name) => Chaos.Domain.Tags.TagName.Normalize(name);

    private static async Task<AppException?> ThrowsOrNullAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (AppException error)
        {
            return error;
        }
    }

    [Test]
    [Arguments("home//garden")]
    [Arguments("/home")]
    [Arguments("home/")]
    [Arguments("   ")]
    public async Task CreatePath_EmptySegment_IsRejectedWithoutWriting(string path)
    {
        using var host = await Host.StartAsync();

        var error = await ThrowsAppAsync(() => host.Tags.CreateAsync(new(Op(), path, null, null)));

        await Assert.That(error.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That((await host.Tags.ListAsync()).Count).IsEqualTo(0);
    }

    [Test]
    public async Task Move_BelowOwnDescendant_IsRejectedAndRootMoveSucceeds()
    {
        using var host = await Host.StartAsync();
        var leaf = await host.TagAsync("a/b/c");
        var tags = await host.Tags.ListAsync();
        var a = tags.Single(row => row.Path == "a").Tag;

        var cycle = await ThrowsAppAsync(() => host.Tags.MoveAsync(new(Op(), a.Id, a.Version, leaf.Id)));
        var self = await ThrowsAppAsync(() => host.Tags.MoveAsync(new(Op(), a.Id, a.Version, a.Id)));
        var moved = await host.Tags.MoveAsync(new(Op(), leaf.Id, leaf.Version, null));

        await Assert.That(cycle.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That(self.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That(moved.Tag.ParentTagId).IsNull();
        await Assert.That(string.Join(",", (await host.Tags.ListAsync()).Select(row => row.Path))).IsEqualTo("a,a/b,c");
    }

    [Test]
    public async Task Merge_WithChildCollisions_MergesRecursivelyDeduplicatesAndMatchesPreview()
    {
        using var host = await Host.StartAsync();
        var homeGarden = await host.TagAsync("home/garden");
        var homeKitchen = await host.TagAsync("home/kitchen");
        var houseGarden = await host.TagAsync("house/garden");
        var all = await host.Tags.ListAsync();
        var home = all.Single(row => row.Path == "home").Tag;
        var house = all.Single(row => row.Path == "house").Tag;
        var shared = await host.TaskAsync("Shared");
        var onlyHome = await host.TaskAsync("Only home garden");
        var kitchenTask = await host.TaskAsync("Kitchen");
        await host.LinkAsync(shared, homeGarden.Id);
        await host.LinkAsync(shared, houseGarden.Id);
        await host.LinkAsync(onlyHome, homeGarden.Id);
        await host.LinkAsync(kitchenTask, homeKitchen.Id);
        await host.LinkAsync(shared, home.Id);

        var preview = await host.Tags.PreviewMergeAsync(home.Id, house.Id);
        var merged = await host.Tags.MergeAsync(new(Op(), home.Id, home.Version, house.Id, house.Version));

        await Assert.That(merged.Applied).IsEqualTo(preview);
        await Assert.That(preview).IsEqualTo(new TagMergePreview(TaskLinksMoved: 2, TaskLinksAlreadyPresent: 1, SubtaskLinksMoved: 0, SubtaskLinksAlreadyPresent: 0, ChildTagsMoved: 1, ChildTagsMerged: 1));
        await Assert.That(string.Join(",", (await host.Tags.ListAsync()).Select(row => row.Path))).IsEqualTo("house,house/garden,house/kitchen");
        await Assert.That(string.Join(",", (await host.Tags.ListTargetTagsAsync(TagTargetKind.Task, shared.Id)).Order(StringComparer.Ordinal))).IsEqualTo(string.Join(",", new[] { house.Id, houseGarden.Id }.Order(StringComparer.Ordinal)));
        var houseView = await host.TitlesAsync(new TaskQuerySpec { View = TaskView.Tag, TagId = house.Id });
        await Assert.That(string.Join(",", houseView.Order())).IsEqualTo("Kitchen,Only home garden,Shared");
    }

    [Test]
    public async Task Merge_IntoOwnDescendant_IsRejected()
    {
        using var host = await Host.StartAsync();
        var child = await host.TagAsync("parent/child");
        var parent = (await host.Tags.ListAsync()).Single(row => row.Path == "parent").Tag;

        var error = await ThrowsAppAsync(() => host.Tags.MergeAsync(new(Op(), parent.Id, parent.Version, child.Id, child.Version)));

        await Assert.That(error.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That((await host.Tags.ListAsync()).Count).IsEqualTo(2);
    }

    [Test]
    public async Task Delete_RemovesLinksButNotTasks_AndRestoreAfterRestartReattachesThem()
    {
        var root = TestPaths.NewDataRoot();
        string tagId;
        long version;
        string taskId;
        string subtaskId;
        using (var host = await Host.StartAsync(root))
        {
            var garden = await host.TagAsync("home/garden");
            var home = (await host.Tags.ListAsync()).Single(row => row.Path == "home").Tag;
            var task = await host.TaskAsync("Weed beds");
            var subtask = (await host.Subtasks.AddAsync(new(Op(), task.Id, "Buy gloves", ""))).Subtask;
            await host.LinkAsync(task, garden.Id);
            await host.Tags.LinkAsync(new(Op(), TagTargetKind.Subtask, subtask.Id, home.Id, null));

            var deleted = await host.Tags.DeleteAsync(new(Op(), home.Id, home.Version));

            await Assert.That((await host.Tags.ListAsync()).Count).IsEqualTo(0);
            await Assert.That((await host.Tasks.GetAsync(task.Id)) is not null).IsTrue();
            await Assert.That((await host.Tags.ListTargetTagsAsync(TagTargetKind.Task, task.Id)).Count).IsEqualTo(0);
            (tagId, version, taskId, subtaskId) = (home.Id, deleted.Tag.Version, task.Id, subtask.Id);
        }

        using (var host = await Host.StartAsync(root))
        {
            var deletedList = await host.Tags.ListDeletedAsync();
            await host.Tags.RestoreAsync(new(Op(), tagId, version));

            await Assert.That(deletedList.Select(row => row.Id)).Contains(tagId);
            await Assert.That(string.Join(",", (await host.Tags.ListAsync()).Select(row => row.Path))).IsEqualTo("home,home/garden");
            await Assert.That((await host.Tags.ListTargetTagsAsync(TagTargetKind.Task, taskId)).Count).IsEqualTo(1);
            await Assert.That(string.Join(",", await host.Tags.ListTargetTagsAsync(TagTargetKind.Subtask, subtaskId))).IsEqualTo(tagId);
            await Assert.That(await host.TitlesAsync(new TaskQuerySpec { View = TaskView.Tag, TagId = tagId })).Contains("Weed beds");
        }
    }

    [Test]
    public async Task Restore_WhenSiblingNameReused_IsRejectedAtomically()
    {
        using var host = await Host.StartAsync();
        var errands = await host.TagAsync("errands");
        var deleted = await host.Tags.DeleteAsync(new(Op(), errands.Id, errands.Version));
        await host.TagAsync("Errands");

        var error = await ThrowsAppAsync(() => host.Tags.RestoreAsync(new(Op(), errands.Id, deleted.Tag.Version)));

        await Assert.That(error.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That((await host.Tags.ListDeletedAsync()).Select(row => row.Id)).Contains(errands.Id);
    }

    [Test]
    public async Task Link_ByPath_CreatesNestedTagsAndIsIdempotent()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Call plumber");

        var first = await host.Tags.LinkAsync(new(Op(), TagTargetKind.Task, task.Id, null, "#home/repairs"));
        var again = await host.Tags.LinkAsync(new(Op(), TagTargetKind.Task, task.Id, first.TagId, null));
        var suggestions = await host.Tags.SuggestAsync("repair");

        await Assert.That(first.Created.Count).IsEqualTo(2);
        await Assert.That(again.Changed).IsFalse();
        await Assert.That(again.TagIds.Count).IsEqualTo(1);
        await Assert.That((await host.Tasks.GetAsync(task.Id))!.ContainerId).IsEqualTo(task.ContainerId);
        await Assert.That(suggestions.Select(row => row.Path)).Contains("home/repairs");
    }

    [Test]
    public async Task Suggest_SingularTyped_OffersExistingPluralWithoutCreatingIt()
    {
        using var host = await Host.StartAsync();
        await host.TagAsync("errands");
        await host.TagAsync("phone");

        var suggestions = await host.Tags.SuggestAsync("#Errand");

        await Assert.That(string.Join(",", suggestions.Select(row => row.Path))).IsEqualTo("errands");
        await Assert.That((await host.Tags.ListAsync()).Count).IsEqualTo(2);
    }

    [Test]
    public async Task TagView_IncludesDescendantsAndSubtaskTags_ExcludesDeletedAndArchived()
    {
        using var host = await Host.StartAsync();
        var garden = await host.TagAsync("home/garden");
        var home = (await host.Tags.ListAsync()).Single(row => row.Path == "home").Tag;
        var direct = await host.TaskAsync("Direct");
        var child = await host.TaskAsync("Child tag");
        var viaSubtask = await host.TaskAsync("Via subtask");
        var deleted = await host.TaskAsync("Deleted");
        var archivedList = (await host.Containers.CreateAsync(new(Op(), "Old", ContainerKind.List, null))).Container;
        var archivedTask = await host.TaskAsync("Archived", archivedList.Id);
        var subtask = (await host.Subtasks.AddAsync(new(Op(), viaSubtask.Id, "step", ""))).Subtask;
        await host.LinkAsync(direct, home.Id);
        await host.LinkAsync(child, garden.Id);
        await host.LinkAsync(deleted, home.Id);
        await host.LinkAsync(archivedTask, home.Id);
        await host.Tags.LinkAsync(new(Op(), TagTargetKind.Subtask, subtask.Id, garden.Id, null));
        await host.Tasks.DeleteAsync(new(Op(), deleted.Id, deleted.Version));
        await host.Containers.ArchiveAsync(new(Op(), archivedList.Id, archivedList.Version));

        var homeView = await host.TitlesAsync(new TaskQuerySpec { View = TaskView.Tag, TagId = home.Id });
        var gardenView = await host.TitlesAsync(new TaskQuerySpec { View = TaskView.Tag, TagId = garden.Id });
        var withArchive = await host.TitlesAsync(new TaskQuerySpec { View = TaskView.Tag, TagId = home.Id, IncludeArchived = true });

        await Assert.That(string.Join(",", homeView.Order())).IsEqualTo("Child tag,Direct,Via subtask");
        await Assert.That(string.Join(",", gardenView.Order())).IsEqualTo("Child tag,Via subtask");
        await Assert.That(withArchive).Contains("Archived");
    }

    [Test]
    public async Task Search_NotesSubtasksDiacriticsAndArabicDigits_ReturnParentTaskRows()
    {
        using var host = await Host.StartAsync();
        var notes = await host.TaskAsync("Kitchen sink", notes: "Ask the plumber about the leak");
        var parent = await host.TaskAsync("Moving day");
        await host.Subtasks.AddAsync(new(Op(), parent.Id, "Call landlord", ""));
        var removed = (await host.Subtasks.AddAsync(new(Op(), parent.Id, "Return keys", ""))).Subtask;
        await host.Subtasks.DeleteAsync(new(Op(), removed.Id, removed.Version, removed.Title, removed.Notes));
        await host.TaskAsync("اتصل السبّاك غرفة ٣");
        await host.TaskAsync("Café crème order");

        async Task<IReadOnlyList<TaskQueryRow>> SearchAsync(string text) => (await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Search, Text = text })).Rows;

        var plumber = await SearchAsync("plumb");
        var landlord = await SearchAsync("landlord");
        var keys = await SearchAsync("keys");
        var arabic = await SearchAsync("السباك 3");
        var accents = await SearchAsync("creme");
        var hostile = await SearchAsync("\"plumber) OR * NEAR(");
        var punctuationOnly = await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Search, Text = "\"*()" });

        await Assert.That(plumber.Single().Task.Id).IsEqualTo(notes.Id);
        await Assert.That(landlord.Single().Task.Id).IsEqualTo(parent.Id);
        await Assert.That(landlord.Single().Matches).Contains(SearchMatchKind.Subtask);
        await Assert.That(keys.Count).IsEqualTo(0);
        await Assert.That(arabic.Count).IsEqualTo(1);
        await Assert.That(accents.Single().Task.Title).IsEqualTo("Café crème order");
        await Assert.That(hostile.Count).IsEqualTo(0);
        await Assert.That(punctuationOnly.TotalCount).IsEqualTo(0L);
    }

    [Test]
    public async Task Search_EditedAndDeletedTasks_TrackTheirCurrentText()
    {
        using var host = await Host.StartAsync();
        var task = await host.TaskAsync("Draft budget");
        var edited = await host.DatesAsync(task with { Title = "Final forecast" }, null, null);
        var gone = await host.TaskAsync("Forecast archive copy");
        await host.Tasks.DeleteAsync(new(Op(), gone.Id, gone.Version));

        var oldText = await host.TitlesAsync(new TaskQuerySpec { View = TaskView.Search, Text = "budget" });
        var newText = await host.TitlesAsync(new TaskQuerySpec { View = TaskView.Search, Text = "forecast" });

        await Assert.That(oldText.Count).IsEqualTo(0);
        await Assert.That(string.Join(",", newText)).IsEqualTo(edited.Title);
    }

    [Test]
    public async Task RebuildSearchIndex_ProducesTheSameResultsAndRunsAtStartupWhenStale()
    {
        var root = TestPaths.NewDataRoot();
        using (var host = await Host.StartAsync(root))
        {
            await host.TaskAsync("Renew passport", notes: "Embassy appointment");
            var parent = await host.TaskAsync("Trip");
            await host.Subtasks.AddAsync(new(Op(), parent.Id, "Embassy forms", ""));
            var before = await host.TitlesAsync(new TaskQuerySpec { View = TaskView.Search, Text = "embassy" });

            var documents = await host.Queries.RebuildSearchIndexAsync();
            var after = await host.TitlesAsync(new TaskQuerySpec { View = TaskView.Search, Text = "embassy" });

            await Assert.That(documents).IsEqualTo(3L);
            await Assert.That(string.Join(",", after.Order())).IsEqualTo(string.Join(",", before.Order()));
            Execute(host.Storage.DatabasePath, "DELETE FROM SearchIndex; DELETE FROM SearchDocuments; UPDATE SearchIndexState SET NormalizationVersion = 0;");
        }

        using (var host = await Host.StartAsync(root))
        {
            var restarted = await host.TitlesAsync(new TaskQuerySpec { View = TaskView.Search, Text = "embassy" });
            await Assert.That(string.Join(",", restarted.Order())).IsEqualTo("Renew passport,Trip");
        }
    }

    [Test]
    public async Task DateViews_SeparatePlannedFromDue_AndJudgeTimedDueInItsZone()
    {
        // 09:00 UTC on 10 Nov; Pacific/Kiritimati (UTC+14) is already 23:00 on 10 Nov.
        using var host = await Host.StartAsync();
        var today = new DateOnly(2026, 11, 10);
        await host.DatesAsync(await host.TaskAsync("Due yesterday"), null, today.AddDays(-1));
        await host.DatesAsync(await host.TaskAsync("Due today all day"), null, today);
        await host.DatesAsync(await host.TaskAsync("Due today 08:00 UTC"), null, today, new TimeOnly(8, 0), "UTC");
        await host.DatesAsync(await host.TaskAsync("Due today 10:00 UTC"), null, today, new TimeOnly(10, 0), "UTC");
        await host.DatesAsync(await host.TaskAsync("Due today 22:00 Kiritimati"), null, today, new TimeOnly(22, 0), "Pacific/Kiritimati");
        await host.DatesAsync(await host.TaskAsync("Planned yesterday only"), today.AddDays(-1), null);
        await host.DatesAsync(await host.TaskAsync("Planned and due today"), today, today);
        await host.DatesAsync(await host.TaskAsync("Planned in 3 days"), today.AddDays(3), today.AddDays(9));
        await host.DatesAsync(await host.TaskAsync("Due in 20 days"), null, today.AddDays(20));
        await host.DatesAsync(await host.TaskAsync("Planned in 5 days only"), today.AddDays(5), null);
        await host.DatesAsync(await host.TaskAsync("Due in 2 days, planned in 4"), today.AddDays(4), today.AddDays(2));
        var done = await host.DatesAsync(await host.TaskAsync("Done yesterday-due"), null, today.AddDays(-1));
        await host.Tasks.SetStatusAsync(new(Op(), done.Id, done.Version, TaskItemStatus.Done, null));

        var overdue = await host.TitlesAsync(new TaskQuerySpec { View = TaskView.Overdue });
        var todayView = await host.TitlesAsync(new TaskQuerySpec { View = TaskView.Today });
        var upcoming = (await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Upcoming })).Rows;
        var recent = await host.TitlesAsync(new TaskQuerySpec { View = TaskView.RecentlyCompleted });

        await Assert.That(string.Join(",", overdue)).IsEqualTo("Due yesterday,Due today 08:00 UTC,Due today 22:00 Kiritimati");
        await Assert.That(todayView.Count(title => title == "Planned and due today")).IsEqualTo(1);
        await Assert.That(todayView).DoesNotContain("Planned yesterday only");
        await Assert.That(todayView.Count).IsEqualTo(5);
        await Assert.That(string.Join(",", upcoming.Select(row => $"{row.Task.Title}@{row.GroupDate:dd}"))).IsEqualTo("Due in 2 days, planned in 4@12,Planned in 3 days@13,Planned in 5 days only@15");
        await Assert.That(string.Join(",", recent)).IsEqualTo("Done yesterday-due");
    }

    [Test]
    public async Task CountsAndPages_MatchForEveryViewAndFilter()
    {
        using var host = await Host.StartAsync();
        var today = new DateOnly(2026, 11, 10);
        var tag = await host.TagAsync("batch");
        for (var index = 0; index < 60; index++)
        {
            var task = await host.TaskAsync($"Task {index:00}", notes: index % 3 == 0 ? "needle" : "hay");
            task = await host.DatesAsync(task, index % 4 == 0 ? today : today.AddDays(index % 7), index % 5 == 0 ? today.AddDays(-(index % 3) - 1) : null, priority: (TaskPriority)(index % 5));
            if (index % 2 == 0) await host.LinkAsync(task, tag.Id);
            if (index % 6 == 0) await host.Tasks.SetStatusAsync(new(Op(), task.Id, task.Version, TaskItemStatus.Done, null));
            if (index % 10 == 1) await host.Tasks.SetStatusAsync(new(Op(), task.Id, task.Version, TaskItemStatus.WaitingOnSomeone, null));
        }

        TaskQuerySpec[] specs =
        [
            new() { View = TaskView.Today },
            new() { View = TaskView.Upcoming },
            new() { View = TaskView.Overdue },
            new() { View = TaskView.AssignedToMe },
            new() { View = TaskView.RecentlyCompleted },
            new() { View = TaskView.Waiting },
            new() { View = TaskView.Tag, TagId = tag.Id },
            new() { View = TaskView.Tag, TagId = tag.Id, IncludeCompleted = true, Priorities = [TaskPriority.High, TaskPriority.Urgent] },
            new() { View = TaskView.Search, Text = "needle" },
            new() { View = TaskView.Search, Text = "task", IncludeCompleted = true },
            new() { View = TaskView.AssignedToMe, Text = "hay", Planned = new(today, today.AddDays(3)) },
        ];

        var counts = await host.Queries.CountsAsync();
        foreach (var spec in specs)
        {
            var seen = new List<string>();
            string? cursor = null;
            long total = -1;
            do
            {
                var page = await host.Queries.RunAsync(spec with { Limit = 7, Cursor = cursor });
                seen.AddRange(page.Rows.Select(row => row.Task.Id));
                total = page.TotalCount;
                cursor = page.NextCursor;
            }
            while (cursor is not null);

            var count = await host.Queries.CountAsync(spec);
            await Assert.That(count).IsEqualTo(total);
            await Assert.That((long)seen.Count).IsEqualTo(count);
            await Assert.That(seen.Distinct().Count()).IsEqualTo(seen.Count);
            if (spec is { View: not TaskView.Tag and not TaskView.Search } && spec.Text is null)
            {
                await Assert.That(counts.Views[spec.View]).IsEqualTo(count);
            }
        }

        await Assert.That(counts.Tags[tag.Id]).IsEqualTo(await host.Queries.CountAsync(new TaskQuerySpec { View = TaskView.Tag, TagId = tag.Id }));
    }

    [Test]
    public async Task Cursor_EditsAndInsertsBetweenPages_NeverDuplicateOrSkipUnchangedRows()
    {
        using var host = await Host.StartAsync();
        var created = new List<TaskRecord>();
        for (var index = 0; index < 12; index++)
        {
            created.Add(await host.TaskAsync($"Row {index:00}"));
        }

        var spec = new TaskQuerySpec { View = TaskView.AssignedToMe, Limit = 5 };
        var first = await host.Queries.RunAsync(spec);
        // A new task sorts after everything; an edit to a row already shown does not move it.
        await host.TaskAsync("Row 12");
        await host.DatesAsync(created[0] with { Title = "Row 00 edited" }, null, null);
        var rest = new List<string>();
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await host.Queries.RunAsync(spec with { Cursor = cursor });
            rest.AddRange(page.Rows.Select(row => row.Task.Title));
            cursor = page.NextCursor;
        }

        var all = first.Rows.Select(row => row.Task.Title).Concat(rest).ToList();
        await Assert.That(all.Count).IsEqualTo(13);
        await Assert.That(all.Distinct().Count()).IsEqualTo(13);
        await Assert.That(rest[0]).IsEqualTo("Row 05");
    }

    [Test]
    public async Task Cursor_FromAnotherView_IsRejectedAsValidation()
    {
        using var host = await Host.StartAsync();
        for (var index = 0; index < 3; index++) await host.TaskAsync($"Row {index}");
        var page = await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.AssignedToMe, Limit = 1 });

        var wrongView = await ThrowsAppAsync(() => host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Today, Cursor = page.NextCursor }));
        var garbage = await ThrowsAppAsync(() => host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.AssignedToMe, Cursor = "not-a-cursor" }));

        await Assert.That(wrongView.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That(garbage.Kind).IsEqualTo(AppErrorKind.Validation);
    }
}
