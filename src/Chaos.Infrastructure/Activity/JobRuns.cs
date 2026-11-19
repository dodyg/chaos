using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Activity;

/// <summary>
/// The JobRuns table (PLAN2 §6.4): one row per scheduled pass, kept apart from the event stream so
/// a 30 s tick adds no ActivityLog rows. Finishing a run counts the activity rows stamped with its
/// id (Noop when none), keeps the last <see cref="KeepPerJob"/> runs per job, and records a failed
/// pass as an ActivityLog row with Outcome=failed and no entities.
/// </summary>
public sealed class JobRuns(SqliteApplicationStore store, TimeProvider clock) : IJobRuns
{
    public const int KeepPerJob = 200;

    public async Task<string> StartAsync(string job, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("D");
        await store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "INSERT INTO JobRuns (Id, Job, StartedAtUtc, Outcome) VALUES ($id, $job, $at, 'running');";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$job", job);
            command.Parameters.AddWithValue("$at", StorageFormats.Instant(clock.GetUtcNow()));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct).ConfigureAwait(false);
        Notify(null);
        return id;
    }

    public async Task FinishAsync(string runId, JobOutcome outcome, string? error = null, CancellationToken ct = default)
    {
        long? failedSeq = null;
        await store.ExecuteAsync((session, _) =>
        {
            var connection = session.Connection();
            var now = StorageFormats.Instant(clock.GetUtcNow());
            string? job;
            using (var find = connection.CreateCommand())
            {
                find.CommandText = "SELECT Job FROM JobRuns WHERE Id = $id;";
                find.Parameters.AddWithValue("$id", runId);
                job = find.ExecuteScalar() as string;
            }

            if (job is null)
            {
                return Task.FromResult(0);
            }

            long activity;
            using (var count = connection.CreateCommand())
            {
                count.CommandText = "SELECT COUNT(*) FROM ActivityLog WHERE JobRunId = $id;";
                count.Parameters.AddWithValue("$id", runId);
                activity = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            var final = outcome == JobOutcome.Failed ? "failed" : activity == 0 ? "noop" : "ok";
            if (outcome == JobOutcome.Failed)
            {
                using var failed = connection.CreateCommand();
                failed.CommandText = """
                    INSERT INTO ActivityLog (OperationId, OccurredAtUtc, Source, Action, Outcome, JobRunId)
                    VALUES ($op, $at, $source, $action, 'failed', $run);
                    SELECT last_insert_rowid();
                    """;
                failed.Parameters.AddWithValue("$op", Guid.NewGuid().ToString("D"));
                failed.Parameters.AddWithValue("$at", now);
                failed.Parameters.AddWithValue("$source", ActivityScope.Current?.Source ?? ActivitySources.System);
                failed.Parameters.AddWithValue("$action", job);
                failed.Parameters.AddWithValue("$run", runId);
                failedSeq = Convert.ToInt64(failed.ExecuteScalar(), CultureInfo.InvariantCulture);
                activity++;
            }

            using (var update = connection.CreateCommand())
            {
                update.CommandText = "UPDATE JobRuns SET FinishedAtUtc = $at, Outcome = $outcome, Error = $error, ActivityCount = $count WHERE Id = $id;";
                update.Parameters.AddWithValue("$at", now);
                update.Parameters.AddWithValue("$outcome", final);
                update.Parameters.AddWithValue("$error", (object?)Truncate(error) ?? DBNull.Value);
                update.Parameters.AddWithValue("$count", activity);
                update.Parameters.AddWithValue("$id", runId);
                update.ExecuteNonQuery();
            }

            using var prune = connection.CreateCommand();
            prune.CommandText = """
                DELETE FROM JobRuns WHERE Job = $job AND Outcome <> 'running' AND Id NOT IN
                    (SELECT Id FROM JobRuns WHERE Job = $job ORDER BY StartedAtUtc DESC, rowid DESC LIMIT $keep);
                """;
            prune.Parameters.AddWithValue("$job", job);
            prune.Parameters.AddWithValue("$keep", KeepPerJob);
            prune.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct).ConfigureAwait(false);

        Notify(failedSeq);
    }

    private void Notify(long? seq)
    {
        try
        {
            if (seq is { } head)
            {
                store.ActivityNotifier?.HeadChanged(head);
            }

            store.ActivityNotifier?.JobsChanged();
        }
        catch (Exception error)
        {
            // A notifier failure never fails the pass.
            System.Diagnostics.Trace.TraceWarning("Activity notifier failed after a job change: {0}", error.GetType().Name);
        }
    }

    private static string? Truncate(string? error) => error is { Length: > 500 } ? error[..500] : error;
}
