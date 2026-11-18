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

    /// <summary>The next sync sequence number; assigned under a row lock (PLAN2 §8.3).</summary>
    public long NextSeq { get; set; } = 1;

    public long StorageBytes { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>The highest sequence number whose change-log rows were pruned (PLAN2 §7.12 horizon); a cursor below it needs the snapshot.</summary>
    public long ChangesPrunedThroughSeq { get; set; }

    public List<AccountLogin> Logins { get; } = [];
    public List<Device> Devices { get; } = [];
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

    public static readonly IReadOnlyList<string> All = [SignIn, DeviceAdded, DeviceRevoked, AccountDeleted, Quota];
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
    public DateTimeOffset CreatedAt { get; set; }
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
    public Guid AccountId { get; set; }

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

/// <summary>An upload that has started and not yet completed.</summary>
public sealed class BlobUploadRow
{
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
    public Guid AccountId { get; set; }
    public required string Sha256 { get; set; }
    public int PartIndex { get; set; }
    public int ByteSize { get; set; }
}
