using Chaos.Application.Abstractions;
using Chaos.Application.People;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

internal sealed class SqlitePeopleStore(SqliteConnection connection) : IPeopleStore
{
    public async Task<IReadOnlyList<SpacePerson>> ListAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MemberId, MemberKind, DisplayName, Role, RemovedAtUtc, GitHubLogin FROM SpaceMembers WHERE SpaceId=$space ORDER BY DisplayName COLLATE NOCASE, MemberId;";
        command.Parameters.AddWithValue("$space", spaceId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<SpacePerson>();
        while (await reader.ReadAsync(ct))
            result.Add(new SpacePerson((reader.GetInt64(1) == 1 ? "person:" : "agent:") + reader.GetString(0), reader.GetString(2), reader.GetInt32(3), !reader.IsDBNull(4), reader.IsDBNull(5) ? null : reader.GetString(5)));
        return result;
    }
}
