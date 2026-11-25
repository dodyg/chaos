using System.ComponentModel;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Tasks;
using Chaos.Sync;
using Microsoft.Extensions.AI;

namespace Chaos.Application.Agents;

/// <summary>
/// The local surface's agent tools of Q17 as Microsoft.Extensions.AI functions (PLAN3 12.2, 12.4). The pipe host of Q18
/// serves these objects as they are (<c>McpServerTool.Create(AIFunction)</c> in SDK 2.4.0), so the names and JSON
/// schemas the assistant sees are the ones made here, from the same contract the service's tools use. Every call runs
/// the agent rules of section 12 first, then the Application command under an agent scope, so its history rows and
/// undo entries name <c>agent:&lt;id&gt;</c>. Results carry no user text; nothing in a task is ever an instruction.
/// </summary>
public sealed class LocalAgentTools(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    /// <summary>The tools bound to one agent. Its ID closes over every call; the agent is never an argument the caller picks.</summary>
    public IReadOnlyList<AIFunction> For(string agentId) => new Bound(store, ids, clock, agentId).Functions;

    private sealed class Bound
    {
        private readonly IApplicationStore _store;
        private readonly IIdGenerator _ids;
        private readonly TimeProvider _clock;
        private readonly string _agentId;
        private readonly string _actor;

        public IReadOnlyList<AIFunction> Functions { get; }

        public Bound(IApplicationStore store, IIdGenerator ids, TimeProvider clock, string agentId)
        {
            _store = store;
            _ids = ids;
            _clock = clock;
            _agentId = agentId;
            _actor = $"agent:{agentId}";
            Functions =
            [
                AIFunctionFactory.Create(typeof(Bound).GetMethod(nameof(ClaimTaskAsync))!, this, AgentToolNames.ClaimTask, AgentToolNames.ClaimTaskDescription),
                AIFunctionFactory.Create(typeof(Bound).GetMethod(nameof(ReleaseTaskAsync))!, this, AgentToolNames.ReleaseTask, AgentToolNames.ReleaseTaskDescription),
                AIFunctionFactory.Create(typeof(Bound).GetMethod(nameof(HeartbeatAsync))!, this, AgentToolNames.Heartbeat, AgentToolNames.HeartbeatDescription),
            ];
        }

        public Task<ClaimToolResult> ClaimTaskAsync([Description("The ID of the task.")] string taskId, CancellationToken cancellationToken) =>
            RunAsync(ClaimAction.Claim, ActivityActions.AgentClaim, taskId, cancellationToken);

        public Task<ClaimToolResult> ReleaseTaskAsync([Description("The ID of the task.")] string taskId, CancellationToken cancellationToken) =>
            RunAsync(ClaimAction.Release, ActivityActions.AgentRelease, taskId, cancellationToken);

        public Task<ClaimToolResult> HeartbeatAsync([Description("The ID of the task.")] string taskId, CancellationToken cancellationToken) =>
            RunAsync(ClaimAction.Heartbeat, ActivityActions.AgentHeartbeat, taskId, cancellationToken);

        private async Task<ClaimToolResult> RunAsync(ClaimAction action, string activityAction, string taskId, CancellationToken cancellationToken)
        {
            // One operation ID for the activity log row and the history and undo of this call, so they can be joined.
            var operationId = Guid.NewGuid().ToString("D");
            using var scope = ActivityScope.Open(ActivitySources.Agent, activityAction, operationId: operationId, actorId: _actor);
            return await TaskMutationSupport.InRecordedTransactionAsync(_store, _clock, _ids,
                (session, profile, now, token) => ApplyAsync(session, profile, now, action, activityAction, taskId, operationId, token), cancellationToken);
        }

        /// <summary>The agent rules (12.3, 12.5, E5) and then the claim, in one transaction of the serialized writer.</summary>
        private async Task<ClaimToolResult> ApplyAsync(RecordingDataSession session, ProfileRecord profile, DateTimeOffset now, ClaimAction action, string activityAction,
            string taskId, string operationId, CancellationToken token)
        {
            var agent = await session.Agents.GetAsync(_agentId, token);
            var task = agent is null ? null : await session.Tasks.GetAsync(taskId, token);
            if (agent is null || task is null)
            {
                return new ClaimToolResult(AgentToolStatus.NotFound, taskId);
            }

            if (agent.PausedAtUtc is not null)
            {
                return Result(AgentToolStatus.Paused, task);
            }

            // A local agent works in the Personal lists the person ticked, never in a shared space (12.3).
            var personal = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token);
            var access = await session.Agents.FindAccessAsync(agent.Id, task.ContainerId, token);
            if (personal is null || task.SpaceId != personal.Id || access is null || access.DeletedAtUtc is not null)
            {
                return Result(AgentToolStatus.Forbidden, task);
            }

            if (task.AgentAssigneeId is { Length: > 0 } assigned && !string.Equals(assigned, agent.Id, StringComparison.OrdinalIgnoreCase))
            {
                return Result(AgentToolStatus.Forbidden, task); // pointed at another agent
            }

            if (action == ClaimAction.Claim)
            {
                var since = now - AgentLimits.Window;
                if (await session.Agents.CountActionsAsync(_actor, ActivityActions.AgentClaim, since, token) >= AgentLimits.ClaimsPerHour)
                {
                    var first = await session.Agents.FirstActionAsync(_actor, ActivityActions.AgentClaim, since, token) ?? now;
                    return Result(AgentToolStatus.RateLimited, task) with { RetryAfterSeconds = (int)Math.Max(1, (first + AgentLimits.Window - now).TotalSeconds) };
                }
            }

            // One recorded operation per tool call, under the agent's name: its history rows and undo entry say agent:<id>.
            session.Begin(operationId, activityAction);
            var outcome = await ClaimCommands.ApplyAsync(session, _ids, task, new ActorId(ActorKind.Agent, Guid.Parse(agent.Id)), action, now, token);
            return outcome.Task is { } stored ? Result(outcome.Error ?? Status(action, stored), stored) : new ClaimToolResult(outcome.Error!, taskId);
        }

        private static ClaimToolResult Result(string status, TaskRecord task) =>
            new(status, task.Id, task.ClaimedById, task.ClaimedAtUtc is { } at ? StorageFormats.Instant(at) : null,
                task.ClaimExpiresAtUtc is { } until ? StorageFormats.Instant(until) : null);

        private static string Status(ClaimAction action, TaskRecord task) => action switch
        {
            ClaimAction.Claim => AgentToolStatus.Claimed,
            ClaimAction.Release => task.ClaimedById is null ? AgentToolStatus.Released : AgentToolStatus.NotClaimant,
            _ => AgentToolStatus.Extended,
        };
    }
}
