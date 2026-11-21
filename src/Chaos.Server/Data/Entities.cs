namespace Chaos.Server.Data;

/// <summary>An account on the online service (PLAN2 §8.2). Never holds task content.</summary>
public sealed class Account
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Lower-cased; unique across accounts.</summary>
    public required string PrimaryEmail { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>The canonical profile, Personal space and Inbox IDs bound by the first device (PLAN2 §7.13).</summary>
    public Guid? ProfileId { get; set; }
    public Guid? PersonalSpaceId { get; set; }
    public Guid? InboxId { get; set; }

    public long StorageBytes { get; set; }
    public long MembershipVersion { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    public List<AccountLogin> Logins { get; } = [];
    public List<Device> Devices { get; } = [];
}

/// <summary>An independently ordered account or space change stream.</summary>
public sealed class SyncStream
{
    public const short AccountKind = 1;
    public const short SpaceKind = 2;

    public Guid Id { get; set; }
    public short Kind { get; set; }
    public Guid? AccountId { get; set; }
    public Guid? SpaceId { get; set; }
    public long NextSeq { get; set; } = 1;
    public long ChangesPrunedThroughSeq { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset? PurgeAfter { get; set; }
}

/// <summary>One way of signing in to an account: an email address or an external provider's subject.</summary>
public sealed class AccountLogin
{
    public Guid AccountId { get; set; }
    public required string Provider { get; set; }
    public required string Subject { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset LinkedAt { get; set; }
}

/// <summary>The sign-in providers an <see cref="AccountLogin"/> may name.</summary>
public static class LoginProviders
{
    public const string Email = "email";
    public const string Google = "google";
    public const string Microsoft = "microsoft";
    public const string Apple = "apple";
    public const string GitHub = "github";

    public static readonly IReadOnlyList<string> All = [Email, Google, Microsoft, Apple, GitHub];
}

/// <summary>A desktop (or later phone) registered to an account; its ID is the client's device ID.</summary>
public sealed class Device
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required string Name { get; set; }
    public required string Platform { get; set; }
    public required string ClientVersion { get; set; }
    public int ClientSchema { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>A one-time email sign-in code, stored only as a PBKDF2 hash (PLAN2 §8.4).</summary>
public sealed class EmailCode
{
    public const int MaxAttempts = 5;

    public Guid Id { get; set; }

    /// <summary>Lower-cased.</summary>
    public required string Email { get; set; }

    public required string CodeHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A content-free audit record (PLAN2 §8.5). It has no foreign key, so the "account-deleted" row
/// outlives the account it describes (§8.6).
/// </summary>
public sealed class AuditEvent
{
    public long Id { get; set; }
    public Guid AccountId { get; set; }
    public required string Kind { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? DeviceId { get; set; }
}

/// <summary>The kinds an <see cref="AuditEvent"/> may record.</summary>
public static class AuditKinds
{
    public const string SignIn = "sign-in";
    public const string DeviceAdded = "device-added";
    public const string DeviceRevoked = "device-revoked";
    public const string AccountDeleted = "account-deleted";
    public const string Quota = "quota";
    public const string SpaceCreated = "space-created";
    public const string SpaceDeleted = "space-deleted";
    public const string SpaceRestored = "space-restored";
    public const string MemberAdded = "member-added";
    public const string MemberRemoved = "member-removed";
    public const string MemberRole = "member-role";
    public const string InviteCreated = "invite-created";
    public const string InviteUsed = "invite-used";
    public const string InviteRevoked = "invite-revoked";
    public const string AgentCreated = "agent-created";
    public const string AgentPaused = "agent-paused";
    public const string AgentRemoved = "agent-removed";
    public const string AgentReverted = "agent-reverted";
    public const string Move = "move";

    public static readonly IReadOnlyList<string> All =
    [
        SignIn, DeviceAdded, DeviceRevoked, AccountDeleted, Quota,
        SpaceCreated, SpaceDeleted, SpaceRestored, MemberAdded, MemberRemoved, MemberRole,
        InviteCreated, InviteUsed, InviteRevoked, AgentCreated, AgentPaused, AgentRemoved,
        AgentReverted, Move,
    ];
}

/// <summary>
/// One synced entity's current state (PLAN2 §8.2): its field groups with their clocks as JSON, merged by
/// <c>Chaos.Sync.Merge</c>. A purged entity stays as a tombstone, so a late edit cannot bring it back.
/// </summary>
public sealed class SyncEntityRow
{
    public Guid AccountId { get; set; }
    public required string Kind { get; set; }
    public required string EntityKey { get; set; }

    /// <summary>name → <c>{hlc, values}</c>, in the shape of <c>SyncJson</c>.</summary>
    public required string Groups { get; set; }

    public DateTimeOffset? PurgedAt { get; set; }
    public Guid? MovedTo { get; set; }
    public long LastSeq { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One entry of the account's change log (PLAN2 §8.2): only the groups that won on the service.</summary>
public sealed class SyncChangeRow
{
    public Guid AccountId { get; set; }
    public long Seq { get; set; }
    public Guid ChangeId { get; set; }
    public required string Kind { get; set; }
    public required string EntityKey { get; set; }
    public required string Op { get; set; }
    public required string Groups { get; set; }
    public Guid? OriginDevice { get; set; }
    public Guid? ActorId { get; set; }
    public string? PreviousGroups { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A person or agent currently or formerly in a shared space.</summary>
public sealed class SpaceMember
{
    public const short PersonKind = 1;
    public const short AgentKind = 2;
    public const short OwnerRole = 1;
    public const short EditorRole = 2;
    public const short ViewerRole = 3;
    public const short AgentRole = 4;

    public Guid SpaceId { get; set; }
    public Guid MemberId { get; set; }
    public short MemberKind { get; set; }
    public Guid? AccountId { get; set; }
    public short Role { get; set; }
    public required string DisplayName { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public Guid? InvitedByMemberId { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public string? AllowedContainers { get; set; }
    public string? GitHubLogin { get; set; }
}

/// <summary>A share invitation. The public token is never persisted; only its SHA-256 hash is stored.</summary>
public sealed class SpaceInvite
{
    public Guid Id { get; set; }
    public Guid SpaceId { get; set; }
    public required byte[] TokenHash { get; set; }
    public string? Email { get; set; }
    public short Role { get; set; }
    public Guid CreatedByMemberId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public int? MaxUses { get; set; }
    public int Uses { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>The states of a stored blob (PLAN2 §9.2). A <c>Blocked</c> blob is never served.</summary>
public static class BlobStates
{
    public const string Clean = "Clean";
    public const string Blocked = "Blocked";

    public static readonly IReadOnlyList<string> All = [Clean, Blocked];
}

/// <summary>One attachment's bytes on the service, per account (PLAN2 §8.2). The bytes are encrypted in the object store.</summary>
public sealed class BlobRow
{
    public Guid StreamId { get; set; }
    public Guid AccountId { get; set; }
    public Guid ObjectId { get; set; }

    /// <summary>Lowercase hex SHA-256 of the plaintext.</summary>
    public required string Sha256 { get; set; }

    public long ByteSize { get; set; }
    public required string State { get; set; }

    /// <summary>The scanner's detection name for a blocked blob; never anything from the file.</summary>
    public string? Detail { get; set; }

    /// <summary>The object key; empty for a blocked blob, whose bytes are not kept.</summary>
    public required string StorageKey { get; set; }

    public required byte[] WrappedKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Since when no live attachment references the hash; the blob goes after the grace period (PLAN2 §9.2).</summary>
    public DateTimeOffset? UnreferencedSince { get; set; }
}

/// <summary>One stored encrypted object, which can be referenced from more than one stream.</summary>
public sealed class BlobObjectRow
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public long ByteSize { get; set; }
    public required string State { get; set; }
    public required string StorageKey { get; set; }
}

/// <summary>A committed object deletion awaiting removal from the object store.</summary>
public sealed class BlobDeletionRow
{
    public Guid Id { get; set; }
    public required string StorageKey { get; set; }
    public DateTimeOffset NotBefore { get; set; }
}

/// <summary>
/// Which account's stored blob an attachment in a space stream reads from. The server records it when that account's
/// push wins the attachment's <c>file</c> group, or when the account moves the attachment in from its own stream. A
/// client never names the custodian, so naming a hash in an attachment cannot unlock another account's blob.
/// </summary>
public sealed class AttachmentBlobBinding
{
    public Guid StreamId { get; set; }
    public required string AttachmentId { get; set; }
    public required string Sha256 { get; set; }
    public Guid CustodianAccountId { get; set; }
}

/// <summary>An upload that has started and not yet completed.</summary>
public sealed class BlobUploadRow
{
    public Guid StreamId { get; set; }
    public Guid AccountId { get; set; }
    public required string Sha256 { get; set; }
    public long ByteSize { get; set; }
    public int PartCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? DeviceId { get; set; }
}

/// <summary>One received part of an upload; its bytes are in the object store.</summary>
public sealed class BlobPartRow
{
    public Guid StreamId { get; set; }
    public required string Sha256 { get; set; }
    public int PartIndex { get; set; }
    public int ByteSize { get; set; }
}
