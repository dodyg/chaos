using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;
using Chaos.Sync;

namespace Chaos.Application.Spaces;

public sealed record CreateSpaceStateCommand(string OperationId, string SpaceId, string Name, TaskItemStatus BaseStatus);
public sealed record DeleteSpaceStateCommand(string OperationId, string StateId, long ExpectedVersion);
public sealed record SpaceStateMutationResult(SpaceStateRecord State, bool Replayed);

/// <summary>The agent settings of a space (PLAN3 12.3, 12.4, E8).</summary>
public sealed record SpaceAgentSettings(bool AgentsAllowed, int ClaimMinutes, bool AllowAgentsToClose)
{
    public static SpaceAgentSettings Of(SpaceRecord space) => new(space.AgentsAllowed, space.ClaimMinutes, space.AllowAgentsToClose);
}

/// <summary>Owner-managed states retain a base status for existing views and integrations.</summary>
public sealed class SpaceStateCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public Task<bool> GetTagsOwnerOnlyAsync(string spaceId, CancellationToken ct = default) =>
        store.QueryAsync(async (session, token) => (await session.Spaces.GetAsync(spaceId, token))?.TagsOwnerOnly
            ?? throw AppException.NotFound("That space is unavailable."), ct);

    public Task SetTagsOwnerOnlyAsync(string spaceId, bool ownerOnly, CancellationToken ct = default) =>
        store.ExecuteAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Your profile is unavailable.");
            var space = await session.Spaces.GetAsync(spaceId, token) ?? throw AppException.NotFound("That space is unavailable.");
            await RequireOwnerAsync(session, space, profile, token);
            if (space.TagsOwnerOnly != ownerOnly) await session.Spaces.SetTagsOwnerOnlyAsync(spaceId, ownerOnly, clock.GetUtcNow(), token);
            return true;
        }, ct);

    /// <summary>The space's agent settings (PLAN3 12.3, 12.4): whether agents may be added, how many minutes a claim lasts, whether an agent may close a task.</summary>
    public Task<SpaceAgentSettings> GetAgentSettingsAsync(string spaceId, CancellationToken ct = default) =>
        store.QueryAsync(async (session, token) => SpaceAgentSettings.Of(await session.Spaces.GetAsync(spaceId, token)
            ?? throw AppException.NotFound("That space is unavailable.")), ct);

    /// <summary>Changes the given agent settings; an owner of the space only. The change syncs like any other space setting.</summary>
    public Task<SpaceAgentSettings> SetAgentSettingsAsync(string spaceId, bool? agentsAllowed, int? claimMinutes, bool? allowAgentsToClose, CancellationToken ct = default) =>
        store.ExecuteAsync(async (session, token) =>
        {
            if (claimMinutes is { } minutes && (minutes < AgentLimits.MinClaimMinutes || minutes > AgentLimits.MaxClaimMinutes))
                throw AppException.Validation("claimMinutes", $"Choose {AgentLimits.MinClaimMinutes} to {AgentLimits.MaxClaimMinutes} minutes.");
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Your profile is unavailable.");
            var space = await session.Spaces.GetAsync(spaceId, token) ?? throw AppException.NotFound("That space is unavailable.");
            await RequireOwnerAsync(session, space, profile, token);
            var current = SpaceAgentSettings.Of(space);
            var changed = (agentsAllowed is { } allowed && allowed != current.AgentsAllowed)
                || (claimMinutes is { } requested && requested != current.ClaimMinutes)
                || (allowAgentsToClose is { } close && close != current.AllowAgentsToClose);
            if (!changed)
            {
                return current;
            }

            await session.Spaces.SetAgentSettingsAsync(spaceId, agentsAllowed, claimMinutes, allowAgentsToClose, clock.GetUtcNow(), token);
            return new SpaceAgentSettings(agentsAllowed ?? current.AgentsAllowed, claimMinutes ?? current.ClaimMinutes, allowAgentsToClose ?? current.AllowAgentsToClose);
        }, ct);
    public Task<IReadOnlyList<SpaceStateRecord>> ListAsync(string spaceId, CancellationToken ct = default) =>
        store.QueryAsync((session, token) => session.Spaces.ListStatesAsync(spaceId, token), ct);

    public Task<SpaceStateMutationResult> CreateAsync(CreateSpaceStateCommand command, CancellationToken ct = default) =>
        store.ExecuteAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token)
                ?? throw new AppException(AppErrorKind.NotFound, "Your profile is unavailable.");
            var now = clock.GetUtcNow();
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId,
                "space_state.create", command, now, async inner =>
                {
                    var space = await session.Spaces.GetAsync(command.SpaceId, inner)
                        ?? throw new AppException(AppErrorKind.NotFound, "That space is unavailable.");
                    await RequireOwnerAsync(session, space, profile, inner);
                    if ((int)command.BaseStatus is < 0 or > 3)
                        throw AppException.Validation("baseStatus", "Choose an open base status.");
                    var name = TaskFieldValidation.Title(command.Name, "name");
                    var normalized = name.Trim().ToLowerInvariant();
                    var states = await session.Spaces.ListStatesAsync(space.Id, inner);
                    if (states.Any(state => state.NormalizedName == normalized))
                        throw AppException.Validation("name", "That space already has a state with this name.");
                    var state = new SpaceStateRecord { Id = ids.NewId().ToString("D"), SpaceId = space.Id, Name = name,
                        NormalizedName = normalized, BaseStatus = command.BaseStatus,
                        Position = states.Select(row => row.Position + 1024).DefaultIfEmpty(0).Max(),
                        Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now };
                    await session.Spaces.AddStateAsync(state, inner);
                    return new SpaceStateMutationResult(state, false);
                }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);

    public Task<SpaceStateMutationResult> DeleteAsync(DeleteSpaceStateCommand command, CancellationToken ct = default) =>
        TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId,
                "space_state.delete", command, now, async inner =>
                {
                    var state = await session.Spaces.GetStateAsync(command.StateId, inner);
                    if (state is null || state.DeletedAtUtc is not null)
                        throw AppException.NotFound("That state no longer exists.");
                    if (state.Version != command.ExpectedVersion)
                        throw AppException.VersionConflict("That state changed elsewhere. Refresh and try again.");
                    var space = await session.Spaces.GetAsync(state.SpaceId, inner)
                        ?? throw AppException.NotFound("That space is unavailable.");
                    await RequireOwnerAsync(session, space, profile, inner);
                    foreach (var task in await session.Spaces.ListTasksInStateAsync(state.Id, inner))
                    {
                        var changed = task with { CustomStateId = null, Status = state.BaseStatus,
                            Version = task.Version + 1, UpdatedAtUtc = now };
                        if (await session.Tasks.UpdateAsync(changed, task.Version, inner) is null)
                            throw AppException.VersionConflict("A task changed elsewhere. Refresh and try again.");
                    }
                    var deleted = state with { DeletedAtUtc = now, Version = state.Version + 1, UpdatedAtUtc = now };
                    var applied = await session.Spaces.UpdateStateAsync(deleted, state.Version, inner)
                        ?? throw AppException.VersionConflict("That state changed elsewhere. Refresh and try again.");
                    return new SpaceStateMutationResult(applied, false);
                }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);

    private static async Task RequireOwnerAsync(IDataSession session, SpaceRecord space, ProfileRecord profile, CancellationToken token)
    {
        if (space.Kind == SpaceKind.Personal ? space.OwnerProfileId != profile.Id
            : !(await session.People.ListAsync(space.Id, token)).Any(person => !person.Former && person.Role == 1 && person.ActorId == $"person:{profile.Id}"))
            throw AppException.Validation("spaceId", "Only a space owner can change its states.");
    }
}
