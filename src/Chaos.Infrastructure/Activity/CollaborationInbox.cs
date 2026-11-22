using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Reminders;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Activity;

public sealed record CollaborationNotice(string Id, string Kind, string SpaceId, string EntityKind, string EntityId,
    string ActorId, string ActorName, string CreatedAtUtc, string NotifyAtUtc, string? DeliveredAtUtc, string? ReadAtUtc, string? TaskId);

/// <summary>Local inbox reads and acknowledgements; notifications never sync to another device.</summary>
public sealed class CollaborationInbox(SqliteApplicationStore store, TimeProvider clock)
{
    public Task<IReadOnlyList<CollaborationNotice>> ListAsync(CancellationToken ct = default) => store.QueryAsync((session, _) =>
    {
        using var command = session.Connection().CreateCommand();
        command.CommandText = """
            SELECT n.Id,n.Kind,n.SpaceId,n.EntityKind,n.EntityId,n.ActorId,
              COALESCE((SELECT m.DisplayName FROM SpaceMembers m WHERE m.SpaceId=n.SpaceId AND m.MemberId=n.ActorId COLLATE NOCASE LIMIT 1),'Unknown person'),
              n.CreatedAtUtc,n.NotifyAtUtc,n.DeliveredAtUtc,n.ReadAtUtc,
              CASE n.EntityKind WHEN 'task' THEN n.EntityId WHEN 'diary_entry' THEN (SELECT TaskId FROM DiaryEntries WHERE Id=n.EntityId) END
            FROM CollaborationNotices n ORDER BY n.CreatedAtUtc DESC,n.Id LIMIT 500;
            """;
        using var reader = command.ExecuteReader();
        var results = new List<CollaborationNotice>();
        while (reader.Read()) results.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11)));
        return Task.FromResult<IReadOnlyList<CollaborationNotice>>(results);
    }, ct);

    public async Task<IReadOnlyList<CollaborationNotice>> DueAsync(CancellationToken ct = default)
    {
        var profile = await store.QueryAsync((session, token) => session.Profiles.GetPrimaryAsync(token), ct);
        if (profile is null) return [];
        var now = clock.GetUtcNow();
        if (ReminderSync.DeliveryInstant(profile, ReminderSync.ProfileZone(profile), now) > now) return [];
        var notices = await ListAsync(ct);
        var settings = new SpaceNotificationPreferences(store, clock);
        var eligible = new List<CollaborationNotice>();
        foreach (var notice in notices.Where(n => n.DeliveredAtUtc is null && StorageFormats.ParseInstant(n.NotifyAtUtc) <= now))
        {
            var preference = await settings.GetAsync(notice.SpaceId, ct);
            if (!preference.Nothing && (notice.Kind != "mentioned" || preference.Mentioned)
                && (notice.Kind != "assigned" || preference.Assigned) && (notice.Kind != "due-soon" || preference.DueSoon)) eligible.Add(notice);
        }
        return eligible;
    }

    public Task MarkReadAsync(string id, CancellationToken ct = default) => MarkAsync(id, "ReadAtUtc", ct);
    public Task MarkDeliveredAsync(string id, CancellationToken ct = default) => MarkAsync(id, "DeliveredAtUtc", ct);

    private Task MarkAsync(string id, string column, CancellationToken ct) => store.ExecuteAsync((session, _) =>
    {
        using var command = session.Connection().CreateCommand();
        command.CommandText = $"UPDATE CollaborationNotices SET {column}=COALESCE({column},$now) WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", StorageFormats.Instant(clock.GetUtcNow()));
        if (command.ExecuteNonQuery() != 0) session.Activity.Note("collaboration_notice", id, ActivityOp.Upsert);
        return Task.FromResult(0);
    }, ct);
}
