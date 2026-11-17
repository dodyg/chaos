using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>Attachment rows and this device's byte state (PLAN2 P9; migration 031).</summary>
public sealed class SqliteAttachmentStore(SqliteConnection connection) : IAttachmentStore
{
    public async Task<AttachmentRecord?> GetAsync(string id, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM Attachments WHERE Id = $id AND DeletedAtUtc IS NULL;", ct, ("$id", id))).FirstOrDefault();

    public async Task<AttachmentRecord?> GetIncludingDeletedAsync(string id, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM Attachments WHERE Id = $id;", ct, ("$id", id))).FirstOrDefault();

    public async Task<IReadOnlyList<AttachmentRecord>> ListByOwnerAsync(AttachmentOwnerKind kind, string ownerId, bool includeDeleted, CancellationToken ct) =>
        await ReadAsync(
            $"SELECT * FROM Attachments WHERE OwnerKind = $kind AND OwnerId = $owner{(includeDeleted ? "" : " AND DeletedAtUtc IS NULL")} ORDER BY CreatedAtUtc, Id;",
            ct, ("$kind", (long)(int)kind), ("$owner", ownerId));

    public async Task<IReadOnlyList<AttachmentRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct) =>
        await ReadAsync("SELECT * FROM Attachments WHERE SpaceId = $space AND DeletedAtUtc IS NULL ORDER BY CreatedAtUtc, Id;", ct, ("$space", spaceId));

    public async Task AddAsync(AttachmentRecord attachment, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Attachments (Id, SpaceId, OwnerKind, OwnerId, FileName, OriginalFileName, MediaType, ByteSize, Sha256, Width, Height, DurationMs, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $space, $kind, $owner, $name, $original, $media, $size, $sha, $width, $height, $duration, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", attachment.Id);
        command.Parameters.AddWithValue("$space", attachment.SpaceId);
        command.Parameters.AddWithValue("$kind", (long)(int)attachment.OwnerKind);
        command.Parameters.AddWithValue("$owner", attachment.OwnerId);
        command.Parameters.AddWithValue("$original", attachment.OriginalFileName);
        command.Parameters.AddWithValue("$media", attachment.MediaType);
        command.Parameters.AddWithValue("$size", attachment.ByteSize);
        command.Parameters.AddWithValue("$sha", attachment.Sha256);
        command.Parameters.AddWithValue("$width", (object?)attachment.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)attachment.Height ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (object?)attachment.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(attachment.CreatedAtUtc));
        Bind(command, attachment);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<AttachmentRecord?> UpdateAsync(AttachmentRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Attachments SET FileName = $name, MediaType = $media, ByteSize = $size, Sha256 = $sha, Width = $width, Height = $height,
                Version = $version, UpdatedAtUtc = $updated, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expected;
            """;
        command.Parameters.AddWithValue("$media", updated.MediaType);
        command.Parameters.AddWithValue("$size", updated.ByteSize);
        command.Parameters.AddWithValue("$sha", updated.Sha256);
        command.Parameters.AddWithValue("$width", (object?)updated.Width ?? DBNull.Value);
        command.Parameters.AddWithValue("$height", (object?)updated.Height ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        Bind(command, updated);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? updated : null;
    }

    public async Task<bool> IsHashReferencedAsync(string sha256, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM Attachments WHERE Sha256 = $sha);";
        command.Parameters.AddWithValue("$sha", sha256);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    public async Task<AttachmentLocalStateRecord?> GetLocalStateAsync(string sha256, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Sha256, State, PinnedReason, UpdatedAtUtc FROM AttachmentLocalState WHERE Sha256 = $sha;";
        command.Parameters.AddWithValue("$sha", sha256);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? new AttachmentLocalStateRecord(reader.GetString(0), (AttachmentBytesState)reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2), StorageFormats.ParseInstant(reader.GetString(3))!.Value)
            : null;
    }

    public async Task SetLocalStateAsync(AttachmentLocalStateRecord state, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AttachmentLocalState (Sha256, State, PinnedReason, UpdatedAtUtc) VALUES ($sha, $state, $pinned, $updated)
            ON CONFLICT (Sha256) DO UPDATE SET State = excluded.State, PinnedReason = excluded.PinnedReason, UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$sha", state.Sha256);
        command.Parameters.AddWithValue("$state", (long)(int)state.State);
        command.Parameters.AddWithValue("$pinned", (object?)state.PinnedReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(state.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(SqliteCommand command, AttachmentRecord attachment)
    {
        command.Parameters.AddWithValue("$name", attachment.FileName);
        command.Parameters.AddWithValue("$version", attachment.Version);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(attachment.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", StorageFormats.Optional(attachment.DeletedAtUtc) ?? DBNull.Value);
    }

    private async Task<IReadOnlyList<AttachmentRecord>> ReadAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<AttachmentRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AttachmentRecord
            {
                Id = reader.S("Id"),
                SpaceId = reader.S("SpaceId"),
                OwnerKind = (AttachmentOwnerKind)(int)reader.L("OwnerKind"),
                OwnerId = reader.S("OwnerId"),
                FileName = reader.S("FileName"),
                OriginalFileName = reader.S("OriginalFileName"),
                MediaType = reader.S("MediaType"),
                ByteSize = reader.L("ByteSize"),
                Sha256 = reader.S("Sha256"),
                Width = (int?)reader.Ln("Width"),
                Height = (int?)reader.Ln("Height"),
                DurationMs = reader.Ln("DurationMs"),
                Version = reader.L("Version"),
                CreatedAtUtc = reader.I("CreatedAtUtc"),
                UpdatedAtUtc = reader.I("UpdatedAtUtc"),
                DeletedAtUtc = reader.In("DeletedAtUtc"),
            });
        }

        return rows;
    }
}
