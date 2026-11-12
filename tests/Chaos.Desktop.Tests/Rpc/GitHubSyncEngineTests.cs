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
    }
    private static GitHubSyncResult Done() => new(1, 0, 2, 0, false);

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
