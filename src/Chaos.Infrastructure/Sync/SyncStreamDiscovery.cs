using System.Text.Json;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Sync;

/// <summary>Refreshes the service's readable stream set before joining or evicting any stream.</summary>
public sealed class SyncStreamDiscovery(IChaosServiceClient service, SyncStreamRegistry registry)
{
    public async Task<long> RefreshAsync(Guid accountId, CancellationToken ct = default)
    {
        var response = await service.CallAsync(HttpMethod.Get, "v2/streams", null, ct);
        if (!response.Ok)
            throw new ChaosServiceException(ServiceFailure.Refused, $"The online service refused stream discovery ({response.Status}).");

        long version;
        List<DiscoveredStream> streams;
        try
        {
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            version = root.GetProperty("membershipVersion").GetInt64();
            if (version < 0) throw new FormatException("Invalid membership version.");
            streams = [];
            foreach (var row in root.GetProperty("streams").EnumerateArray())
            {
                var id = row.GetProperty("id").GetGuid();
                var kind = row.GetProperty("kind").GetInt32() switch
                {
                    1 => "account",
                    2 => "space",
                    _ => throw new FormatException("Invalid stream kind."),
                };
                var space = row.GetProperty("spaceId");
                var role = row.GetProperty("role");
                streams.Add(new DiscoveredStream(id, kind,
                    space.ValueKind == JsonValueKind.Null ? null : space.GetGuid(),
                    role.ValueKind == JsonValueKind.Null ? null : role.GetInt32()));
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            throw new ChaosServiceException(ServiceFailure.Other, "The online service returned an invalid stream list.", error);
        }

        await registry.ReconcileAsync(accountId, streams, ct);
        return version;
    }
}
