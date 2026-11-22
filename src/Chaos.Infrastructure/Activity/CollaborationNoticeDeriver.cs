using System.Text.Json;
using System.Text.RegularExpressions;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Reminders;
using Chaos.Application.Contracts;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Activity;

/// <summary>Consumes committed remote text changes and derives the local member's mention inbox.</summary>
public sealed class CollaborationNoticeDeriver(SqliteApplicationStore store, TimeProvider clock)
{
    public const string Consumer = "collaboration-notices";

    public Task<int> DeriveAsync(CancellationToken ct = default) => store.ExecuteAsync(async (session, token) =>
    {
        var profile = await session.Profiles.GetPrimaryAsync(token);
        if (profile is null) return 0;
        var connection = session.Connection();
        var cursor = 0L;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Seq FROM ActivityCursors WHERE Consumer=$consumer;";
            command.Parameters.AddWithValue("$consumer", Consumer);
            if (command.ExecuteScalar() is long value) cursor = value;
        }
        var entries = new List<(long Seq, string? Detail)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Seq, CASE WHEN Source='sync' THEN DetailJson END FROM ActivityLog WHERE Seq>$cursor ORDER BY Seq LIMIT 500;";
            command.Parameters.AddWithValue("$cursor", cursor);
            using var reader = command.ExecuteReader();
            while (reader.Read()) entries.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }
        var count = 0;
        var now = clock.GetUtcNow();
        foreach (var (seq, detail) in entries)
        {
            if (detail is not null)
            {
                var changes = JsonSerializer.Deserialize<List<RemoteTextChange>>(detail) ?? [];
                foreach (var change in changes)
                {
                    if (string.Equals(change.ActorId, profile.Id, StringComparison.OrdinalIgnoreCase)) continue;
                    var assignment = change.Kind == "task_assignee" && string.Equals(change.After, profile.Id, StringComparison.OrdinalIgnoreCase);
                    if (!assignment && (!ContainsMention(change.After, profile.Id) || ContainsMention(change.Before, profile.Id))) continue;
                    var kind = assignment ? "assigned" : "mentioned";
                    var entityKind = assignment ? "task" : change.Kind;
                    var entityId = assignment ? change.EntityId.Split('|')[0] : change.EntityId;
                    using var command = connection.CreateCommand();
                    command.CommandText = """
                        INSERT OR IGNORE INTO CollaborationNotices
                          (Id, Kind, SpaceId, EntityKind, EntityId, ActorId, CreatedAtUtc, NotifyAtUtc)
                        SELECT $id, $noticeKind, $space, $kind, $entity, $actor, $now, $notify
                        WHERE EXISTS (SELECT 1 FROM Spaces s JOIN SpaceMembers m ON m.SpaceId=s.Id
                            WHERE s.Id=$space AND s.Kind=2 AND s.DeletedAtUtc IS NULL AND m.MemberId=$profile COLLATE NOCASE AND m.RemovedAtUtc IS NULL)
                          AND NOT EXISTS (SELECT 1 FROM SpaceNotificationSettings n WHERE n.ProfileId=$profile COLLATE NOCASE
                            AND n.SpaceId=$space AND (n."Nothing"=1 OR ($noticeKind='mentioned' AND n.Mentioned=0) OR ($noticeKind='assigned' AND n.Assigned=0)));
                        """;
                    var noticeId = Guid.NewGuid().ToString("D");
                    command.Parameters.AddWithValue("$id", noticeId);
                    command.Parameters.AddWithValue("$space", change.SpaceId);
                    command.Parameters.AddWithValue("$noticeKind", kind);
                    command.Parameters.AddWithValue("$kind", entityKind);
                    command.Parameters.AddWithValue("$entity", entityId);
                    command.Parameters.AddWithValue("$actor", change.ActorId);
                    command.Parameters.AddWithValue("$profile", profile.Id);
                    command.Parameters.AddWithValue("$now", StorageFormats.Instant(now));
                    command.Parameters.AddWithValue("$notify", StorageFormats.Instant(ReminderSync.DeliveryInstant(profile, ReminderSync.ProfileZone(profile), now)));
                    var inserted = command.ExecuteNonQuery();
                    count += inserted;
                    if (inserted != 0) session.Activity.Note("collaboration_notice", noticeId, ActivityOp.Upsert);
                }
            }
            cursor = seq;
        }
        if (entries.Count > 0)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO ActivityCursors (Consumer,Seq,UpdatedAtUtc) VALUES ($consumer,$seq,$now)
                ON CONFLICT (Consumer) DO UPDATE SET Seq=excluded.Seq, UpdatedAtUtc=excluded.UpdatedAtUtc;
                """;
            command.Parameters.AddWithValue("$consumer", Consumer);
            command.Parameters.AddWithValue("$seq", cursor);
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(now));
            command.ExecuteNonQuery();
        }
        count += await DeriveDueSoonAsync(session, profile, now, token);
        return count;
    }, ct);

    private static async Task<int> DeriveDueSoonAsync(Chaos.Application.Abstractions.IDataSession session, ProfileRecord profile, DateTimeOffset now, CancellationToken ct)
    {
        var connection = session.Connection();
        var taskIds = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT t.Id FROM Tasks t JOIN Spaces s ON s.Id=t.SpaceId
                JOIN SpaceMembers m ON m.SpaceId=s.Id AND m.MemberId=$profile COLLATE NOCASE AND m.RemovedAtUtc IS NULL
                JOIN SpaceNotificationSettings n ON n.SpaceId=s.Id AND n.ProfileId=$profile COLLATE NOCASE
                WHERE s.Kind=2 AND s.DeletedAtUtc IS NULL AND t.DeletedAtUtc IS NULL AND t.DueDate IS NOT NULL
                  AND n.DueSoon=1 AND n."Nothing"=0;
                """;
            command.Parameters.AddWithValue("$profile", profile.Id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) taskIds.Add(reader.GetString(0));
        }
        var count = 0;
        var zone = ReminderSync.ProfileZone(profile);
        foreach (var id in taskIds)
        {
            var task = await session.Tasks.GetAsync(id, ct);
            if (task is null || task.IsTerminal || ReminderSync.DueAnchor(task, profile, zone) is not { } due || due < now || due > now.AddDays(1)) continue;
            var assigned = (await session.Tasks.ListAssigneesAsync(id, ct)).Where(a => a.DeletedAtUtc is null).ToList();
            if (assigned.Count > 0 && !assigned.Any(a => string.Equals(a.MemberId, profile.Id, StringComparison.OrdinalIgnoreCase))) continue;
            var noticeId = Guid.NewGuid().ToString("D");
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO CollaborationNotices (Id,Kind,SpaceId,EntityKind,EntityId,ActorId,CreatedAtUtc,NotifyAtUtc)
                VALUES ($id,'due-soon',$space,'task',$task,$profile,$now,$notify);
                """;
            command.Parameters.AddWithValue("$id", noticeId);
            command.Parameters.AddWithValue("$space", task.SpaceId);
            command.Parameters.AddWithValue("$task", id);
            command.Parameters.AddWithValue("$profile", profile.Id);
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(now));
            command.Parameters.AddWithValue("$notify", StorageFormats.Instant(ReminderSync.DeliveryInstant(profile, zone, now)));
            var inserted = command.ExecuteNonQuery();
            count += inserted;
            if (inserted != 0) session.Activity.Note("collaboration_notice", noticeId, ActivityOp.Upsert);
        }
        return count;
    }

    internal static bool ContainsMention(string? markdown, string memberId)
    {
        if (string.IsNullOrEmpty(markdown)) return false;
        var withoutCode = Regex.Replace(markdown, @"(?ms)^\s*(```+|~~~+).*?^\s*\1\s*$|`+[^`\r\n]*`+", "");
        return Regex.IsMatch(withoutCode, @"\[@(?:\\.|[^\]\\])*\]\(chaos:member/" + Regex.Escape(memberId) + @"\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
