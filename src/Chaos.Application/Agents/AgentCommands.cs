using System.Security.Cryptography;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Chaos.Application.Reminders;
using Chaos.Application.Tasks;
using Chaos.Sync;

namespace Chaos.Application.Agents;

public sealed record CreateAgentCommand(string OperationId, string Name);
public sealed record RenameAgentCommand(string OperationId, string AgentId, long ExpectedVersion, string Name);
public sealed record SetAgentPausedCommand(string OperationId, string AgentId, long ExpectedVersion, bool Paused);
public sealed record RemoveAgentCommand(string OperationId, string AgentId, long ExpectedVersion);
public sealed record CreateAgentTokenCommand(string OperationId, string AgentId, long ExpectedVersion, string Name);
public sealed record RevokeAgentTokenCommand(string OperationId, string AgentId, string TokenId);
/// <summary>Grants or revokes the agent's access to one list of the Personal space.</summary>
public sealed record SetAgentAccessCommand(string OperationId, string AgentId, string ContainerId, bool Allowed);
/// <summary>A token as it is returned once: the row, and the secret the person copies into their MCP client.</summary>
public sealed record AgentTokenCreated(LocalAgentTokenRecord Token, string Secret);
public sealed record AgentView(LocalAgentRecord Agent, IReadOnlyList<LocalAgentAccessRecord> Access, IReadOnlyList<LocalAgentTokenRecord> Tokens);

/// <summary>
/// Local agents (PLAN3 12.3): the person's own assistants, working through the local MCP surface in the Personal lists
/// the person ticks. A token is returned once and kept as a SHA-256; it is never written to a receipt, a log or sync.
/// Removing or pausing an agent releases its claims.
/// </summary>
public sealed class AgentCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public Task<IReadOnlyList<AgentView>> ListAsync(CancellationToken ct = default) =>
        store.QueryAsync<IReadOnlyList<AgentView>>(async (session, token) =>
        {
            var agents = await session.Agents.ListAsync(token);
            var views = new List<AgentView>();
            foreach (var agent in agents)
            {
                views.Add(new AgentView(agent, await session.Agents.ListAccessAsync(agent.Id, includeRevoked: false, token),
                    await session.Agents.ListTokensAsync(agent.Id, token)));
            }

            return views;
        }, ct);

    public Task<LocalAgentRecord> CreateAsync(CreateAgentCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        var name = ValidName(command.Name);
        return TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var agent = new LocalAgentRecord
            {
                Id = ids.NewId().ToString(), Name = name, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now,
            };
            await session.Agents.AddAsync(agent, token);
            session.Activity.Note("local_agent", agent.Id, ActivityOp.Upsert, agent.Version);
            return agent;
        }, ct);
    }

    public Task<LocalAgentRecord> RenameAsync(RenameAgentCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        var name = ValidName(command.Name);
        return MutateAsync(command.OperationId, command.AgentId, command.ExpectedVersion, async (session, agent, now, token) =>
        {
            var updated = agent with { Name = name, Version = agent.Version + 1, UpdatedAtUtc = now };
            await SaveAsync(session, updated, agent.Version, token);
            return updated;
        }, ct);
    }

    /// <summary>A pause releases the agent's claims; a paused agent's tools answer <c>paused</c> (12.3).</summary>
    public Task<LocalAgentRecord> SetPausedAsync(SetAgentPausedCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        return MutateAsync(command.OperationId, command.AgentId, command.ExpectedVersion, async (session, agent, now, token) =>
        {
            if (command.Paused)
            {
                await ClaimCommands.ReleaseAgentClaimsAsync(session, ids, agent.Id, now, "the agent was paused", token);
            }

            var updated = agent with { PausedAtUtc = command.Paused ? now : null, Version = agent.Version + 1, UpdatedAtUtc = now };
            await SaveAsync(session, updated, agent.Version, token);
            return updated;
        }, ct);
    }

    public Task<LocalAgentRecord> RemoveAsync(RemoveAgentCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        return MutateAsync(command.OperationId, command.AgentId, command.ExpectedVersion, async (session, agent, now, token) =>
        {
            await ClaimCommands.ReleaseAgentClaimsAsync(session, ids, agent.Id, now, "the agent was removed", token);
            await session.Agents.RevokeTokensAsync(agent.Id, now, token);
            foreach (var access in await session.Agents.ListAccessAsync(agent.Id, includeRevoked: false, token))
            {
                await session.Agents.SaveAccessAsync(access with { DeletedAtUtc = now, Version = access.Version + 1, UpdatedAtUtc = now }, token);
            }

            var updated = agent with { DeletedAtUtc = now, Version = agent.Version + 1, UpdatedAtUtc = now };
            await SaveAsync(session, updated, agent.Version, token);
            return updated;
        }, ct);
    }

    /// <summary>Makes a token for an MCP client configuration. The secret exists only in this result.</summary>
    public Task<AgentTokenCreated> CreateTokenAsync(CreateAgentTokenCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        var name = ValidName(command.Name);
        return TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            // No receipt: the result holds the secret, and a receipt would store it.
            if (await session.Receipts.FindAsync(profile.Id, command.OperationId, token) is not null)
            {
                throw AppException.OperationConflict("This operation ID was already used. Use a new one for a new token.");
            }

            var agent = await session.Agents.GetAsync(command.AgentId, token)
                ?? throw AppException.NotFound("That agent no longer exists. Refresh and try again.");
            RequireCurrentVersion(agent, command.ExpectedVersion);
            var secret = Token();
            var row = new LocalAgentTokenRecord
            {
                Id = ids.NewId().ToString(), AgentId = agent.Id, Name = name, TokenHash = HashToken(secret), CreatedAtUtc = now,
            };
            await session.Agents.AddTokenAsync(row, token);
            session.Activity.Note("local_agent_token", row.Id, ActivityOp.Upsert);
            await session.Receipts.AddAsync(new OperationReceiptRecord
            {
                ProfileId = profile.Id, OperationId = command.OperationId, CommandKind = "agents.token.create",
                PayloadHash = StoredJsonFormats.Hash(new { command.AgentId, name }), ResultJson = "{}", CommittedAtUtc = now,
            }, token);
            return new AgentTokenCreated(row, Secret: secret);
        }, ct);
    }

    public Task<RevokeAgentTokenCommand> RevokeTokenAsync(RevokeAgentTokenCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        return TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var row = await session.Agents.FindTokenAsync(command.TokenId, token)
                ?? throw AppException.NotFound("That token no longer exists. Refresh and try again.");
            if (row.AgentId != command.AgentId) throw AppException.NotFound("That token no longer exists. Refresh and try again.");
            await session.Agents.RevokeTokenAsync(row.Id, now, token);
            session.Activity.Note("local_agent_token", row.Id, ActivityOp.Delete);
            return command;
        }, ct);
    }

    public Task<LocalAgentAccessRecord> SetAccessAsync(SetAgentAccessCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        return TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var agent = await session.Agents.GetAsync(command.AgentId, token)
                ?? throw AppException.NotFound("That agent no longer exists. Refresh and try again.");
            var container = await session.Containers.GetAsync(command.ContainerId, token)
                ?? throw AppException.Validation("containerId", "Choose a list that still exists.");
            var personal = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token);
            if (personal is null || container.SpaceId != personal.Id)
            {
                throw AppException.Validation("containerId", "A local agent works in lists of your Personal space. For a shared space, add a server agent to it.");
            }

            var current = await session.Agents.FindAccessAsync(agent.Id, container.Id, token);
            LocalAgentAccessRecord updated;
            if (command.Allowed)
            {
                updated = current is null
                    ? new LocalAgentAccessRecord
                    {
                        Id = agent.Id + "|" + container.Id, AgentId = agent.Id, ContainerId = container.Id, GrantedAtUtc = now, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now,
                    }
                    : current.DeletedAtUtc is null ? current : current with { DeletedAtUtc = null, GrantedAtUtc = now, Version = current.Version + 1, UpdatedAtUtc = now };
            }
            else
            {
                if (current is null || current.DeletedAtUtc is not null)
                {
                    return current ?? new LocalAgentAccessRecord
                    {
                        Id = agent.Id + "|" + container.Id, AgentId = agent.Id, ContainerId = container.Id, GrantedAtUtc = now, DeletedAtUtc = now, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now,
                    };
                }

                updated = current with { DeletedAtUtc = now, Version = current.Version + 1, UpdatedAtUtc = now };
            }

            await session.Agents.SaveAccessAsync(updated, token);
            session.Activity.Note("local_agent_access", updated.Id, command.Allowed ? ActivityOp.Upsert : ActivityOp.Delete, updated.Version);
            return updated;
        }, ct);
    }

    private Task<LocalAgentRecord> MutateAsync(string operationId, string agentId, long expectedVersion,
        Func<IDataSession, LocalAgentRecord, DateTimeOffset, CancellationToken, Task<LocalAgentRecord>> body, CancellationToken ct) =>
        TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var agent = await session.Agents.GetAsync(agentId, token)
                ?? throw AppException.NotFound("That agent no longer exists. Refresh and try again.");
            RequireCurrentVersion(agent, expectedVersion);
            var updated = await body(session, agent, now, token);
            await session.Receipts.AddAsync(new OperationReceiptRecord
            {
                ProfileId = profile.Id, OperationId = operationId, CommandKind = "agents.mutate",
                PayloadHash = StoredJsonFormats.Hash(new { agentId, expectedVersion }), ResultJson = "{}", CommittedAtUtc = now,
            }, token);
            return updated;
        }, ct);

    private static void RequireCurrentVersion(LocalAgentRecord agent, long expectedVersion)
    {
        if (agent.Version != expectedVersion)
        {
            throw AppException.VersionConflict("The agent changed elsewhere while you were working. Refresh and try again.");
        }
    }

    private static async Task SaveAsync(IDataSession session, LocalAgentRecord updated, long expectedVersion, CancellationToken token)
    {
        _ = await session.Agents.UpdateAsync(updated, expectedVersion, token)
            ?? throw AppException.VersionConflict("The agent changed elsewhere. Refresh and try again.");
        session.Activity.Note("local_agent", updated.Id, ActivityOp.Upsert, updated.Version);
    }

    /// <summary>The secret an MCP client presents: 32 random bytes, URL-safe Base64.</summary>
    internal static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>What the device keeps instead of the secret: lowercase hex SHA-256.</summary>
    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    internal static string ValidName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.EnumerateRunes().Count() > AgentLimits.MaxNameLength)
        {
            throw AppException.Validation("name", $"Use 1 to {AgentLimits.MaxNameLength} characters.");
        }

        return trimmed;
    }
}
