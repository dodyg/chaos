using Chaos.Sync;

namespace Chaos.Server.Sync;

/// <summary>The kinds and groups the service accepts. It is <see cref="SyncKinds.All"/> in production; tests swap it to simulate a newer client.</summary>
public sealed class SyncKindCatalog(IReadOnlyList<SyncKindInfo> kinds)
{
    private readonly Dictionary<string, SyncKindInfo> _byKind = kinds.ToDictionary(kind => kind.Kind, StringComparer.Ordinal);

    public SyncKindCatalog()
        : this(SyncKinds.All)
    {
    }

    public SyncKindInfo? Find(string kind) => _byKind.GetValueOrDefault(kind);
}
