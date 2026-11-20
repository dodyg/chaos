using Chaos.Sync;

namespace Chaos.Server.Sync;

/// <summary>The kinds and groups the service accepts. It is <see cref="SyncKinds.All"/> in production; tests swap it to simulate a newer client.</summary>
public sealed class SyncKindCatalog(IReadOnlyList<SyncKindInfo> kinds)
{
    private static readonly IReadOnlyList<SyncKindInfo> ServerKinds =
    [
        new("space_member", 1, 21, true, new SyncGroupInfo[]
        {
            new("create", 4, SyncWrite.Server),
            new("profile", 4, SyncWrite.Server),
            new("role", 4, SyncWrite.Server),
            new("state", 4, SyncWrite.Server),
        })
        { Route = new SyncRoute(SyncRouteKind.ServerOnly) },
    ];

    private readonly Dictionary<string, SyncKindInfo> _byKind = Build(kinds);

    public SyncKindCatalog()
        : this(SyncKinds.All)
    {
    }

    public SyncKindInfo? Find(string kind) => _byKind.GetValueOrDefault(kind);
    public IReadOnlyList<SyncKindInfo> All => [.. _byKind.Values.OrderBy(kind => kind.ApplyOrder)];

    private static Dictionary<string, SyncKindInfo> Build(IReadOnlyList<SyncKindInfo> clientKinds)
    {
        var kinds = clientKinds.ToDictionary(kind => kind.Kind, StringComparer.Ordinal);
        foreach (var serverKind in ServerKinds)
        {
            kinds[serverKind.Kind] = serverKind;
        }

        kinds["space"] = kinds["space"] with
        {
            Groups =
            [
                .. kinds["space"].Groups,
                new("look", 4, SyncWrite.Owner),
                new("settings", 4, SyncWrite.Owner),
            ],
        };
        return kinds;
    }
}
