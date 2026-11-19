using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteOperationReceiptStore(SqliteConnection connection) : IOperationReceiptStore
{
    public async Task<IReadOnlyList<OperationReceiptRecord>> ListByKindAsync(string profileId, string commandKind, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM OperationReceipts WHERE ProfileId = $profile AND CommandKind = $kind ORDER BY rowid DESC;";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$kind", commandKind);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<OperationReceiptRecord>();
        while (await reader.ReadAsync(ct)) rows.Add(ReadReceipt(reader));
        return rows;
    }
    public async Task<OperationReceiptRecord?> FindAsync(string profileId, string operationId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM OperationReceipts WHERE ProfileId = $profile AND OperationId = $operation;";
        command.Parameters.AddWithValue("$profile", profileId);
        command.Parameters.AddWithValue("$operation", operationId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadReceipt(reader) : null;
    }

    public async Task AddAsync(OperationReceiptRecord receipt, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO OperationReceipts (ProfileId, OperationId, CommandKind, PayloadHash, ResultJson, CommittedAtUtc)
            VALUES ($profile, $operation, $kind, $hash, $result, $committed);
            """;
        command.Parameters.AddWithValue("$profile", receipt.ProfileId);
        command.Parameters.AddWithValue("$operation", receipt.OperationId);
        command.Parameters.AddWithValue("$kind", receipt.CommandKind);
        command.Parameters.AddWithValue("$hash", receipt.PayloadHash);
        command.Parameters.AddWithValue("$result", receipt.ResultJson);
        command.Parameters.AddWithValue("$committed", StorageFormats.Instant(receipt.CommittedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static OperationReceiptRecord ReadReceipt(SqliteDataReader r) => new()
    {
        ProfileId = r.S("ProfileId"),
        OperationId = r.S("OperationId"),
        CommandKind = r.S("CommandKind"),
        PayloadHash = r.S("PayloadHash"),
        ResultJson = r.S("ResultJson"),
        CommittedAtUtc = r.I("CommittedAtUtc"),
    };
}
