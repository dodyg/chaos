using System.Globalization;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;
using Chaos.Sync;

namespace Chaos.Infrastructure.Sync;

/// <summary>The service no longer lets this device read the stream (403 <c>not-member</c>): discovery will evict it.</summary>
public sealed class SyncStreamUnreadableException() : Exception("This device can no longer read the stream.");

/// <summary>Snapshots a joining stream and pulls its change log in bounded, durable batches.</summary>
public sealed class SyncStreamPuller(SqliteApplicationStore store, SyncApplier applier, IChaosServiceClient service, int batchSize = 200)
{
    public async Task<(long Pulled, bool Snapshot)> PullAsync(Guid streamId, CancellationToken ct = default)
    {
        if (batchSize <= 0) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var id = streamId.ToString("D");
        var state = await ReadAsync(id, ct);
        var snapshot = state.State == "joining";
        if (snapshot) state = await SnapshotAsync(id, ct);
        long pulled = 0;
        var cursor = state.Cursor;
        while (true)
        {
            var response = await service.CallAsync(HttpMethod.Get, $"v2/sync/pull?stream={id}&after={cursor.ToString(CultureInfo.InvariantCulture)}&limit={SyncProtocol.MaxPullChanges}", null, ct);
            if (!response.Ok) throw Refused(response);
            using var document = JsonDocument.Parse(response.Body);
            var root = document.RootElement;
            if (root.GetProperty("snapshotRequired").GetBoolean())
            {
                if (snapshot) throw new ChaosServiceException(ServiceFailure.Other, "The stream changed while its snapshot was being read. Try again.");
                state = await SnapshotAsync(id, ct);
                cursor = state.Cursor;
                snapshot = true;
                continue;
            }
            var next = root.GetProperty("next").GetInt64();
            if (next < cursor) throw new FormatException("The service returned a decreasing stream cursor.");
            var more = root.GetProperty("more").GetBoolean();
            if (more && next == cursor) throw new FormatException("The service returned a non-advancing stream page.");
            var changes = root.GetProperty("changes").EnumerateArray().Select(c => (Payload: c.GetRawText(), Seq: c.GetProperty("seq").GetInt64())).ToList();
            if (changes.Any(c => c.Seq <= cursor || c.Seq > next) || changes.Select(c => c.Seq).Distinct().Count() != changes.Count
                || !changes.Select(c => c.Seq).SequenceEqual(changes.Select(c => c.Seq).Order()))
                throw new FormatException("The service returned an invalid stream sequence.");
            if (changes.Count == 0 && next > cursor) await applier.ApplyAsync([], ct, next, id);
            for (var offset = 0; offset < changes.Count; offset += batchSize)
            {
                var chunk = changes.Skip(offset).Take(batchSize).ToList();
                var through = offset + chunk.Count == changes.Count ? next : chunk[^1].Seq;
                await applier.ApplyAsync([.. chunk.Select(c => c.Payload)], ct, through, id);
            }
            pulled += changes.Count;
            cursor = next;
            if (!more) return (pulled, snapshot);
        }
    }

    private async Task<(string State, long Cursor)> SnapshotAsync(string id, CancellationToken ct)
    {
        long? snapshotCursor = null;
        foreach (var kind in SyncCatalog.Ordered().Where(table => table.Mode != SyncMode.LocalOnly).Select(table => table.Kind))
        {
            string? after = null;
            while (true)
            {
                var path = $"v2/sync/snapshot?stream={id}&kind={Uri.EscapeDataString(kind)}&limit={SyncProtocol.MaxPullChanges}" + (after is null ? "" : $"&after={Uri.EscapeDataString(after)}");
                var response = await service.CallAsync(HttpMethod.Get, path, null, ct);
                if (!response.Ok) throw Refused(response);
                using var document = JsonDocument.Parse(response.Body);
                var root = document.RootElement;
                snapshotCursor ??= root.GetProperty("cursor").GetInt64();
                var changes = root.GetProperty("changes").EnumerateArray().Select(c => c.GetRawText()).ToList();
                for (var offset = 0; offset < changes.Count; offset += batchSize)
                    await applier.ApplyAsync([.. changes.Skip(offset).Take(batchSize)], ct);
                var next = root.GetProperty("next");
                var previous = after;
                after = next.ValueKind == JsonValueKind.String ? next.GetString() : null;
                if (!root.GetProperty("more").GetBoolean()) break;
                if (after is null || after == previous) throw new FormatException("The service returned an incomplete snapshot page.");
            }
        }
        var through = snapshotCursor ?? throw new FormatException("The service returned no snapshot cursor.");
        await applier.ApplyAsync([], ct, through, id);
        await store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "UPDATE SyncStreams SET State='active' WHERE StreamId=$id AND State='joining';";
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, ct);
        return await ReadAsync(id, ct);
    }

    private Task<(string State, long Cursor)> ReadAsync(string id, CancellationToken ct) => store.QueryAsync((session, _) =>
    {
        using var command = session.Connection().CreateCommand();
        command.CommandText = "SELECT State, Cursor FROM SyncStreams WHERE StreamId=$id AND State IN ('joining','active');";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new AppException(AppErrorKind.StorageUnavailable, "The stream is missing or being removed.");
        return Task.FromResult((reader.GetString(0), reader.GetInt64(1)));
    }, ct);

    private static Exception Refused(ServiceResponse response) => response.Status == 403
        ? new SyncStreamUnreadableException()
        : new ChaosServiceException(ServiceFailure.Refused, $"The online service refused the stream request ({response.Status}).");
}
