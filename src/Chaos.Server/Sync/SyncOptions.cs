namespace Chaos.Server.Sync;

/// <summary>Sync settings (<c>Sync</c> section).</summary>
public sealed class SyncOptions
{
    public const string Section = "Sync";

    /// <summary>Clients whose schema is older than this are refused with <c>schema-too-old</c> (PLAN2 ยง7.12).</summary>
    public int MinClientSchema { get; set; }

    /// <summary>How long the change log is kept; a device that pulled longer ago re-syncs from the snapshot.</summary>
    public TimeSpan ChangeLogHorizon { get; set; } = Chaos.Sync.SyncProtocol.ChangeLogHorizon;

    /// <summary>How often the pruning job runs.</summary>
    public TimeSpan HorizonJobInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>How often an idle event stream sends a keep-alive comment (PLAN2 ง7.12: 25 s).</summary>
    public TimeSpan EventKeepAlive { get; set; } = TimeSpan.FromSeconds(25);
}
