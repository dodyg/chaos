using Chaos.Application.Abstractions;

namespace Chaos.Application.Activity;

public enum ActivityOp { Upsert, Delete }

/// <summary>
/// Device-only writers (LocalOnly tables have no triggers) note what they changed inside their
/// own transaction (PLAN2 §6.4 rule 3). Churning caches note one summary entity, not every row.
/// </summary>
public interface IActivityRecorder
{
    void Note(string kind, string entityKey, ActivityOp op, long? version = null);
}

public sealed class NullActivityRecorder : IActivityRecorder
{
    public static readonly NullActivityRecorder Instance = new();
    public void Note(string kind, string entityKey, ActivityOp op, long? version = null) { }
}

/// <summary>Told after commit that the log head moved; the Desktop host coalesces and pushes it.</summary>
public interface IActivityNotifier
{
    void HeadChanged(long seq);

    /// <summary>A job run started or finished (sync indicators watch JobRuns); no data changed.</summary>
    void JobsChanged();
}

public sealed record ActivityEntity(string Kind, string Key, string Op, long? Version);

public sealed record ActivityEntry(
    long Seq, string OperationId, string OccurredAtUtc, string Source, string Action, string Outcome, string? JobRunId,
    IReadOnlyList<ActivityEntity> Entities);

public sealed record ActivityPage(IReadOnlyList<ActivityEntry> Entries, long Head, bool Reset);

public sealed record JobState(string Job, string? RunningSinceUtc, string? LastRunId, string? LastStartedUtc, string? LastFinishedUtc, string? LastOutcome, string? LastError);

/// <summary>Reads of the activity log (PLAN2 §6.4): in order, bounded, with reset when the position was pruned.</summary>
public interface IActivityFeed
{
    Task<long> HeadAsync(CancellationToken ct);
    Task<ActivityPage> SinceAsync(long after, int limit, CancellationToken ct);
    Task<IReadOnlyList<JobState>> JobsAsync(CancellationToken ct);
    /// <summary>The stored position of a host-side consumer, or null.</summary>
    Task<long?> GetCursorAsync(string consumer, CancellationToken ct);
    Task SetCursorAsync(string consumer, long seq, CancellationToken ct);
    /// <summary>Applies the retention rules; returns rows removed.</summary>
    Task<int> PruneAsync(CancellationToken ct);
}

public enum JobOutcome { Ok, Noop, Failed }

/// <summary>One entry per scheduled pass, including passes that do nothing (PLAN2 §6.4).</summary>
public interface IJobRuns
{
    Task<string> StartAsync(string job, CancellationToken ct = default);
    Task FinishAsync(string runId, JobOutcome outcome, string? error = null, CancellationToken ct = default);
}

/// <summary>
/// Wraps one scheduled pass: a JobRuns entry plus a scope that stamps every write the pass makes.
/// The outcome is Noop when the pass wrote no activity and Ok otherwise; an exception is Failed
/// (and rethrown). Failure to record the run never fails the pass.
/// </summary>
public static class JobPass
{
    public static async Task RunAsync(IJobRuns? runs, string source, string action, Func<CancellationToken, Task> pass, CancellationToken ct)
    {
        string? runId = null;
        if (runs is not null)
        {
            try { runId = await runs.StartAsync(action, CancellationToken.None).ConfigureAwait(false); } catch (Exception) when (!ct.IsCancellationRequested) { }
        }

        using var scope = ActivityScope.Open(source, action, jobRunId: runId);
        try
        {
            await pass(ct).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (runs is not null && runId is not null)
            {
                try { await runs.FinishAsync(runId, JobOutcome.Failed, error.Message, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            }

            throw;
        }

        if (runs is not null && runId is not null)
        {
            try { await runs.FinishAsync(runId, JobOutcome.Ok, null, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
        }
    }
}
