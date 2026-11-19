using System.Globalization;
using System.Text.Json.Nodes;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.DataProtection;

/// <summary>What a purge would remove (or removed): deleted items only, never active ones.</summary>
public sealed record PurgeCounts(long Tasks, long Subtasks, long DiaryEntries, long PadItems, long Lists, long Sections, long Tags, long Templates, long Pages = 0, long Attachments = 0)
{
    public long Total => Tasks + Subtasks + DiaryEntries + PadItems + Lists + Sections + Tags + Templates + Pages + Attachments;
}

public sealed record PurgeOutcome(PurgeCounts Removed, IReadOnlyList<string> OsNotificationIds);

/// <summary>
/// Permanent deletion of items already in the trash (PLAN §14), separate from reversible
/// delete. Everything that belongs only to a purged item goes with it in one transaction:
/// subtasks, tag links, diary entries and their revisions, history, search documents,
/// reminders, follow-ups, notification jobs, plan membership, pad links, import mappings and
/// undo entries that mention it. Stored summaries keep their counts but the purged task's
/// title is replaced. Lists, sections, tags and recurrence series that are deleted are removed
/// only once nothing refers to them. The database is then compacted so the removed text does
/// not stay in free pages. Backups made earlier still contain the items until they rotate out.
/// </summary>
public sealed class DataPurge(SqliteApplicationStore store, Chaos.Application.Attachments.IAttachmentFileStore? files = null)
{
    public const string PurgedTitle = "Permanently deleted task";

    public Task<PurgeCounts> PreviewAsync(string? taskId, CancellationToken ct) =>
        store.QueryAsync(async (session, token) =>
        {
            var connection = session.Connection();
            using var transaction = connection.BeginTransaction(deferred: true);
            Select(connection, taskId);
            var counts = Count(connection, taskId is null);
            transaction.Rollback();
            return await Task.FromResult(counts);
        }, ct);

    /// <summary>Active data counts of the live database, to compare with a backup before restoring.</summary>
    public Task<DataCounts> CurrentCountsAsync(CancellationToken ct) =>
        store.QueryAsync((session, _) => Task.FromResult(BackupManager.ReadCounts(session.Connection())), ct);

    /// <summary>Purges every deleted item, or one deleted task when <paramref name="taskId"/> is given.</summary>
    public async Task<PurgeOutcome> PurgeAsync(string? taskId, CancellationToken ct)
    {
        IReadOnlyList<string> orphanedHashes = [];
        var outcome = await store.ExecuteAsync(async (session, token) =>
        {
            var connection = session.Connection();
            if (taskId is not null)
            {
                using var check = connection.CreateCommand();
                check.CommandText = "SELECT DeletedAtUtc IS NOT NULL FROM Tasks WHERE Id = $id;";
                check.Parameters.AddWithValue("$id", taskId);
                if (check.ExecuteScalar() is not long deleted)
                {
                    throw AppException.NotFound("That task no longer exists.");
                }

                if (deleted == 0)
                {
                    throw AppException.Validation("taskId", "Only deleted tasks can be removed permanently. Delete it first.");
                }
            }

            Select(connection, taskId);
            var counts = Count(connection, taskId is null);
            var osIds = Strings(connection, "SELECT OsNotificationId FROM NotificationJobs WHERE OsNotificationId IS NOT NULL AND TaskId IN (SELECT Id FROM temp.PurgeTasks);");
            var attachmentHashes = Strings(connection, "SELECT DISTINCT Sha256 FROM Attachments WHERE Id IN (SELECT Id FROM temp.PurgeAttachments);");
            ScrubSummaries(connection);
            DeleteSelected(connection);
            orphanedHashes = ForgetUnreferencedHashes(connection, attachmentHashes);
            if (taskId is null)
            {
                DeleteUnreferencedContainersAndTags(connection);
            }

            return await Task.FromResult(new PurgeOutcome(counts, osIds));
        }, ct);

        // Compact so purged text is not left in free pages or the WAL.
        await store.RunExclusiveAsync<object?>(token =>
        {
            using var connection = BackupManager.OpenFile(store.Source.DatabasePath, SqliteOpenMode.ReadWrite);
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            Execute(connection, "VACUUM;");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            return Task.FromResult<object?>(null);
        }, ct);

        // The rows are gone for good, so bytes nothing else names go too. (The backups keep their own copies.)
        if (files is not null)
        {
            foreach (var hash in orphanedHashes)
            {
                await files.DeleteAsync(hash, ct);
            }

            // Emptying the trash also clears files an image edit left behind (kept for undo) once they are an hour old.
            if (taskId is null)
            {
                await SweepUnreferencedFilesAsync(TimeSpan.FromHours(1), ct);
            }
        }

        return outcome;
    }

    /// <summary>Removes stored files older than <paramref name="minAge"/> that no attachment row names. Returns how many went.</summary>
    public async Task<int> SweepUnreferencedFilesAsync(TimeSpan minAge, CancellationToken ct)
    {
        if (files is null)
        {
            return 0;
        }

        var referenced = await store.QueryAsync(async (session, token) =>
        {
            var connection = session.Connection();
            return Strings(connection, "SELECT DISTINCT Sha256 FROM Attachments;").ToHashSet(StringComparer.Ordinal);
        }, ct);
        var stale = new List<string>();
        var cutoff = DateTimeOffset.UtcNow - minAge;
        await foreach (var blob in files.ListAsync(ct))
        {
            if (!referenced.Contains(blob.Sha256) && blob.ModifiedUtc <= cutoff)
            {
                stale.Add(blob.Sha256);
            }
        }

        foreach (var hash in stale)
        {
            await files.DeleteAsync(hash, ct);
        }

        return stale.Count;
    }

    /// <summary>Drops this device's byte state for hashes no attachment names any more; returns those hashes, whose files may now go.</summary>
    private static IReadOnlyList<string> ForgetUnreferencedHashes(SqliteConnection connection, IReadOnlyList<string> candidates)
    {
        var orphans = new List<string>();
        foreach (var hash in candidates)
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT EXISTS (SELECT 1 FROM Attachments WHERE Sha256 = $sha);";
            check.Parameters.AddWithValue("$sha", hash);
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                using var forget = connection.CreateCommand();
                forget.CommandText = "DELETE FROM AttachmentLocalState WHERE Sha256 = $sha;";
                forget.Parameters.AddWithValue("$sha", hash);
                forget.ExecuteNonQuery();
                orphans.Add(hash);
            }
        }

        return orphans;
    }

    /// <summary>Fills temp tables with the IDs to purge.</summary>
    private static void Select(SqliteConnection connection, string? taskId)
    {
        Execute(connection, """
            CREATE TEMP TABLE IF NOT EXISTS PurgeTasks (Id TEXT PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS PurgeSubtasks (Id TEXT PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS PurgeDiary (Id TEXT PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS PurgePad (Id TEXT PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS PurgePages (Id TEXT PRIMARY KEY);
            CREATE TEMP TABLE IF NOT EXISTS PurgeAttachments (Id TEXT PRIMARY KEY);
            DELETE FROM temp.PurgeTasks; DELETE FROM temp.PurgeSubtasks; DELETE FROM temp.PurgeDiary; DELETE FROM temp.PurgePad; DELETE FROM temp.PurgePages; DELETE FROM temp.PurgeAttachments;
            """);

        using var tasks = connection.CreateCommand();
        tasks.CommandText = "INSERT INTO temp.PurgeTasks SELECT Id FROM Tasks WHERE DeletedAtUtc IS NOT NULL AND ($id IS NULL OR Id = $id);";
        tasks.Parameters.AddWithValue("$id", (object?)taskId ?? DBNull.Value);
        tasks.ExecuteNonQuery();

        var all = taskId is null ? 1L : 0L;
        using var rest = connection.CreateCommand();
        rest.CommandText = """
            INSERT INTO temp.PurgeSubtasks SELECT Id FROM Subtasks WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks) OR ($all = 1 AND DeletedAtUtc IS NOT NULL);
            INSERT INTO temp.PurgeDiary SELECT Id FROM DiaryEntries WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks) OR ($all = 1 AND DeletedAtUtc IS NOT NULL);
            INSERT INTO temp.PurgePad SELECT Id FROM ScratchPadItems WHERE $all = 1 AND DeletedAtUtc IS NOT NULL;
            INSERT INTO temp.PurgePages SELECT Id FROM Pages WHERE $all = 1 AND DeletedAtUtc IS NOT NULL;
            INSERT INTO temp.PurgeAttachments SELECT Id FROM Attachments
                WHERE ($all = 1 AND DeletedAtUtc IS NOT NULL)
                   OR (OwnerKind = 1 AND OwnerId IN (SELECT Id FROM temp.PurgeTasks))
                   OR (OwnerKind = 2 AND OwnerId IN (SELECT Id FROM temp.PurgeDiary))
                   OR (OwnerKind = 3 AND OwnerId IN (SELECT Id FROM temp.PurgePad))
                   OR (OwnerKind = 4 AND OwnerId IN (SELECT Id FROM temp.PurgePages));
            """;
        rest.Parameters.AddWithValue("$all", all);
        rest.ExecuteNonQuery();
    }

    private static PurgeCounts Count(SqliteConnection connection, bool all) => new(
        Scalar(connection, "SELECT count(*) FROM temp.PurgeTasks;"),
        Scalar(connection, "SELECT count(*) FROM temp.PurgeSubtasks;"),
        Scalar(connection, "SELECT count(*) FROM temp.PurgeDiary;"),
        Scalar(connection, "SELECT count(*) FROM temp.PurgePad;"),
        !all ? 0 : Scalar(connection, "SELECT count(*) FROM Containers WHERE DeletedAtUtc IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Tasks t WHERE t.ContainerId = Containers.Id AND t.Id NOT IN (SELECT Id FROM temp.PurgeTasks)) AND NOT EXISTS (SELECT 1 FROM GitHubRepositories r WHERE r.TargetContainerId = Containers.Id) AND NOT EXISTS (SELECT 1 FROM GitHubMilestoneProjects m WHERE m.ContainerId = Containers.Id);"),
        !all ? 0 : Scalar(connection, "SELECT count(*) FROM Sections WHERE DeletedAtUtc IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Tasks t WHERE t.SectionId = Sections.Id AND t.Id NOT IN (SELECT Id FROM temp.PurgeTasks)) AND NOT EXISTS (SELECT 1 FROM GitHubRepositories r WHERE r.TargetSectionId = Sections.Id);"),
        !all ? 0 : Scalar(connection, "SELECT count(*) FROM Tags WHERE DeletedAtUtc IS NOT NULL;"),
        !all ? 0 : Scalar(connection, "SELECT count(*) FROM TaskTemplates WHERE DeletedAtUtc IS NOT NULL;"),
        Scalar(connection, "SELECT count(*) FROM temp.PurgePages;"),
        Scalar(connection, "SELECT count(*) FROM temp.PurgeAttachments;"));

    private static void DeleteSelected(SqliteConnection connection)
    {
        // Undo entries that mention any purged item can no longer be applied.
        var ids = Strings(connection, "SELECT Id FROM temp.PurgeTasks UNION ALL SELECT Id FROM temp.PurgeSubtasks UNION ALL SELECT Id FROM temp.PurgeDiary UNION ALL SELECT Id FROM temp.PurgePad UNION ALL SELECT Id FROM temp.PurgePages UNION ALL SELECT Id FROM temp.PurgeAttachments;");
        foreach (var id in ids)
        {
            using var undo = connection.CreateCommand();
            undo.CommandText = "DELETE FROM UndoEntries WHERE instr(InversePayload, $id) > 0 OR instr(ExpectedVersions, $id) > 0;";
            undo.Parameters.AddWithValue("$id", id);
            undo.ExecuteNonQuery();
        }

        Execute(connection, "DELETE FROM Attachments WHERE Id IN (SELECT Id FROM temp.PurgeAttachments);");
        Execute(connection, """
            DELETE FROM SearchIndex WHERE rowid IN (
                SELECT RowId FROM SearchDocuments
                WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks)
                   OR (TargetKind = 2 AND TargetId IN (SELECT Id FROM temp.PurgeSubtasks))
                   OR (TargetKind = 3 AND TargetId IN (SELECT Id FROM temp.PurgeDiary))
                   OR (TargetKind = 4 AND TargetId IN (SELECT Id FROM temp.PurgePad))
                   OR (TargetKind = 5 AND TargetId IN (SELECT Id FROM temp.PurgePages)));
            DELETE FROM SearchDocuments
                WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks)
                   OR (TargetKind = 2 AND TargetId IN (SELECT Id FROM temp.PurgeSubtasks))
                   OR (TargetKind = 3 AND TargetId IN (SELECT Id FROM temp.PurgeDiary))
                   OR (TargetKind = 4 AND TargetId IN (SELECT Id FROM temp.PurgePad))
                   OR (TargetKind = 5 AND TargetId IN (SELECT Id FROM temp.PurgePages));
            DELETE FROM SubtaskTags WHERE SubtaskId IN (SELECT Id FROM temp.PurgeSubtasks);
            DELETE FROM GitHubChecklistItems WHERE SubtaskId IN (SELECT Id FROM temp.PurgeSubtasks);
            DELETE FROM GitHubCommentLinks WHERE DiaryEntryId IN (SELECT Id FROM temp.PurgeDiary);
            DELETE FROM GitHubIssueWrites WHERE IssueLinkId IN (SELECT Id FROM GitHubIssueLinks WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks));
            UPDATE GitHubIssueLinks SET TaskId = NULL, State = 3, LocalDirty = 0, LocalDirtySinceUtc = NULL WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM ChecklistConversions WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks) OR SubtaskId IN (SELECT Id FROM temp.PurgeSubtasks);
            DELETE FROM Subtasks WHERE Id IN (SELECT Id FROM temp.PurgeSubtasks);
            DELETE FROM DiaryRevisions WHERE EntryId IN (SELECT Id FROM temp.PurgeDiary);
            DELETE FROM DiaryEntries WHERE Id IN (SELECT Id FROM temp.PurgeDiary);
            DELETE FROM TaskTags WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM TaskHistory WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM ScratchTaskLinks WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks) OR PadItemId IN (SELECT Id FROM temp.PurgePad);
            DELETE FROM PadDrawings WHERE PadItemId IN (SELECT Id FROM temp.PurgePad);
            DELETE FROM ScratchPadTags WHERE PadItemId IN (SELECT Id FROM temp.PurgePad);
            DELETE FROM ScratchPadItems WHERE Id IN (SELECT Id FROM temp.PurgePad);
            DELETE FROM PageTaskLinks WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks) OR PageId IN (SELECT Id FROM temp.PurgePages);
            DELETE FROM PageTags WHERE PageId IN (SELECT Id FROM temp.PurgePages);
            DELETE FROM PageRevisions WHERE PageId IN (SELECT Id FROM temp.PurgePages);
            UPDATE Pages SET ParentPageId = NULL WHERE ParentPageId IN (SELECT Id FROM temp.PurgePages) AND Id NOT IN (SELECT Id FROM temp.PurgePages);
            DELETE FROM Pages WHERE Id IN (SELECT Id FROM temp.PurgePages);
            UPDATE RecurrenceOccurrences SET TaskId = NULL WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM SavedViewMatches WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM NotificationJobs WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM ReminderRules WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM FollowUps WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM WaitingDetails WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM DayPlanItems WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM WeekGoalTasks WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM WeekAssignments WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM ImportedItems WHERE TaskId IN (SELECT Id FROM temp.PurgeTasks);
            DELETE FROM Tasks WHERE Id IN (SELECT Id FROM temp.PurgeTasks);
            """);
    }

    private static void DeleteUnreferencedContainersAndTags(SqliteConnection connection)
    {
        Execute(connection, """
            DELETE FROM TaskTemplates WHERE DeletedAtUtc IS NOT NULL;
            DELETE FROM Sections WHERE DeletedAtUtc IS NOT NULL AND NOT EXISTS (SELECT 1 FROM Tasks t WHERE t.SectionId = Sections.Id) AND NOT EXISTS (SELECT 1 FROM GitHubRepositories r WHERE r.TargetSectionId = Sections.Id);
            DELETE FROM RecurrenceExceptions WHERE SeriesId IN (SELECT Id FROM RecurrenceSeries s WHERE s.DeletedAtUtc IS NOT NULL AND NOT EXISTS (SELECT 1 FROM RecurrenceOccurrences o WHERE o.SeriesId = s.Id AND o.TaskId IS NOT NULL));
            DELETE FROM RecurrenceSeriesRevisions WHERE SeriesId IN (SELECT Id FROM RecurrenceSeries s WHERE s.DeletedAtUtc IS NOT NULL AND NOT EXISTS (SELECT 1 FROM RecurrenceOccurrences o WHERE o.SeriesId = s.Id AND o.TaskId IS NOT NULL));
            DELETE FROM RecurrenceOccurrences WHERE TaskId IS NULL AND SeriesId IN (SELECT Id FROM RecurrenceSeries s WHERE s.DeletedAtUtc IS NOT NULL AND NOT EXISTS (SELECT 1 FROM RecurrenceOccurrences o WHERE o.SeriesId = s.Id AND o.TaskId IS NOT NULL));
            DELETE FROM RecurrenceSeries WHERE DeletedAtUtc IS NOT NULL AND NOT EXISTS (SELECT 1 FROM RecurrenceOccurrences o WHERE o.SeriesId = RecurrenceSeries.Id);
            DELETE FROM Sections WHERE ContainerId IN (SELECT Id FROM Containers c WHERE c.DeletedAtUtc IS NOT NULL)
                AND NOT EXISTS (SELECT 1 FROM Tasks t WHERE t.SectionId = Sections.Id)
                AND NOT EXISTS (SELECT 1 FROM GitHubRepositories r WHERE r.TargetSectionId = Sections.Id);
            DELETE FROM Containers WHERE DeletedAtUtc IS NOT NULL
                AND NOT EXISTS (SELECT 1 FROM Tasks t WHERE t.ContainerId = Containers.Id)
                AND NOT EXISTS (SELECT 1 FROM Sections s WHERE s.ContainerId = Containers.Id)
                AND NOT EXISTS (SELECT 1 FROM RecurrenceSeries r WHERE r.ContainerId = Containers.Id)
                AND NOT EXISTS (SELECT 1 FROM WeekGoals g WHERE g.ProjectId = Containers.Id)
                AND NOT EXISTS (SELECT 1 FROM GitHubRepositories r WHERE r.TargetContainerId = Containers.Id)
                AND NOT EXISTS (SELECT 1 FROM GitHubMilestoneProjects m WHERE m.ContainerId = Containers.Id);
            DELETE FROM GitHubLabelTags WHERE TagId IN (SELECT Id FROM Tags WHERE DeletedAtUtc IS NOT NULL);
            """);

        // Tags: leaves first, repeatedly, so a deleted subtree goes once its children have.
        // Only active links keep a tag alive; tombstoned links (migration 024) do not. A tag
        // going away takes its tombstoned link rows with it — a purge removes dependents, and
        // the link tables' foreign keys refuse to let the tag rows go while they exist.
        while (true)
        {
            Execute(connection, """
                CREATE TEMP TABLE IF NOT EXISTS PurgeTags (Id TEXT PRIMARY KEY);
                DELETE FROM temp.PurgeTags;
                INSERT INTO temp.PurgeTags
                    SELECT Id FROM Tags WHERE DeletedAtUtc IS NOT NULL
                        AND NOT EXISTS (SELECT 1 FROM Tags c WHERE c.ParentTagId = Tags.Id)
                        AND NOT EXISTS (SELECT 1 FROM TaskTags l WHERE l.TagId = Tags.Id AND l.DeletedAtUtc IS NULL)
                        AND NOT EXISTS (SELECT 1 FROM SubtaskTags l WHERE l.TagId = Tags.Id AND l.DeletedAtUtc IS NULL)
                        AND NOT EXISTS (SELECT 1 FROM ScratchPadTags l WHERE l.TagId = Tags.Id AND l.DeletedAtUtc IS NULL)
                        AND NOT EXISTS (SELECT 1 FROM PageTags l WHERE l.TagId = Tags.Id AND l.DeletedAtUtc IS NULL);
                DELETE FROM TaskTags WHERE TagId IN (SELECT Id FROM temp.PurgeTags);
                DELETE FROM SubtaskTags WHERE TagId IN (SELECT Id FROM temp.PurgeTags);
                DELETE FROM ScratchPadTags WHERE TagId IN (SELECT Id FROM temp.PurgeTags);
                DELETE FROM PageTags WHERE TagId IN (SELECT Id FROM temp.PurgeTags);
                """);
            using var tags = connection.CreateCommand();
            tags.CommandText = "DELETE FROM Tags WHERE Id IN (SELECT Id FROM temp.PurgeTags);";
            if (tags.ExecuteNonQuery() == 0)
            {
                break;
            }
        }
    }

    /// <summary>Replaces the captured title of purged tasks in stored summary facts; counts and structure stay.</summary>
    private static void ScrubSummaries(SqliteConnection connection)
    {
        var purged = Strings(connection, "SELECT Id FROM temp.PurgeTasks;").ToHashSet(StringComparer.Ordinal);
        if (purged.Count == 0)
        {
            return;
        }

        var rows = new List<(string Id, string Facts)>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT Id, Facts FROM Summaries;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (id, facts) in rows)
        {
            if (!purged.Any(taskId => facts.Contains(taskId, StringComparison.Ordinal)) || JsonNode.Parse(facts) is not { } node)
            {
                continue;
            }

            Scrub(node, purged);
            using var write = connection.CreateCommand();
            write.CommandText = "UPDATE Summaries SET Facts = $facts WHERE Id = $id;";
            write.Parameters.AddWithValue("$facts", node.ToJsonString());
            write.Parameters.AddWithValue("$id", id);
            write.ExecuteNonQuery();
        }
    }

    private static void Scrub(JsonNode node, HashSet<string> purged)
    {
        switch (node)
        {
            case JsonObject obj:
                var idKey = obj.Select(pair => pair.Key).FirstOrDefault(key => key.Equals("taskId", StringComparison.OrdinalIgnoreCase));
                var idValue = idKey is not null && obj[idKey] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                if (idValue is not null && purged.Contains(idValue))
                {
                    foreach (var key in obj.Select(pair => pair.Key).ToList())
                    {
                        if (key.Equals("title", StringComparison.OrdinalIgnoreCase) || key.Equals("taskTitle", StringComparison.OrdinalIgnoreCase))
                        {
                            obj[key] = PurgedTitle;
                        }
                        else if (key.Equals("excerpt", StringComparison.OrdinalIgnoreCase))
                        {
                            obj[key] = string.Empty;
                        }
                    }
                }

                foreach (var child in obj.Select(pair => pair.Value).Where(child => child is not null).ToList())
                {
                    Scrub(child!, purged);
                }

                break;
            case JsonArray array:
                foreach (var child in array.Where(child => child is not null).ToList())
                {
                    Scrub(child!, purged);
                }

                break;
        }
    }

    private static List<string> Strings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
