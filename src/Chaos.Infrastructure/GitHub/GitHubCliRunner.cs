using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.GitHub;

/// <summary>Native process boundary. Returned output is private and must never be logged.</summary>
public interface IGitHubCliRunner
{
    Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct);
}

public interface IGitHubChildProcess : IDisposable
{
    TextReader StandardOutput { get; }
    TextReader StandardError { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    bool Start();
    Task WaitForExitAsync(CancellationToken ct);
    void KillTree();
}

public interface IGitHubProcessFactory
{
    IGitHubChildProcess Create(ProcessStartInfo start);
}

/// <summary>Uses only the standard machine installation, never a repository/PATH executable.</summary>
public sealed class GitHubCliRunner : IGitHubCliRunner
{
    private readonly IGitHubProcessFactory _factory;
    private readonly Func<string, bool> _exists;
    private readonly TimeSpan _timeout;

    public GitHubCliRunner(IGitHubProcessFactory? factory = null, Func<string, bool>? exists = null, TimeSpan? timeout = null)
    {
        _factory = factory ?? new SystemProcessFactory();
        _exists = exists ?? File.Exists;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout < TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe");
        if (!OperatingSystem.IsWindows() || !_exists(executable)) throw new GitHubApiException(GitHubErrorKind.CliUnavailable);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath(), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_HOST", "GH_DEBUG", "GH_CONFIG_DIR", "GH_REPO" })
            start.Environment.Remove(name);
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        start.Environment["GH_TELEMETRY"] = "0";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_timeout);
        using var process = _factory.Create(start);
        var started = false;
        try
        {
            started = process.Start();
            if (!started) throw new GitHubApiException(GitHubErrorKind.CliUnavailable);
            var output = ReadBoundedAsync(process.StandardOutput, timeout, timeout.Token);
            var error = ReadBoundedAsync(process.StandardError, timeout, timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw new GitHubApiException(GitHubErrorKind.Unauthorized);
            return await output;
        }
        catch (Win32Exception) { throw new GitHubApiException(GitHubErrorKind.CliUnavailable); }
        catch (IOException) { throw new GitHubApiException(GitHubErrorKind.CliUnavailable); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new GitHubApiException(GitHubErrorKind.Network); }
        finally
        {
            if (started && !process.HasExited)
            {
                process.KillTree();
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(TextReader reader, CancellationTokenSource timeout, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[512];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (output.Length + read > 16384)
            {
                await timeout.CancelAsync();
                throw new GitHubApiException(GitHubErrorKind.CliUnavailable);
            }
            output.Append(buffer, 0, read);
        }
        return output.ToString();
    }

    private sealed class SystemProcessFactory : IGitHubProcessFactory
    {
        public IGitHubChildProcess Create(ProcessStartInfo start) => new SystemChildProcess(start);
    }

    private sealed class SystemChildProcess(ProcessStartInfo start) : IGitHubChildProcess
    {
        private readonly Process _process = new() { StartInfo = start };
        public TextReader StandardOutput => _process.StandardOutput;
        public TextReader StandardError => _process.StandardError;
        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;
        public bool Start() => _process.Start();
        public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);
        public void KillTree() => _process.Kill(entireProcessTree: true);
        public void Dispose() => _process.Dispose();
    }
}
