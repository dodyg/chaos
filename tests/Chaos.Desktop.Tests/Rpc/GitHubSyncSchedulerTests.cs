using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using Chaos.Desktop.Platform;
using System.Threading.Channels;

namespace Chaos.Desktop.Tests.Rpc;

public sealed class GitHubSyncSchedulerTests
{
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        public Channel<TimeSpan> Created { get; } = Channel.CreateUnbounded<TimeSpan>();
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(callback, state, _now + dueTime);
                _timers.Add(timer);
                Created.Writer.TryWrite(dueTime);
                return timer;
            }
        }
        public void Advance(TimeSpan time)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += time;
                due = _timers.Where(t => !t.Disposed && t.Due <= _now).ToList();
                foreach (var timer in due) timer.Disposed = true;
            }
            foreach (var timer in due) timer.Fire();
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; } = due;
            public bool Disposed { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() { Disposed = true; GC.SuppressFinalize(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
            public void Fire() => callback(state);
        }
    }

    private sealed class RetryRunner(Action retry) : IGitHubSyncRunner
    {
        public TaskCompletionSource Full { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Retried { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GitHubSyncResult> RunAsync(string? repositoryId, CancellationToken ct, string? expectedConnectionId = null)
        {
            Full.TrySetResult();
            return Task.FromResult(new GitHubSyncResult(0, 0, 0, 0, false));
        }
        public Task<GitHubSyncResult> RunTaskAsync(string taskId, CancellationToken ct)
        {
            retry();
            Retried.TrySetResult();
            return Task.FromResult(new GitHubSyncResult(0, 0, 0, 0, false));
        }
    }

    [Test]
    public async Task Retry_IsScheduledAtLinkDeadlineBeforeNextFullPass()
    {
        var clock = new ManualClock();
        var link = new GitHubIssueLinkRecord { Id = "link", RepositorySubscriptionId = "repo", ConnectionId = "connection", TaskId = "task", IssueId = 1, IssueNodeId = "node", IssueNumber = 1,
            HtmlUrl = "https://github.com/owner/repo/issues/1", BaseJson = "{}", BaseRemoteUpdatedUtc = clock.GetUtcNow(), CreatedAtUtc = clock.GetUtcNow(), UpdatedAtUtc = clock.GetUtcNow(), NextAttemptUtc = clock.GetUtcNow().AddMinutes(1) };
        var pending = true;
        var runner = new RetryRunner(() => pending = false);
        using var engine = new GitHubSyncEngine(runner, clock);
        using var scheduler = new GitHubSyncScheduler(engine, _ => Task.FromResult(new GitHubSchedule(Active(), null, pending ? [link] : [])), clock);
        scheduler.Start();
        await Assert.That(await clock.Created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(10));
        await runner.Full.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(await clock.Created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(TimeSpan.FromSeconds(50));
        clock.Advance(TimeSpan.FromSeconds(49));
        await Assert.That(runner.Retried.Task.IsCompleted).IsFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        await runner.Retried.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task PersistentBackoff_DelaysStartupAndLinkRetriesUntilDeadline()
    {
        var clock = new ManualClock();
        var runner = new RetryRunner(() => { });
        var deadline = clock.GetUtcNow().AddMinutes(2);
        using var engine = new GitHubSyncEngine(runner, clock);
        using var scheduler = new GitHubSyncScheduler(engine, _ => Task.FromResult(new GitHubSchedule(Active(), deadline, [])), clock);
        scheduler.Start();
        await Assert.That(await clock.Created.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(TimeSpan.FromMinutes(2));
        clock.Advance(TimeSpan.FromSeconds(119));
        await Assert.That(runner.Full.Task.IsCompleted).IsFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        await runner.Full.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private sealed class Runner : IGitHubSyncRunner
    {
        public TaskCompletionSource<string?> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GitHubSyncResult> RunAsync(string? repositoryId, CancellationToken ct, string? expectedConnectionId = null)
        {
            Started.TrySetResult(expectedConnectionId);
            return Task.FromResult(new GitHubSyncResult(0, 0, 0, 0, false));
        }
        public Task<GitHubSyncResult> RunTaskAsync(string taskId, CancellationToken ct) => throw new InvalidOperationException();
    }

    [Test]
    public async Task Wake_AfterConnectionAdded_StartsFullPassForThatConnection()
    {
        var inspected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = false;
        var runner = new Runner();
        using var engine = new GitHubSyncEngine(runner, TimeProvider.System);
        using var scheduler = new GitHubSyncScheduler(engine, _ =>
        {
            var result = connected ? Active() : new GitHubStatus(null, []);
            inspected.TrySetResult();
            return Task.FromResult(new GitHubSchedule(result, null, []));
        }, TimeProvider.System);
        scheduler.Start();
        await inspected.Task.WaitAsync(TimeSpan.FromSeconds(10));
        connected = true;
        scheduler.Wake();
        await Assert.That(await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo("connection");
        await engine.WaitForIdleAsync();
    }

    private static GitHubStatus Active()
    {
        var now = DateTimeOffset.UtcNow;
        return new(new GitHubConnectionRecord { Id = "connection", SpaceId = "space", ProfileId = "profile", WebBaseUrl = "https://github.com", ApiBaseUrl = "https://api.github.com", AccountLogin = "user", CreatedAtUtc = now, UpdatedAtUtc = now },
            [new GitHubRepositoryRecord { Id = "repo", ConnectionId = "connection", Owner = "owner", Name = "repo", FullName = "owner/repo", HtmlUrl = "https://github.com/owner/repo", TargetContainerId = "inbox", FilterJson = "{}", CreatedAtUtc = now, UpdatedAtUtc = now }]);
    }
}
