using System.Diagnostics;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.GitHub;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubCliRunnerTests
{
    private sealed class Child(string output = "fixture-a", bool hang = false) : IGitHubChildProcess
    {
        public TextReader StandardOutput { get; } = new StringReader(output);
        public TextReader StandardError { get; } = new StringReader("fixture diagnostic, never exposed");
        public bool HasExited { get; private set; }
        public int ExitCode { get; init; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public Action? Started { get; init; }
        public bool Start() { Started?.Invoke(); return true; }
        public Task WaitForExitAsync(CancellationToken ct)
        {
            if (!hang || HasExited) { HasExited = true; return Task.CompletedTask; }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => completion.TrySetCanceled(ct));
            return completion.Task;
        }
        public void KillTree() { Killed = true; HasExited = true; }
        public void Dispose() { Disposed = true; StandardOutput.Dispose(); StandardError.Dispose(); }
    }

    private sealed class Factory(Child child) : IGitHubProcessFactory
    {
        public ProcessStartInfo? Start { get; private set; }
        public IGitHubChildProcess Create(ProcessStartInfo start) { Start = start; return child; }
    }

    [Test]
    public async Task RunAsync_StructuredArguments_RemovesOverridesAndNeverUsesShell()
    {
        var child = new Child();
        var factory = new Factory(child);
        var runner = new GitHubCliRunner(factory, _ => true);
        await Assert.That(await runner.RunAsync(["auth", "token", "--user=person"], CancellationToken.None)).IsEqualTo("fixture-a");
        await Assert.That(factory.Start!.UseShellExecute).IsFalse();
        await Assert.That(factory.Start.CreateNoWindow).IsTrue();
        await Assert.That(factory.Start.ArgumentList.SequenceEqual(new[] { "auth", "token", "--user=person" })).IsTrue();
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_DEBUG", "GH_HOST", "GH_CONFIG_DIR" })
            await Assert.That(factory.Start.Environment.ContainsKey(name)).IsFalse();
        await Assert.That(Path.GetDirectoryName(factory.Start.FileName)!.EndsWith("GitHub CLI", StringComparison.Ordinal)).IsTrue();
        await Assert.That(child.Disposed).IsTrue();
    }

    [Test]
    public async Task RunAsync_MissingExecutable_DoesNotCreateProcess()
    {
        var factory = new Factory(new Child());
        await Assert.That(async () => await new GitHubCliRunner(factory, _ => false).RunAsync(["--version"], CancellationToken.None)).Throws<GitHubApiException>();
        await Assert.That(factory.Start).IsNull();
    }

    [Test]
    public async Task RunAsync_OutputLimit_KillsAndDisposesChild()
    {
        var child = new Child(new string('a', 16385), hang: true);
        await Assert.That(async () => await new GitHubCliRunner(new Factory(child), _ => true).RunAsync(["--version"], CancellationToken.None)).Throws<GitHubApiException>();
        await Assert.That(child.Killed).IsTrue();
        await Assert.That(child.Disposed).IsTrue();
    }

    [Test]
    public async Task RunAsync_Timeout_KillsAndDisposesChild()
    {
        var child = new Child(hang: true);
        await Assert.That(async () => await new GitHubCliRunner(new Factory(child), _ => true, TimeSpan.Zero).RunAsync(["--version"], CancellationToken.None)).Throws<GitHubApiException>();
        await Assert.That(child.Killed).IsTrue();
        await Assert.That(child.Disposed).IsTrue();
    }

    [Test]
    public async Task RunAsync_CanceledDuringExecution_KillsChildAndPreservesCancellation()
    {
        using var canceled = new CancellationTokenSource();
        var child = new Child(hang: true) { Started = canceled.Cancel };
        await Assert.That(async () => await new GitHubCliRunner(new Factory(child), _ => true).RunAsync(["--version"], canceled.Token)).Throws<OperationCanceledException>();
        await Assert.That(child.Killed).IsTrue();
        await Assert.That(child.Disposed).IsTrue();
    }

    [Test]
    public async Task RunAsync_CanceledBeforeStart_DoesNotCreateProcess()
    {
        var factory = new Factory(new Child());
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.That(async () => await new GitHubCliRunner(factory, _ => true).RunAsync(["--version"], canceled.Token)).Throws<OperationCanceledException>();
        await Assert.That(factory.Start).IsNull();
    }
}
