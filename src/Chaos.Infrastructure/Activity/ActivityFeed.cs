using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Activity;

/// <summary>Reads, cursors and retention of the activity log (PLAN2 §6.4).</summary>
public sealed class ActivityFeed(SqliteApplicationStore store, TimeProvider clock) : IActivityFeed
{
    public const int MaxPage = 500;
    public const int FloorDays = 7;
    public const int HardCapDays = 30;
    public const int HardCapRows = 200_000;
    private const string PrunedKey = "ActivityPrunedThrough";

    public Task<long> HeadAsync(CancellationToken ct) => store.QueryAsync((session, _) => Task.FromResult(Head(Conn(session))), ct);

    public Task<ActivityPage> SinceAsync(long after, int limit, CancellationToken ct) =>
        store.QueryAsync((session, _) =>
        {
            var connection = Conn(session);
            var head = Head(connection);
            var pruned = Scalar(connection, $"SELECT CAST(Value AS INTEGER) FROM DeviceSettings WHERE Key = '{PrunedKey}';");
            if (after < pruned)
            {
                return Task.FromResult(new ActivityPage([], head, true));
            }

            var take = Math.Clamp(limit, 1, MaxPage);
            var entries = new List<ActivityEntry>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT Seq, OperationId, OccurredAtUtc, Source, Action, Outcome, JobRunId, ActorId
                    FROM ActivityLog WHERE Seq > $after ORDER BY Seq LIMIT $take;
                    """;
                command.Parameters.AddWithValue("$after", after);
                command.Parameters.AddWithValue("$take", take);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    entries.Add(new ActivityEntry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), [],
                        reader.IsDBNull(7) ? null : reader.GetString(7)));
                }
            }

            if (entries.Count > 0)
            {
                var bySeq = new Dictionary<long, List<ActivityEntity>>();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Seq, Kind, EntityKey, Op, Version FROM ActivityLogEntities WHERE Seq BETWEEN $from AND $to ORDER BY Seq, Kind, EntityKey;";
                command.Parameters.AddWithValue("$from", entries[0].Seq);
                command.Parameters.AddWithValue("$to", entries[^1].Seq);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var seq = reader.GetInt64(0);
                    if (!bySeq.TryGetValue(seq, out var list))
                    {
                        bySeq[seq] = list = [];
                    }

                    list.Add(new ActivityEntity(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt64(4)));
                }

                for (var index = 0; index < entries.Count; index++)
                {
                    if (bySeq.TryGetValue(entries[index].Seq, out var list))
                    {
                        entries[index] = entries[index] with { Entities = list };
                    }
                }
            }

            return Task.FromResult(new ActivityPage(entries, head, false));
        }, ct);

    public Task<IReadOnlyList<JobState>> JobsAsync(CancellationToken ct) =>
        store.QueryAsync((session, _) =>
        {
            var connection = Conn(session);
            var jobs = new List<JobState>();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT j.Job,
                       (SELECT MIN(r.StartedAtUtc) FROM JobRuns r WHERE r.Job = j.Job AND r.Outcome = 'running'),
                       l.Id, l.StartedAtUtc, l.FinishedAtUtc, l.Outcome, l.Error
                FROM (SELECT DISTINCT Job FROM JobRuns) j
                LEFT JOIN JobRuns l ON l.Id = (SELECT Id FROM JobRuns x WHERE x.Job = j.Job ORDER BY x.StartedAtUtc DESC, x.rowid DESC LIMIT 1)
                ORDER BY j.Job;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                jobs.Add(new JobState(reader.GetString(0), Text(reader, 1), Text(reader, 2), Text(reader, 3), Text(reader, 4), Text(reader, 5), Text(reader, 6)));
            }

            return Task.FromResult<IReadOnlyList<JobState>>(jobs);
        }, ct);

    public Task<long?> GetCursorAsync(string consumer, CancellationToken ct) =>
        store.QueryAsync((session, _) =>
        {
            using var command = Conn(session).CreateCommand();
            command.CommandText = "SELECT Seq FROM ActivityCursors WHERE Consumer = $c;";
            command.Parameters.AddWithValue("$c", consumer);
            return Task.FromResult(command.ExecuteScalar() is long seq ? (long?)seq : null);
        }, ct);

    public Task SetCursorAsync(string consumer, long seq, CancellationToken ct) =>
        store.ExecuteAsync((session, _) =>
        {
            using var command = Conn(session).CreateCommand();
            command.CommandText = """
                INSERT INTO ActivityCursors (Consumer, Seq, UpdatedAtUtc) VALUES ($c, $s, $at)
                ON CONFLICT (Consumer) DO UPDATE SET Seq = excluded.Seq, UpdatedAtUtc = excluded.UpdatedAtUtc;
                """;
            command.Parameters.AddWithValue("$c", consumer);
            command.Parameters.AddWithValue("$s", seq);
            command.Parameters.AddWithValue("$at", StorageFormats.Instant(clock.GetUtcNow()));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);

    /// <summary>
    /// Deletes rows below the lowest cursor (or all rows when no consumer is registered) but keeps
    /// the last <see cref="FloorDays"/> days; then enforces the hard cap (<see cref="HardCapDays"/> days
    /// or <see cref="HardCapRows"/> rows) so a stalled consumer cannot grow the log forever.
    /// Records the pruned-through position so older positions get <c>reset</c>.
    /// </summary>
    public Task<int> PruneAsync(CancellationToken ct) =>
        store.ExecuteAsync((session, _) =>
        {
            var connection = Conn(session);
            var now = clock.GetUtcNow();
            var floor = StorageFormats.Instant(now.AddDays(-FloorDays));
            var hard = StorageFormats.Instant(now.AddDays(-HardCapDays));
            var head = Head(connection);
            var minCursor = Scalar(connection, "SELECT MIN(Seq) FROM ActivityCursors;", defaultValue: long.MaxValue);

            long Highest(string where, params (string, object)[] parameters)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COALESCE(MAX(Seq), 0) FROM ActivityLog WHERE {where};";
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            var soft = Highest("OccurredAtUtc < $floor AND Seq <= $cursor", ("$floor", floor), ("$cursor", minCursor == long.MaxValue ? head : minCursor));
            var capByAge = Highest("OccurredAtUtc < $hard", ("$hard", hard));
            var capByRows = head - HardCapRows;
            var through = Math.Max(soft, Math.Max(capByAge, capByRows));
            if (through <= 0)
            {
                return Task.FromResult(0);
            }

            int removed;
            using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM ActivityLog WHERE Seq <= $through;";
                delete.Parameters.AddWithValue("$through", through);
                removed = delete.ExecuteNonQuery();
            }

            using (var mark = connection.CreateCommand())
            {
                mark.CommandText = $"""
                    INSERT INTO DeviceSettings (Key, Value) VALUES ('{PrunedKey}', $v)
                    ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value WHERE CAST(Value AS INTEGER) < CAST(excluded.Value AS INTEGER);
                    """;
                mark.Parameters.AddWithValue("$v", through.ToString(CultureInfo.InvariantCulture));
                mark.ExecuteNonQuery();
            }

            return Task.FromResult(removed);
        }, ct);

    private static SqliteConnection Conn(IDataSession session) => session.Connection();

    private static long Head(SqliteConnection connection) => Scalar(connection, "SELECT COALESCE(MAX(Seq), 0) FROM ActivityLog;");

    private static long Scalar(SqliteConnection connection, string sql, long defaultValue = 0)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? defaultValue : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
