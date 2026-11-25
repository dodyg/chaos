using System.ComponentModel;
using Chaos.Sync;
using Microsoft.Extensions.AI;

namespace Chaos.Server.Sync;

/// <summary>
/// The service surface's agent tools of Q17 as Microsoft.Extensions.AI functions (PLAN3 12.2, 12.4). The MCP endpoint of
/// Q19 serves these objects as they are (<c>McpServerTool.Create(AIFunction)</c> in SDK 2.4.0), so the names and JSON
/// schemas the assistant sees are the ones made here, from the same contract the desktop's local tools use. Results carry
/// no user text; nothing in a task is ever an instruction.
/// </summary>
public sealed class AgentTools(AgentService agents)
{
    /// <summary>The tools bound to one agent. Its ID closes over every call; the agent is never an argument the caller picks.</summary>
    public IReadOnlyList<AIFunction> For(Guid agentId) => new Bound(agents, agentId).Functions;

    private sealed class Bound
    {
        private readonly AgentService _agents;
        private readonly Guid _agentId;

        public IReadOnlyList<AIFunction> Functions { get; }

        public Bound(AgentService agents, Guid agentId)
        {
            _agents = agents;
            _agentId = agentId;
            Functions =
            [
                AIFunctionFactory.Create(typeof(Bound).GetMethod(nameof(ClaimTaskAsync))!, this, AgentToolNames.ClaimTask, AgentToolNames.ClaimTaskDescription),
                AIFunctionFactory.Create(typeof(Bound).GetMethod(nameof(ReleaseTaskAsync))!, this, AgentToolNames.ReleaseTask, AgentToolNames.ReleaseTaskDescription),
                AIFunctionFactory.Create(typeof(Bound).GetMethod(nameof(HeartbeatAsync))!, this, AgentToolNames.Heartbeat, AgentToolNames.HeartbeatDescription),
            ];
        }

        public Task<ClaimToolResult> ClaimTaskAsync([Description("The ID of the task.")] string taskId, CancellationToken cancellationToken) =>
            Run(ClaimAction.Claim, taskId, cancellationToken);

        public Task<ClaimToolResult> ReleaseTaskAsync([Description("The ID of the task.")] string taskId, CancellationToken cancellationToken) =>
            Run(ClaimAction.Release, taskId, cancellationToken);

        public Task<ClaimToolResult> HeartbeatAsync([Description("The ID of the task.")] string taskId, CancellationToken cancellationToken) =>
            Run(ClaimAction.Heartbeat, taskId, cancellationToken);

        private async Task<ClaimToolResult> Run(ClaimAction action, string taskId, CancellationToken cancellationToken)
        {
            var outcome = await _agents.AgentClaimAsync(_agentId, taskId, action, cancellationToken);
            return new ClaimToolResult(outcome.Error ?? Status(action, outcome), taskId,
                outcome.Claim?.ClaimedById, outcome.Claim?.ClaimedAtUtc, outcome.Claim?.ClaimExpiresAtUtc, outcome.RetryAfterSeconds);
        }

        /// <summary>A heartbeat on a claim the agent does not hold changes nothing; otherwise it extended the claim.</summary>
        private static string Status(ClaimAction action, ClaimOperation outcome) => action switch
        {
            ClaimAction.Claim => AgentToolStatus.Claimed,
            ClaimAction.Release => outcome.Claim?.ClaimedById is null ? AgentToolStatus.Released : AgentToolStatus.NotClaimant,
            _ => AgentToolStatus.Extended,
        };
    }
}
