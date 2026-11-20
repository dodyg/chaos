namespace Chaos.Sync;

/// <summary>Constants of wire protocol version 1 (PLAN2 §7.12, §8.5), shared by the desktop and the service.</summary>
public static class SyncProtocol
{
    public const int Version = 1;
    public const int Version1 = 1;
    public const int Version2 = 2;

    public const string ProtocolHeader = "Chaos-Protocol";
    public const string SchemaHeader = "Chaos-Client-Schema";
    public const string DeviceHeader = "Chaos-Device";

    public const int MaxPushChanges = 500;
    public const int MaxPushBytes = 4 * 1024 * 1024;
    public const int MaxPullChanges = 1000;
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxKeyLength = 300;

    /// <summary>How long the service keeps the change log; a device that pulled longer ago re-syncs from the snapshot.</summary>
    public static readonly TimeSpan ChangeLogHorizon = TimeSpan.FromDays(90);
}

/// <summary>Why the service refused one change (PLAN2 §7.12).</summary>
public static class SyncRejection
{
    public const string UnknownKind = "unknown-kind";
    public const string TooLarge = "too-large";
    public const string Quota = "quota";
    public const string SchemaTooOld = "schema-too-old";
    public const string NotOwner = "not-owner";
    public const string Invalid = "invalid";

    /// <summary>The entity was purged: a late edit to it has nothing to apply to (PLAN2 §8.3 step 6).</summary>
    public const string Purged = "purged";
}
