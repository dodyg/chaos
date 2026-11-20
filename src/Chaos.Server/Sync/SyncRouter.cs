using Chaos.Server.Data;
using Chaos.Sync;

namespace Chaos.Server.Sync;

/// <summary>Routes the current protocol's changes to the account stream. Shared streams arrive in Q3.</summary>
internal static class SyncRouter
{
    internal static (Guid StreamId, string? Rejection) Route(Account account, SyncKindInfo kind, SyncChange change)
    {
        // Preserve the v1 ownership check, including its treatment of purges and unbound accounts.
        if (change.Op != SyncOp.Purge && kind.HasSpace && account.PersonalSpaceId is { } space
            && change.Groups.TryGetValue("create", out var create)
            && create.Values.Values.TryGetValue("SpaceId", out var value)
            && (value is not string text || !Guid.TryParse(text, out var entitySpace) || entitySpace != space))
        {
            return (account.Id, SyncRejection.NotOwner);
        }

        // All existing routes resolve to the account stream while shared membership is unavailable.
        return (account.Id, null);
    }
}
