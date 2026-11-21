using Chaos.Server.Data;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Sync;

/// <summary>Checks that an attachment belongs to a live diary entry written by the caller.</summary>
internal static class DiaryAttachmentAuthorization
{
    public static async Task<bool> IsAuthorAsync(ChaosDbContext db, Guid streamId, Guid profileId,
        IReadOnlyDictionary<string, SyncGroupState> groups, CancellationToken ct, bool allowDeletedAttachment = false)
    {
        if (!groups.TryGetValue("create", out var create)
            || create.Values.Values.GetValueOrDefault("OwnerKind")?.ToString() != "2"
            || create.Values.Values.GetValueOrDefault("OwnerId")?.ToString() is not { Length: > 0 } ownerId
            || (!allowDeletedAttachment && groups.GetValueOrDefault("deleted")?.Values.Values.GetValueOrDefault("DeletedAtUtc") is not null))
        {
            return false;
        }

        var diary = await db.SyncEntities.FindAsync([streamId, "diary_entry", ownerId], ct);
        if (diary is null || diary.PurgedAt is not null || diary.MovedTo is not null)
        {
            return false;
        }

        var entry = SyncJson.ReadGroups(diary.Groups);
        return entry.GetValueOrDefault("deleted")?.Values.Values.GetValueOrDefault("DeletedAtUtc") is null
            && Guid.TryParse(entry.GetValueOrDefault("create")?.Values.Values.GetValueOrDefault("AuthorProfileId")?.ToString(), out var author)
            && author == profileId;
    }
}
