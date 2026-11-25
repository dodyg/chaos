using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>Local agents, their list access and tokens, and task proposals (migration 050).</summary>
public sealed class SqliteAgentStore(SqliteConnection connection) : IAgentStore
{
    public Task<IReadOnlyList<LocalAgentRecord>> ListAsync(CancellationToken ct) =>
        ReadAgentsAsync("SELECT * FROM LocalAgents WHERE DeletedAtUtc IS NULL ORDER BY Name COLLATE NOCASE, Id;", ct);

    public async Task<LocalAgentRecord?> GetAsync(string agentId, CancellationToken ct) =>
        (await ReadAgentsAsync("SELECT * FROM LocalAgents WHERE Id = $id AND DeletedAtUtc IS NULL;", ct, ("$id", agentId))).FirstOrDefault();

    public async Task<LocalAgentRecord?> GetIncludingDeletedAsync(string agentId, CancellationToken ct) =>
        (await ReadAgentsAsync("SELECT * FROM LocalAgents WHERE Id = $id;", ct, ("$id", agentId))).FirstOrDefault();

    public async Task AddAsync(LocalAgentRecord agent, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LocalAgents (Id, Name, PausedAtUtc, DeletedAtUtc, Version, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, $name, $paused, $deleted, $version, $created, $updated);
            """;
        Bind(command, agent);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<LocalAgentRecord?> UpdateAsync(LocalAgentRecord agent, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE LocalAgents SET Name = $name, PausedAtUtc = $paused, DeletedAtUtc = $deleted, Version = $version, UpdatedAtUtc = $updated
            WHERE Id = $id AND Version = $expected;
            """;
        Bind(command, agent);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? agent : null;
    }

    public Task<IReadOnlyList<LocalAgentAccessRecord>> ListAccessAsync(string agentId, bool includeRevoked, CancellationToken ct) =>
        ReadAccessAsync("SELECT * FROM LocalAgentAccess WHERE AgentId = $agent" + (includeRevoked ? "" : " AND DeletedAtUtc IS NULL") + " ORDER BY ContainerId;", ct, ("$agent", agentId));

    public async Task<LocalAgentAccessRecord?> FindAccessAsync(string agentId, string containerId, CancellationToken ct) =>
        (await ReadAccessAsync("SELECT * FROM LocalAgentAccess WHERE AgentId = $agent AND ContainerId = $container;", ct, ("$agent", agentId), ("$container", containerId))).FirstOrDefault();

    public async Task SaveAccessAsync(LocalAgentAccessRecord access, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LocalAgentAccess (AgentId, ContainerId, GrantedAtUtc, DeletedAtUtc, Version, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($agent, $container, $granted, $deleted, $version, $created, $updated)
            ON CONFLICT(AgentId, ContainerId) DO UPDATE SET GrantedAtUtc = excluded.GrantedAtUtc, DeletedAtUtc = excluded.DeletedAtUtc,
                Version = excluded.Version, UpdatedAtUtc = excluded.UpdatedAtUtc;
            """;
        command.Parameters.AddWithValue("$agent", access.AgentId);
        command.Parameters.AddWithValue("$container", access.ContainerId);
        command.Parameters.AddWithValue("$granted", StorageFormats.Instant(access.GrantedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(access.DeletedAtUtc));
        command.Parameters.AddWithValue("$version", access.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(access.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(access.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AddTokenAsync(LocalAgentTokenRecord token, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LocalAgentTokens (Id, AgentId, Name, TokenHash, CreatedAtUtc, LastUsedAtUtc, RevokedAtUtc)
            VALUES ($id, $agent, $name, $hash, $created, $used, $revoked);
            """;
        command.Parameters.AddWithValue("$id", token.Id);
        command.Parameters.AddWithValue("$agent", token.AgentId);
        command.Parameters.AddWithValue("$name", token.Name);
        command.Parameters.AddWithValue("$hash", token.TokenHash);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(token.CreatedAtUtc));
        command.Parameters.AddWithValue("$used", SqliteProfileStore.OptionalInstant(token.LastUsedAtUtc));
        command.Parameters.AddWithValue("$revoked", SqliteProfileStore.OptionalInstant(token.RevokedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<LocalAgentTokenRecord>> ListTokensAsync(string agentId, CancellationToken ct) =>
        ReadTokensAsync("SELECT * FROM LocalAgentTokens WHERE AgentId = $agent AND RevokedAtUtc IS NULL ORDER BY CreatedAtUtc, Id;", ct, ("$agent", agentId));

    public async Task<LocalAgentTokenRecord?> FindTokenByHashAsync(string tokenHash, CancellationToken ct) =>
        (await ReadTokensAsync("""
            SELECT t.* FROM LocalAgentTokens t JOIN LocalAgents a ON a.Id = t.AgentId AND a.DeletedAtUtc IS NULL
            WHERE t.TokenHash = $hash AND t.RevokedAtUtc IS NULL;
            """, ct, ("$hash", tokenHash))).FirstOrDefault();

    public async Task<LocalAgentTokenRecord?> FindTokenAsync(string tokenId, CancellationToken ct) =>
        (await ReadTokensAsync("SELECT * FROM LocalAgentTokens WHERE Id = $id;", ct, ("$id", tokenId))).FirstOrDefault();

    public Task TouchTokenAsync(string tokenId, DateTimeOffset at, CancellationToken ct) =>
        ExecuteAsync("UPDATE LocalAgentTokens SET LastUsedAtUtc = $at WHERE Id = $id;", ct, ("$at", StorageFormats.Instant(at)), ("$id", tokenId));

    public Task RevokeTokenAsync(string tokenId, DateTimeOffset at, CancellationToken ct) =>
        ExecuteAsync("UPDATE LocalAgentTokens SET RevokedAtUtc = $at WHERE Id = $id AND RevokedAtUtc IS NULL;", ct, ("$at", StorageFormats.Instant(at)), ("$id", tokenId));

    public Task RevokeTokensAsync(string agentId, DateTimeOffset at, CancellationToken ct) =>
        ExecuteAsync("UPDATE LocalAgentTokens SET RevokedAtUtc = $at WHERE AgentId = $agent AND RevokedAtUtc IS NULL;", ct, ("$at", StorageFormats.Instant(at)), ("$agent", agentId));

    public async Task AddProposalAsync(TaskProposalRecord proposal, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TaskProposals (Id, SpaceId, AgentId, SourcePadItemId, Title, NotesMarkdown, QuickAddText, State, CreatedTaskId, DecidedAtUtc,
                Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $space, $agent, $pad, $title, $notes, $quick, $state, $task, $decided, $version, $created, $updated, $deleted);
            """;
        Bind(command, proposal);
        await command.ExecuteNonQueryAsync(ct);
    }

    public Task<IReadOnlyList<TaskProposalRecord>> ListProposalsAsync(string spaceId, CancellationToken ct) =>
        ReadProposalsAsync("SELECT * FROM TaskProposals WHERE SpaceId = $space AND DeletedAtUtc IS NULL ORDER BY CreatedAtUtc, Id;", ct, ("$space", spaceId));

    public async Task<TaskProposalRecord?> UpdateProposalAsync(TaskProposalRecord proposal, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE TaskProposals SET State = $state, CreatedTaskId = $task, DecidedAtUtc = $decided, DeletedAtUtc = $deleted,
                Version = $version, UpdatedAtUtc = $updated
            WHERE Id = $id AND Version = $expected;
            """;
        Bind(command, proposal);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? proposal : null;
    }

    /// <summary>The agent's hourly counter: its activity-log rows of one action since the window started.</summary>
    public async Task<int> CountActionsAsync(string actorId, string action, DateTimeOffset since, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ActivityLog WHERE Source = 'agent' AND ActorId = $actor AND Action = $action AND OccurredAtUtc > $since;";
        command.Parameters.AddWithValue("$actor", actorId);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$since", StorageFormats.Instant(since));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<DateTimeOffset?> FirstActionAsync(string actorId, string action, DateTimeOffset since, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(OccurredAtUtc) FROM ActivityLog WHERE Source = 'agent' AND ActorId = $actor AND Action = $action AND OccurredAtUtc > $since;";
        command.Parameters.AddWithValue("$actor", actorId);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$since", StorageFormats.Instant(since));
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : StorageFormats.ParseInstant((string)value);
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Bind(SqliteCommand command, LocalAgentRecord agent)
    {
        command.Parameters.AddWithValue("$id", agent.Id);
        command.Parameters.AddWithValue("$name", agent.Name);
        command.Parameters.AddWithValue("$paused", SqliteProfileStore.OptionalInstant(agent.PausedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(agent.DeletedAtUtc));
        command.Parameters.AddWithValue("$version", agent.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(agent.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(agent.UpdatedAtUtc));
    }

    private async Task<IReadOnlyList<LocalAgentRecord>> ReadAgentsAsync(string sql, CancellationToken ct, params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<LocalAgentRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new LocalAgentRecord
            {
                Id = reader.S("Id"), Name = reader.S("Name"), PausedAtUtc = reader.In("PausedAtUtc"), DeletedAtUtc = reader.In("DeletedAtUtc"),
                Version = reader.L("Version"), CreatedAtUtc = reader.I("CreatedAtUtc"), UpdatedAtUtc = reader.I("UpdatedAtUtc"),
            });
        }

        return rows;
    }

    private async Task<IReadOnlyList<LocalAgentAccessRecord>> ReadAccessAsync(string sql, CancellationToken ct, params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<LocalAgentAccessRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new LocalAgentAccessRecord
            {
                Id = reader.S("AgentId") + "|" + reader.S("ContainerId"), AgentId = reader.S("AgentId"), ContainerId = reader.S("ContainerId"),
                GrantedAtUtc = reader.I("GrantedAtUtc"), DeletedAtUtc = reader.In("DeletedAtUtc"), Version = reader.L("Version"),
                CreatedAtUtc = reader.I("CreatedAtUtc"), UpdatedAtUtc = reader.I("UpdatedAtUtc"),
            });
        }

        return rows;
    }

    private async Task<IReadOnlyList<LocalAgentTokenRecord>> ReadTokensAsync(string sql, CancellationToken ct, params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<LocalAgentTokenRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new LocalAgentTokenRecord
            {
                Id = reader.S("Id"), AgentId = reader.S("AgentId"), Name = reader.S("Name"), TokenHash = reader.S("TokenHash"),
                CreatedAtUtc = reader.I("CreatedAtUtc"), LastUsedAtUtc = reader.In("LastUsedAtUtc"), RevokedAtUtc = reader.In("RevokedAtUtc"),
            });
        }

        return rows;
    }

    private async Task<IReadOnlyList<TaskProposalRecord>> ReadProposalsAsync(string sql, CancellationToken ct, params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<TaskProposalRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TaskProposalRecord
            {
                Id = reader.S("Id"), SpaceId = reader.S("SpaceId"), AgentId = reader.S("AgentId"), SourcePadItemId = reader.Sn("SourcePadItemId"),
                Title = reader.S("Title"), NotesMarkdown = reader.S("NotesMarkdown"), QuickAddText = reader.Sn("QuickAddText"),
                State = (ProposalState)reader.L("State"), CreatedTaskId = reader.Sn("CreatedTaskId"), DecidedAtUtc = reader.In("DecidedAtUtc"),
                Version = reader.L("Version"), CreatedAtUtc = reader.I("CreatedAtUtc"), UpdatedAtUtc = reader.I("UpdatedAtUtc"), DeletedAtUtc = reader.In("DeletedAtUtc"),
            });
        }

        return rows;
    }

    private static void Bind(SqliteCommand command, TaskProposalRecord proposal)
    {
        command.Parameters.AddWithValue("$id", proposal.Id);
        command.Parameters.AddWithValue("$space", proposal.SpaceId);
        command.Parameters.AddWithValue("$agent", proposal.AgentId);
        command.Parameters.AddWithValue("$pad", SqliteProfileStore.OptionalValue(proposal.SourcePadItemId));
        command.Parameters.AddWithValue("$title", proposal.Title);
        command.Parameters.AddWithValue("$notes", proposal.NotesMarkdown);
        command.Parameters.AddWithValue("$quick", SqliteProfileStore.OptionalValue(proposal.QuickAddText));
        command.Parameters.AddWithValue("$state", (long)proposal.State);
        command.Parameters.AddWithValue("$task", SqliteProfileStore.OptionalValue(proposal.CreatedTaskId));
        command.Parameters.AddWithValue("$decided", SqliteProfileStore.OptionalInstant(proposal.DecidedAtUtc));
        command.Parameters.AddWithValue("$version", proposal.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(proposal.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(proposal.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(proposal.DeletedAtUtc));
    }
}

