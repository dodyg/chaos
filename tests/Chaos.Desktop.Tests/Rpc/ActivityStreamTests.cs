using System.Reflection;
using System.Text.RegularExpressions;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.GitHub;
using Chaos.Application.Tasks;
using Chaos.Desktop.Platform;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Activity;
using Chaos.Infrastructure.Bootstrap;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>
/// The activity stream at the desktop host (PLAN2 §6.4, P6a): every RPC method is a listed action and opens a
/// scope, a scheduler pass records a job run and stamps its writes, and what a pass changed reaches a client
/// through <c>activity.since</c>; head notices coalesce into one pushed event.
/// </summary>
public sealed class ActivityStreamTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static async Task<ChaosStorageHandle> StartAsync() =>
        await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", new SequentialIdGenerator(), new FixedTimeProvider(Now), CancellationToken.None);

    [Test]
    public async Task Every_rpc_method_is_a_listed_action()
    {
        var methods = typeof(ActivityRpcService).Assembly.GetTypes()
            .Select(type => (Type: type, Service: type.GetCustomAttribute<NeoRpcServiceAttribute>()))
            .Where(item => item.Service is not null)
            .SelectMany(item => item.Type.GetMethods().Select(method => (Service: item.Service!.Name, Method: method.GetCustomAttribute<NeoRpcMethodAttribute>())).Where(m => m.Method is not null)
                .Select(m => $"{m.Service}.{m.Method!.Name}"))
            .ToList();

        await Assert.That(methods.Count).IsGreaterThan(200);
        var missing = methods.Where(action => !ActivityActions.IsKnown(action)).ToList();
        await Assert.That(missing.Count).IsEqualTo(0);
        var listedButGone = ActivityActions.Rpc.Except(methods).ToList();
        await Assert.That(listedButGone.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Every_rpc_method_opens_an_activity_scope()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "src", "Chaos.Desktop", "Services")))
        {
            root = Path.GetDirectoryName(root);
        }

        await Assert.That(root).IsNotNull();
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(root!, "src", "Chaos.Desktop", "Services"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            var starts = Regex.Matches(text, @"\[NeoRpcMethod\(""([^""]+)""").ToList();
            for (var index = 0; index < starts.Count; index++)
            {
                var end = index + 1 < starts.Count ? starts[index + 1].Index : text.Length;
                var body = text[starts[index].Index..end];
                if (!body.Contains("ActivityScope.Rpc(") && !body.Contains("ActivityScope.Open("))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {starts[index].Groups[1].Value}");
                }
            }
        }

        await Assert.That(offenders.Count).IsEqualTo(0);
    }

    private sealed class WritingRunner(TaskCommands tasks) : IGitHubSyncRunner
    {
        public async Task<GitHubSyncResult> RunAsync(string? repositoryId, CancellationToken ct, string? expectedConnectionId = null)
        {
            await tasks.CaptureAsync(new CaptureTaskCommand(Guid.NewGuid().ToString(), string.Empty, "From a sync pass", null), ct);
            return new GitHubSyncResult(1, 0, 0, 0, false);
        }

        public Task<GitHubSyncResult> RunTaskAsync(string taskId, CancellationToken ct) => Task.FromResult(new GitHubSyncResult(0, 0, 0, 0, false));
    }

    [Test]
    public async Task A_scheduler_pass_records_a_job_run_and_its_change_reaches_a_client_through_since()
    {
        using var storage = await StartAsync();
        using var strict = ActivityScope.UseStrict();
        var clock = new FixedTimeProvider(Now);
        var feed = new ActivityFeed(storage.Store, clock);
        var runs = new JobRuns(storage.Store, clock);
        var service = new ActivityRpcService(feed);
        var tasks = new TaskCommands(storage.Store, new SequentialIdGenerator(), clock);
        var head = (await service.HeadAsync(new(), CancellationToken.None)).Head;

        using var engine = new GitHubSyncEngine(new WritingRunner(tasks), clock, default, runs);
        engine.RequestSync(null);
        await engine.WaitForIdleAsync();

        var since = await service.SinceAsync(new(head, 50), CancellationToken.None);
        var entry = since.Entries.Single();
        await Assert.That(entry.Source).IsEqualTo("sync");
        await Assert.That(entry.Action).IsEqualTo("github.sync");
        await Assert.That(entry.JobRunId).IsNotNull();
        await Assert.That(entry.Entities.Any(entity => entity.Kind == "task")).IsTrue();

        var jobs = await service.JobsAsync(new(), CancellationToken.None);
        var job = jobs.Jobs.Single(candidate => candidate.Job == "github.sync");
        await Assert.That(job.Running).IsFalse();
        await Assert.That(job.LastOutcome).IsEqualTo("ok");
        await Assert.That(job.LastRunId).IsEqualTo(entry.JobRunId);
    }

    [Test]
    public async Task A_pass_that_changes_nothing_is_a_noop_run_without_an_activity_row()
    {
        using var storage = await StartAsync();
        var clock = new FixedTimeProvider(Now);
        var feed = new ActivityFeed(storage.Store, clock);
        var runs = new JobRuns(storage.Store, clock);
        var before = await feed.HeadAsync(CancellationToken.None);

        await JobPass.RunAsync(runs, ActivitySources.Schedule, ActivityActions.CalendarRefresh, _ => Task.CompletedTask, CancellationToken.None);

        await Assert.That(await feed.HeadAsync(CancellationToken.None)).IsEqualTo(before);
        var job = (await feed.JobsAsync(CancellationToken.None)).Single(candidate => candidate.Job == "calendar.refresh");
        await Assert.That(job.LastOutcome).IsEqualTo("noop");
    }

    [Test]
    public async Task Head_notices_coalesce_into_one_pushed_event()
    {
        using var publisher = new ActivityPublisher(TimeProvider.System);
        var published = new List<string>();
        publisher.Attach(payload => { lock (published) { published.Add(payload.Seq); } return Task.CompletedTask; });

        publisher.HeadChanged(3);
        publisher.HeadChanged(4);
        publisher.JobsChanged();
        publisher.HeadChanged(5);
        await Task.Delay(400);

        string[] seen;
        lock (published)
        {
            seen = [.. published];
        }

        await Assert.That(seen.Length).IsEqualTo(1);
        await Assert.That(seen[0]).IsEqualTo("5");
    }
}
