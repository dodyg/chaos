using Chaos.Application.Abstractions;
using Chaos.Application.GitHub;
using Chaos.Desktop.Platform;

namespace Chaos.Desktop.Tests.Rpc;

public sealed class GitHubSyncEngineTests
{
    private sealed class Runner(Func<CancellationToken, Task<GitHubSyncResult>> run) : IGitHubSyncRunner
    {
        public int Calls { get; private set; }
        public Task<GitHubSyncResult> RunAsync(string? repositoryId, CancellationToken ct, string? expectedConnectionId = null) { Calls++; return run(ct); }
        public Task<GitHubSyncResult> RunTaskAsync(string taskId, CancellationToken ct) { Calls++; return run(ct); }
    }
    private static GitHubSyncResult Done() => new(1, 0, 2, 0, false);

    private sealed class ScopedRunner(Func<string, CancellationToken, Task> run) : IGitHubSyncRunner
    {
        public List<string> Calls { get; } = [];
        public async Task<GitHubSyncResult> RunAsync(string? repositoryId, CancellationToken ct, string? expectedConnectionId = null) { Calls.Add("full"); await run("full", ct); return Done(); }
        public async Task<GitHubSyncResult> RunTaskAsync(string taskId, CancellationToken ct) { Calls.Add(taskId); await run(taskId, ct); return Done(); }
    }

    [Test]
    public async Task TaskActions_DuringFullPass_CoalesceAndRunAfterIt()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScopedRunner(async (id, ct) => { if (id == "full") { entered.SetResult(); await release.Task.WaitAsync(ct); } });
        using var engine = new GitHubSyncEngine(runner, TimeProvider.System);
        engine.RequestSync(null, "account");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.RequestTaskSync("a"); engine.RequestTaskSync("a"); engine.RequestTaskSync("b");
        await Assert.That(engine.TaskStatus("a")!.Running).IsTrue();
        release.SetResult(); await engine.WaitForIdleAsync();
        await Assert.That(string.Join(",", runner.Calls)).IsEqualTo("full,a,b");
        await Assert.That(engine.TaskStatus("a")!.Running).IsFalse();
    }

    [Test]
    public async Task TaskActions_DuringSameTaskPass_QueueOneFollowupWithoutLosingOtherTasks()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        var runner = new ScopedRunner(async (_, ct) => { if (first) { first = false; entered.SetResult(); await release.Task.WaitAsync(ct); } });
        using var engine = new GitHubSyncEngine(runner, TimeProvider.System);
        engine.RequestTaskSync("a");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.RequestTaskSync("a"); engine.RequestTaskSync("a"); engine.RequestTaskSync("b");
        release.SetResult(); await engine.WaitForIdleAsync();
        await Assert.That(string.Join(",", runner.Calls)).IsEqualTo("a,a,b");
    }

    [Test]
    public async Task Replacement_CancelsTaskPassAndDropsQueuedWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScopedRunner(async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        using var engine = new GitHubSyncEngine(runner, TimeProvider.System);
        engine.RequestTaskSync("a"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.RequestTaskSync("b");
        await engine.RunPausedAsync(() => { engine.RequestTaskSync("c"); return Task.CompletedTask; }, CancellationToken.None);
        await engine.WaitForIdleAsync();
        await Assert.That(string.Join(",", runner.Calls)).IsEqualTo("a");
        await Assert.That(engine.TaskStatus("b")).IsNull();
    }

    [Test]
    public async Task FullSync_DuringTaskPass_RunsAndRetainsItsOwnStatusAfterQueuedTasks()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new ScopedRunner(async (id, ct) => { if (id == "a") { entered.SetResult(); await release.Task.WaitAsync(ct); } });
        using var engine = new GitHubSyncEngine(runner, TimeProvider.System);
        engine.RequestTaskSync("a"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.RequestSync(null, "account"); engine.RequestTaskSync("b");
        await Assert.That(engine.Status.Running).IsTrue();
        await Assert.That(engine.Status.ConnectionId).IsEqualTo("account");
        release.SetResult(); await engine.WaitForIdleAsync();
        await Assert.That(string.Join(",", runner.Calls)).IsEqualTo("a,full,b");
        await Assert.That(engine.Status.Running).IsFalse();
        await Assert.That(engine.Status.ConnectionId).IsEqualTo("account");
    }

    [Test]
    public async Task CoalescingNeverAcknowledgesADifferentConnection()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = new GitHubSyncEngine(new Runner(async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); return Done(); }), TimeProvider.System);
        engine.RequestSync(null, "connection-one");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(() => engine.RequestSync(null, "connection-two")).Throws<AppException>();
        release.SetResult(); await engine.WaitForIdleAsync();
        await Assert.That(engine.Status.ConnectionId).IsEqualTo("connection-one");
    }

    [Test]
    public async Task ExplicitRequestsReturnImmediatelyAndCoalesce_WithoutStartupWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return Done(); });
        using var engine = new GitHubSyncEngine(runner, TimeProvider.System);
        await Assert.That(runner.Calls).IsEqualTo(0);
        await Assert.That(engine.RequestSync(null).Running).IsTrue();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(engine.RequestSync("another-repo").Running).IsTrue();
        await Assert.That(runner.Calls).IsEqualTo(1);
        release.SetResult(); await engine.WaitForIdleAsync();
        await Assert.That(engine.Status.Result!.Created).IsEqualTo(1);
        await Assert.That(engine.Status.Running).IsFalse();
    }

    [Test]
    public async Task PauseCancelsAndDrainsBeforeReplacement_AndAllowsLaterSync()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Runner(async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return Done(); });
        using var engine = new GitHubSyncEngine(runner, TimeProvider.System);
        engine.RequestSync(null); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await engine.RunPausedAsync(async () =>
        {
            await Assert.That(engine.Status.Running).IsFalse();
            await Assert.That(engine.Status.AppError!.Kind).IsEqualTo(AppErrorKind.Cancelled);
            await Assert.That(() => engine.RequestSync(null)).Throws<AppException>();
        }, CancellationToken.None);
        release.SetResult(); engine.RequestSync(null); await engine.WaitForIdleAsync();
        await Assert.That(engine.Status.Result!.CommentChanges).IsEqualTo(2);
    }

    [Test]
    public async Task UnexpectedFailureIsReportedWithoutPrivateExceptionText()
    {
        using var engine = new GitHubSyncEngine(new Runner(_ => throw new IOException("PRIVATE REMOTE COMMENT")), TimeProvider.System);
        engine.RequestSync(null); await engine.WaitForIdleAsync();
        await Assert.That(engine.Status.AppError!.Message.Contains("PRIVATE", StringComparison.Ordinal)).IsFalse();
        await Assert.That(engine.Status.AppError.Kind).IsEqualTo(AppErrorKind.StorageUnavailable);
    }

    [Test]
    public async Task DisposeCancelsAndDrainsActiveWork()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new GitHubSyncEngine(new Runner(async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); return Done(); }), TimeProvider.System);
        engine.RequestSync(null); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        engine.Dispose();
        await Assert.That(engine.Status.Running).IsFalse();
        await Assert.That(engine.Status.AppError!.Kind).IsEqualTo(AppErrorKind.Cancelled);
        await Assert.That(() => engine.RequestSync(null)).Throws<AppException>();
    }
}
