using System.Globalization;
using System.Text.Json;
using Chaos.Server.Data;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Sync;

/// <summary>
/// Tells every member's live devices that a stream's head moved, in-process and through PostgreSQL
/// LISTEN/NOTIFY for the other service instances (PLAN3 7.8). The stream itself stands in when the
/// space has no person left, so the account stream of the space still learns the change.
/// </summary>
internal static class StreamNotifications
{
    public static async Task NotifyAsync(ChaosDbContext db, SyncBroadcaster broadcaster, Guid streamId, CancellationToken ct)
    {
        var stream = await db.SyncStreams.AsNoTracking().Where(s => s.Id == streamId)
            .Select(s => new { s.Id, Seq = s.NextSeq - 1 }).FirstOrDefaultAsync(ct);
        if (stream is null)
        {
            return;
        }

        var accounts = await db.SpaceMembers.AsNoTracking()
            .Where(m => m.SpaceId == streamId && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null && m.AccountId != null)
            .Select(m => m.AccountId!.Value).ToListAsync(ct);
        if (accounts.Count == 0)
        {
            accounts.Add(streamId);
        }

        foreach (var accountId in accounts.Distinct())
        {
            broadcaster.PublishStream(accountId, streamId, stream.Seq);
        }

        var payload = SyncNotificationListener.Payload(streamId, stream.Seq);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_notify({SyncNotificationListener.Channel}, {payload})", CancellationToken.None);
    }
}
