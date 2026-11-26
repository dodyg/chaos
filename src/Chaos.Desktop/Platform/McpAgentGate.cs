using Chaos.Application.Abstractions;
using Chaos.Application.Agents;
using Microsoft.Extensions.AI;

namespace Chaos.Desktop.Platform;

/// <summary>
/// The gate of the local MCP pipe over the device's own data: a token is looked up by its SHA-256 among live tokens of live
/// agents, and the agent's tools are the ones <see cref="LocalAgentTools"/> makes. No account and no network is involved.
/// </summary>
public sealed class McpAgentGate(IApplicationStore store, LocalAgentTools tools, TimeProvider clock) : IMcpAgentGate
{
    public async Task<IReadOnlyList<AIFunction>?> AuthenticateAsync(string token, CancellationToken ct)
    {
        var hash = AgentCommands.HashToken(token);
        var row = await store.QueryAsync((session, inner) => session.Agents.FindTokenByHashAsync(hash, inner), ct);
        if (row is null)
        {
            return null;
        }

        await store.ExecuteAsync(async (session, inner) =>
        {
            await session.Agents.TouchTokenAsync(row.Id, clock.GetUtcNow(), inner);
            return true;
        }, ct);
        return tools.For(row.AgentId);
    }
}
