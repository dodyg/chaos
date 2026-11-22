using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;
using System.Text.Json;

namespace Chaos.Infrastructure.Activity;

public sealed record SpaceNotificationPreference(bool Assigned = true, bool Mentioned = true, bool DueSoon = false,
    bool Delegations = true, bool Nothing = false, bool OwnerDefaultReminder = true);

/// <summary>The current profile's account-synced preferences for a shared space.</summary>
public sealed class SpaceNotificationPreferences(SqliteApplicationStore store, TimeProvider clock)
{
    public Task<SpaceNotificationPreference> GetAsync(string spaceId, CancellationToken ct = default) => store.QueryAsync(async (session, token) =>
    {
        var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw new AppException(AppErrorKind.NotFound, "No profile exists.");
        using var command = session.Connection().CreateCommand();
        command.CommandText = "SELECT Assigned,Mentioned,DueSoon,Delegations,\"Nothing\",OwnerDefaultReminder FROM SpaceNotificationSettings WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", Id(profile.Id, spaceId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new SpaceNotificationPreference(reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2),
            reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5)) : new SpaceNotificationPreference();
    }, ct);

    public Task SetAsync(string spaceId, SpaceNotificationPreference value, CancellationToken ct = default) => store.ExecuteAsync(async (session, token) =>
    {
        if (!Guid.TryParseExact(spaceId, "D", out var parsed)) throw AppException.Validation("spaceId", "Choose a valid space.");
        spaceId = parsed.ToString("D");
        var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw new AppException(AppErrorKind.NotFound, "No profile exists.");
        using var command = session.Connection().CreateCommand();
        command.CommandText = """
            INSERT INTO SpaceNotificationSettings (Id,ProfileId,SpaceId,Assigned,Mentioned,DueSoon,Delegations,"Nothing",OwnerDefaultReminder,CreatedAtUtc,UpdatedAtUtc)
            SELECT $id,$profile,$space,$assigned,$mentioned,$due,$delegations,$nothing,$owner,$now,$now
            WHERE EXISTS (SELECT 1 FROM Spaces s JOIN SpaceMembers m ON m.SpaceId=s.Id
                WHERE s.Id=$space AND s.Kind=2 AND s.DeletedAtUtc IS NULL AND m.MemberId=$profile COLLATE NOCASE AND m.RemovedAtUtc IS NULL)
            ON CONFLICT (Id) DO UPDATE SET Assigned=excluded.Assigned,Mentioned=excluded.Mentioned,DueSoon=excluded.DueSoon,
                Delegations=excluded.Delegations,"Nothing"=excluded."Nothing",OwnerDefaultReminder=excluded.OwnerDefaultReminder,
                Version=SpaceNotificationSettings.Version+1,UpdatedAtUtc=excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$id", Id(profile.Id, spaceId));
        command.Parameters.AddWithValue("$profile", profile.Id);
        command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$assigned", value.Assigned ? 1 : 0);
        command.Parameters.AddWithValue("$mentioned", value.Mentioned ? 1 : 0);
        command.Parameters.AddWithValue("$due", value.DueSoon ? 1 : 0);
        command.Parameters.AddWithValue("$delegations", value.Delegations ? 1 : 0);
        command.Parameters.AddWithValue("$nothing", value.Nothing ? 1 : 0);
        command.Parameters.AddWithValue("$owner", value.OwnerDefaultReminder ? 1 : 0);
        command.Parameters.AddWithValue("$now", StorageFormats.Instant(clock.GetUtcNow()));
        if (command.ExecuteNonQuery() != 1) throw AppException.Validation("spaceId", "You are not a member of that shared space.");
        return 0;
    }, ct);

    private static string Id(string profileId, string spaceId) => $"spacenotify:{profileId.ToLowerInvariant()}:{spaceId.ToLowerInvariant()}";

    public Task<int?> GetDefaultDeadlineAsync(string spaceId, CancellationToken ct = default) => store.QueryAsync((session, _) =>
    {
        using var command = session.Connection().CreateCommand();
        command.CommandText = "SELECT DefaultDeadlineReminderJson FROM Spaces WHERE Id=$space AND Kind=2 AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$space", spaceId);
        if (command.ExecuteScalar() is not string json) return Task.FromResult<int?>(null);
        using var document = JsonDocument.Parse(json);
        return Task.FromResult(document.RootElement.TryGetProperty("offsetMinutes", out var value) && value.TryGetInt32(out var offset) ? (int?)offset : null);
    }, ct);

    public Task SetDefaultDeadlineAsync(string spaceId, int? offsetMinutes, CancellationToken ct = default) => store.ExecuteAsync(async (session, token) =>
    {
        if (!Guid.TryParseExact(spaceId, "D", out var id)) throw AppException.Validation("spaceId", "Choose a valid space.");
        if (offsetMinutes is { } offset && Math.Abs((long)offset) > Chaos.Domain.Reminders.ReminderTiming.MaximumOffsetMinutes)
            throw AppException.Validation("offsetMinutes", "Choose a reminder within one year of the deadline.");
        var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw new AppException(AppErrorKind.NotFound, "No profile exists.");
        using var command = session.Connection().CreateCommand();
        command.CommandText = """
            UPDATE Spaces SET DefaultDeadlineReminderJson=$json, Version=Version+1, UpdatedAtUtc=$now
            WHERE Id=$space AND Kind=2 AND DeletedAtUtc IS NULL AND EXISTS
              (SELECT 1 FROM SpaceMembers m WHERE m.SpaceId=Spaces.Id AND m.MemberId=$profile COLLATE NOCASE AND m.Role=1 AND m.RemovedAtUtc IS NULL);
            """;
        command.Parameters.AddWithValue("$json", offsetMinutes is null ? DBNull.Value : JsonSerializer.Serialize(new { offsetMinutes }));
        command.Parameters.AddWithValue("$now", StorageFormats.Instant(clock.GetUtcNow()));
        command.Parameters.AddWithValue("$space", id.ToString("D"));
        command.Parameters.AddWithValue("$profile", profile.Id);
        if (command.ExecuteNonQuery() != 1) throw AppException.Validation("spaceId", "Only a space owner can set the default reminder.");
        return 0;
    }, ct);
}
