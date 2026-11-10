using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>Week plans, goals, assignments, revisions and availability (PLAN §10); migration 009 holds the constraints.</summary>
public sealed class SqliteWeekPlanStore(SqliteConnection connection) : IWeekPlanStore
{
    public async Task<WeekPlanRecord?> GetAsync(string weekPlanId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM WeekPlans WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", weekPlanId);
        return (await ReadPlansAsync(command, ct)).FirstOrDefault();
    }

    public async Task<WeekPlanRecord?> FindByStartAsync(string profileId, DateOnly weekStart, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM WeekPlans WHERE ProfileId = $profile AND WeekStartDate = $start;";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$start", StorageFormats.Date(weekStart));
        return (await ReadPlansAsync(command, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<WeekPlanRecord>> ListOverlappingAsync(string profileId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM WeekPlans
            WHERE ProfileId = $profile AND State <> 2
              AND WeekStartDate <= $to AND date(WeekStartDate, '+6 days') >= $from
            ORDER BY WeekStartDate, Id;
            """;
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$from", StorageFormats.Date(from));
        command.Parameters.AddWithValue("$to", StorageFormats.Date(to));
        return await ReadPlansAsync(command, ct);
    }

    public async Task<bool> TryAddAsync(WeekPlanRecord plan, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WeekPlans (Id, ProfileId, WeekStartDate, State, ConfirmedRevision, ConfirmedAtUtc, DraftPayload, DraftRevision, DraftBaseRevision, Version, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, $profile, $start, $state, $confirmedRevision, $confirmedAt, $draft, $draftRevision, $draftBase, $version, $created, $updated)
            ON CONFLICT (ProfileId, WeekStartDate) DO NOTHING;
            """;
        BindPlan(command, plan);
        return await command.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<WeekPlanRecord?> UpdateAsync(WeekPlanRecord plan, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE WeekPlans SET State = $state, ConfirmedRevision = $confirmedRevision, ConfirmedAtUtc = $confirmedAt,
                DraftPayload = $draft, DraftRevision = $draftRevision, DraftBaseRevision = $draftBase,
                Version = $version, UpdatedAtUtc = $updated
            WHERE Id = $id AND Version = $expected;
            """;
        BindPlan(command, plan);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? plan : null;
    }

    public async Task<IReadOnlyList<WeekGoalRecord>> ListGoalsAsync(string weekPlanId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM WeekGoals WHERE WeekPlanId = $plan ORDER BY Position, Id;";
        command.Parameters.AddWithValue("$plan", weekPlanId);
        var goals = new List<WeekGoalRecord>();
        foreach (var goal in await ReadGoalsAsync(command, ct))
        {
            goals.Add(goal with { TaskIds = await GoalTaskIdsAsync(goal.Id, ct) });
        }

        return goals;
    }

    public async Task<WeekGoalRecord?> GetGoalAsync(string goalId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM WeekGoals WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", goalId);
        var goal = (await ReadGoalsAsync(command, ct)).FirstOrDefault();
        return goal is null ? null : goal with { TaskIds = await GoalTaskIdsAsync(goal.Id, ct) };
    }

    public async Task<WeekGoalRecord?> UpdateGoalOutcomeAsync(WeekGoalRecord goal, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE WeekGoals SET Outcome = $outcome, Version = $version, UpdatedAtUtc = $updated WHERE Id = $id AND Version = $expected;";
        command.Parameters.AddWithValue("$id", goal.Id);
        command.Parameters.AddWithValue("$outcome", (int)goal.Outcome);
        command.Parameters.AddWithValue("$version", goal.Version);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(goal.UpdatedAtUtc));
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? goal : null;
    }

    public async Task<IReadOnlyList<WeekAssignmentRecord>> ListAssignmentsAsync(string weekPlanId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM WeekAssignments WHERE WeekPlanId = $plan ORDER BY LocalDate IS NULL, LocalDate, Position, Id;";
        command.Parameters.AddWithValue("$plan", weekPlanId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<WeekAssignmentRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new WeekAssignmentRecord
            {
                Id = reader.S("Id"),
                WeekPlanId = reader.S("WeekPlanId"),
                TaskId = reader.S("TaskId"),
                LocalDate = reader.Dn("LocalDate"),
                Position = reader.L("Position"),
                Origin = (WeekAssignmentOrigin)reader.L("Origin"),
                CreatedAtUtc = reader.I("CreatedAtUtc"),
                UpdatedAtUtc = reader.I("UpdatedAtUtc"),
            });
        }

        return rows;
    }

    public async Task ReplaceContentAsync(string weekPlanId, IReadOnlyList<WeekGoalRecord> goals, IReadOnlyList<WeekAssignmentRecord> assignments, CancellationToken ct)
    {
        await NonQueryAsync("DELETE FROM WeekGoalTasks WHERE GoalId IN (SELECT Id FROM WeekGoals WHERE WeekPlanId = $plan);", weekPlanId, ct);
        await NonQueryAsync("DELETE FROM WeekGoals WHERE WeekPlanId = $plan;", weekPlanId, ct);
        await NonQueryAsync("DELETE FROM WeekAssignments WHERE WeekPlanId = $plan;", weekPlanId, ct);

        foreach (var goal in goals)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO WeekGoals (Id, WeekPlanId, Title, Position, ProjectId, Outcome, Version, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($id, $plan, $title, $position, $project, $outcome, $version, $created, $updated);
                """;
            insert.Parameters.AddWithValue("$id", goal.Id);
            insert.Parameters.AddWithValue("$plan", weekPlanId);
            insert.Parameters.AddWithValue("$title", goal.Title);
            insert.Parameters.AddWithValue("$position", goal.Position);
            insert.Parameters.AddWithValue("$project", (object?)goal.ProjectId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$outcome", (int)goal.Outcome);
            insert.Parameters.AddWithValue("$version", goal.Version);
            insert.Parameters.AddWithValue("$created", StorageFormats.Instant(goal.CreatedAtUtc));
            insert.Parameters.AddWithValue("$updated", StorageFormats.Instant(goal.UpdatedAtUtc));
            await insert.ExecuteNonQueryAsync(ct);

            foreach (var taskId in goal.TaskIds.Distinct(StringComparer.Ordinal))
            {
                using var link = connection.CreateCommand();
                link.CommandText = "INSERT INTO WeekGoalTasks (GoalId, TaskId) VALUES ($goal, $task);";
                link.Parameters.AddWithValue("$goal", goal.Id);
                link.Parameters.AddWithValue("$task", taskId);
                await link.ExecuteNonQueryAsync(ct);
            }
        }

        foreach (var assignment in assignments)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO WeekAssignments (Id, WeekPlanId, TaskId, LocalDate, Position, Origin, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($id, $plan, $task, $date, $position, $origin, $created, $updated);
                """;
            insert.Parameters.AddWithValue("$id", assignment.Id);
            insert.Parameters.AddWithValue("$plan", weekPlanId);
            insert.Parameters.AddWithValue("$task", assignment.TaskId);
            insert.Parameters.AddWithValue("$date", assignment.LocalDate is { } date ? StorageFormats.Date(date) : DBNull.Value);
            insert.Parameters.AddWithValue("$position", assignment.Position);
            insert.Parameters.AddWithValue("$origin", (int)assignment.Origin);
            insert.Parameters.AddWithValue("$created", StorageFormats.Instant(assignment.CreatedAtUtc));
            insert.Parameters.AddWithValue("$updated", StorageFormats.Instant(assignment.UpdatedAtUtc));
            await insert.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task AddRevisionAsync(string weekPlanId, long revision, string snapshot, DateTimeOffset confirmedAtUtc, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO WeekPlanRevisions (WeekPlanId, Revision, Snapshot, ConfirmedAtUtc) VALUES ($plan, $revision, $snapshot, $at);";
        command.Parameters.AddWithValue("$plan", weekPlanId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$snapshot", snapshot);
        command.Parameters.AddWithValue("$at", StorageFormats.Instant(confirmedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> CountRevisionsAsync(string weekPlanId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM WeekPlanRevisions WHERE WeekPlanId = $plan;";
        command.Parameters.AddWithValue("$plan", weekPlanId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<AvailabilityRecord>> ListRulesAsync(string profileId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Weekday, TotalMinutes, WorkMinutes FROM AvailabilityRules WHERE ProfileId = $profile ORDER BY Weekday;";
        command.Parameters.AddWithValue("$profile", profileId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<AvailabilityRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AvailabilityRecord((DayOfWeek)(int)reader.L("Weekday"), null, (int)reader.L("TotalMinutes"), WorkMinutes(reader)));
        }

        return rows;
    }

    public async Task UpsertRuleAsync(string profileId, DayOfWeek weekday, int totalMinutes, int? workMinutes, DateTimeOffset now, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AvailabilityRules (ProfileId, Weekday, TotalMinutes, WorkMinutes, UpdatedAtUtc)
            VALUES ($profile, $weekday, $total, $work, $updated)
            ON CONFLICT (ProfileId, Weekday) DO UPDATE SET TotalMinutes = excluded.TotalMinutes, WorkMinutes = excluded.WorkMinutes, UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$weekday", (int)weekday);
        command.Parameters.AddWithValue("$total", totalMinutes);
        command.Parameters.AddWithValue("$work", (object?)workMinutes ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(now));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AvailabilityRecord>> ListOverridesAsync(string profileId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT LocalDate, TotalMinutes, WorkMinutes FROM WeekAvailabilityOverrides
            WHERE ProfileId = $profile AND LocalDate BETWEEN $from AND $to ORDER BY LocalDate;
            """;
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$from", StorageFormats.Date(from));
        command.Parameters.AddWithValue("$to", StorageFormats.Date(to));
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<AvailabilityRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AvailabilityRecord(null, reader.Dn("LocalDate"), (int)reader.L("TotalMinutes"), WorkMinutes(reader)));
        }

        return rows;
    }

    public async Task UpsertOverrideAsync(string profileId, DateOnly date, int totalMinutes, int? workMinutes, DateTimeOffset now, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO WeekAvailabilityOverrides (ProfileId, LocalDate, TotalMinutes, WorkMinutes, UpdatedAtUtc)
            VALUES ($profile, $date, $total, $work, $updated)
            ON CONFLICT (ProfileId, LocalDate) DO UPDATE SET TotalMinutes = excluded.TotalMinutes, WorkMinutes = excluded.WorkMinutes, UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$date", StorageFormats.Date(date));
        command.Parameters.AddWithValue("$total", totalMinutes);
        command.Parameters.AddWithValue("$work", (object?)workMinutes ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(now));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteOverrideAsync(string profileId, DateOnly date, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM WeekAvailabilityOverrides WHERE ProfileId = $profile AND LocalDate = $date;";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$date", StorageFormats.Date(date));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static int? WorkMinutes(SqliteDataReader reader) => reader.Ln("WorkMinutes") is { } work ? (int)work : null;

    private async Task NonQueryAsync(string sql, string weekPlanId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$plan", weekPlanId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<IReadOnlyList<string>> GoalTaskIdsAsync(string goalId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TaskId FROM WeekGoalTasks WHERE GoalId = $goal ORDER BY TaskId;";
        command.Parameters.AddWithValue("$goal", goalId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static async Task<IReadOnlyList<WeekGoalRecord>> ReadGoalsAsync(SqliteCommand command, CancellationToken ct)
    {
        using var reader = await command.ExecuteReaderAsync(ct);
        var goals = new List<WeekGoalRecord>();
        while (await reader.ReadAsync(ct))
        {
            goals.Add(new WeekGoalRecord
            {
                Id = reader.S("Id"),
                WeekPlanId = reader.S("WeekPlanId"),
                Title = reader.S("Title"),
                Position = reader.L("Position"),
                ProjectId = reader.Sn("ProjectId"),
                Outcome = (WeekGoalOutcome)reader.L("Outcome"),
                Version = reader.L("Version"),
                CreatedAtUtc = reader.I("CreatedAtUtc"),
                UpdatedAtUtc = reader.I("UpdatedAtUtc"),
            });
        }

        return goals;
    }

    private static async Task<IReadOnlyList<WeekPlanRecord>> ReadPlansAsync(SqliteCommand command, CancellationToken ct)
    {
        using var reader = await command.ExecuteReaderAsync(ct);
        var plans = new List<WeekPlanRecord>();
        while (await reader.ReadAsync(ct))
        {
            plans.Add(new WeekPlanRecord
            {
                Id = reader.S("Id"),
                ProfileId = reader.S("ProfileId"),
                WeekStartDate = reader.Dn("WeekStartDate") ?? throw new InvalidOperationException("A week plan has no start date."),
                State = (WeekPlanState)reader.L("State"),
                ConfirmedRevision = reader.L("ConfirmedRevision"),
                ConfirmedAtUtc = reader.In("ConfirmedAtUtc"),
                DraftPayload = reader.Sn("DraftPayload"),
                DraftRevision = reader.L("DraftRevision"),
                DraftBaseRevision = reader.Ln("DraftBaseRevision"),
                Version = reader.L("Version"),
                CreatedAtUtc = reader.I("CreatedAtUtc"),
                UpdatedAtUtc = reader.I("UpdatedAtUtc"),
            });
        }

        return plans;
    }

    private static void BindPlan(SqliteCommand command, WeekPlanRecord plan)
    {
        command.Parameters.AddWithValue("$id", plan.Id);
        command.Parameters.AddWithValue("$profile", plan.ProfileId);
        command.Parameters.AddWithValue("$start", StorageFormats.Date(plan.WeekStartDate));
        command.Parameters.AddWithValue("$state", (int)plan.State);
        command.Parameters.AddWithValue("$confirmedRevision", plan.ConfirmedRevision);
        command.Parameters.AddWithValue("$confirmedAt", plan.ConfirmedAtUtc is { } at ? StorageFormats.Instant(at) : DBNull.Value);
        command.Parameters.AddWithValue("$draft", (object?)plan.DraftPayload ?? DBNull.Value);
        command.Parameters.AddWithValue("$draftRevision", plan.DraftRevision);
        command.Parameters.AddWithValue("$draftBase", (object?)plan.DraftBaseRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$version", plan.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(plan.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(plan.UpdatedAtUtc));
    }
}
