using Chaos.Application.Abstractions;
using Chaos.Application.Capture;
using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Tests.Testing;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests;

/// <summary>Work/Personal focus on real SQLite: query and count filters, list default categories and capture.</summary>
public sealed class CategoryFocusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 10);

    private static string Op() => Guid.NewGuid().ToString();

    private sealed class Host : IDisposable
    {
        public required ChaosStorageHandle Storage { get; init; }
        public required TaskCommands Tasks { get; init; }
        public required ContainerCommands Containers { get; init; }
        public required CaptureCommands Capture { get; init; }
        public required TaskQueries Queries { get; init; }

        public static async Task<Host> StartAsync()
        {
            var clock = new FixedTimeProvider(Now);
            var ids = new SequentialIdGenerator();
            var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, clock, CancellationToken.None);
            using (var connection = new SqliteConnection($"Data Source={storage.DatabasePath};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE Profiles SET ZoneId = 'UTC';";
                command.ExecuteNonQuery();
            }

            return new Host
            {
                Storage = storage,
                Tasks = new TaskCommands(storage.Store, ids, clock),
                Containers = new ContainerCommands(storage.Store, ids, clock),
                Capture = new CaptureCommands(storage.Store, ids, clock),
                Queries = new TaskQueries(storage.Store, clock),
            };
        }

        public async Task<TaskRecord> TodayTaskAsync(string title, CapacityCategory category)
        {
            var task = (await Tasks.CaptureAsync(new(Op(), string.Empty, title, null))).Task;
            return (await Tasks.UpdateDetailsAsync(new(Op(), task.Id, task.Version, task.Title, task.Notes, Today, null, null, null, null, null, TaskPriority.Normal, null, category))).Task;
        }

        public async Task<IReadOnlyList<TaskRecord>> CaptureAsync(params CaptureTaskDraft[] drafts) =>
            (await Capture.CreateAsync(new CreateCapturedTasksCommand(Op(), drafts))).Tasks;

        public void Dispose() => Storage.Dispose();
    }

    [Test]
    public async Task CategoryFilter_LimitsViewRowsAndCounts()
    {
        using var host = await Host.StartAsync();
        await host.TodayTaskAsync("Ship release", CapacityCategory.Work);
        await host.TodayTaskAsync("Water plants", CapacityCategory.Personal);

        var work = await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Today, Category = CapacityCategory.Work });
        var both = await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Today });
        await Assert.That(work.Rows.Select(row => row.Task.Title).ToArray()).IsEquivalentTo(new[] { "Ship release" });
        await Assert.That(work.TotalCount).IsEqualTo(1);
        await Assert.That(both.TotalCount).IsEqualTo(2);

        var personalCounts = await host.Queries.CountsAsync(CapacityCategory.Personal);
        var allCounts = await host.Queries.CountsAsync();
        await Assert.That(personalCounts.Views[TaskView.Today]).IsEqualTo(1);
        await Assert.That(allCounts.Views[TaskView.Today]).IsEqualTo(2);
    }

    [Test]
    public async Task DefaultCategory_PersistsAndClears_AndProtectsTheInbox()
    {
        using var host = await Host.StartAsync();
        var list = (await host.Containers.CreateAsync(new(Op(), "Sprint", ContainerKind.List, null))).Container;
        await Assert.That(list.DefaultCategory).IsNull();

        var set = (await host.Containers.SetDefaultCategoryAsync(new(Op(), list.Id, list.Version, CapacityCategory.Work))).Container;
        await Assert.That(set.Version).IsEqualTo(list.Version + 1);
        await Assert.That((await host.Containers.ListPersonalAsync()).Single(c => c.Id == list.Id).DefaultCategory).IsEqualTo(CapacityCategory.Work);

        var cleared = (await host.Containers.SetDefaultCategoryAsync(new(Op(), list.Id, set.Version, null))).Container;
        await Assert.That(cleared.DefaultCategory).IsNull();

        var inbox = (await host.Containers.ListPersonalAsync()).Single(c => c.IsInbox);
        AppException? error = null;
        try { await host.Containers.SetDefaultCategoryAsync(new(Op(), inbox.Id, inbox.Version, CapacityCategory.Work)); }
        catch (AppException caught) { error = caught; }
        await Assert.That(error).IsNotNull();
        await Assert.That(error!.Kind).IsEqualTo(AppErrorKind.Validation);
    }

    [Test]
    public async Task Capture_UsesExplicitCategory_ThenListDefault_ThenPersonal()
    {
        using var host = await Host.StartAsync();
        var list = (await host.Containers.CreateAsync(new(Op(), "Sprint", ContainerKind.List, null))).Container;
        await host.Containers.SetDefaultCategoryAsync(new(Op(), list.Id, list.Version, CapacityCategory.Work));

        var tasks = await host.CaptureAsync(
            new CaptureTaskDraft { Title = "From list default", ContainerId = list.Id },
            new CaptureTaskDraft { Title = "Explicit personal", ContainerId = list.Id, Category = CapacityCategory.Personal },
            new CaptureTaskDraft { Title = "Inbox default" },
            new CaptureTaskDraft { Title = "Inbox explicit work", Category = CapacityCategory.Work });

        await Assert.That(string.Join(",", tasks.Select(task => task.CapacityCategory))).IsEqualTo("Work,Personal,Personal,Work");
    }
}
