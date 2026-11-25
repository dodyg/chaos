using System.Globalization;
using System.Text.Json;
using Chaos.Server.Auth;
using Chaos.Server.Data;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Sync;

public sealed record CreateAgentRequest(string? Name);
public sealed record UpdateAgentRequest(string? Name, bool? Paused);
public sealed record AgentMembershipDto(Guid SpaceId, DateTimeOffset JoinedAt, IReadOnlyList<Guid>? AllowedContainerIds);
public sealed record AgentDto(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? PausedAt,
    IReadOnlyList<AgentMembershipDto> Memberships);
public sealed record AddAgentRequest(Guid AgentId, IReadOnlyList<Guid>? AllowedContainerIds);
/// <summary>An agent the caller could add to the space (their own, or one whose owner is a live member) and whether it is in it.</summary>
public sealed record SpaceAgentDto(AgentDto Agent, string OwnerName, bool Member);
public sealed record RevertAgentRequest(Guid SpaceId, int Hours);
/// <summary><paramref name="Skipped"/> names the groups a person changed after the agent, as <c>kind:key:group</c>.</summary>
public sealed record RevertAgentDto(int RestoredGroups, int Trashed, IReadOnlyList<string> Skipped);
/// <summary>A task's claim as the REST endpoints return it. <paramref name="ClaimedById"/> is an actor ID (<c>person:…</c> or <c>agent:…</c>).</summary>
public sealed record ClaimDto(Guid TaskId, Guid StreamId, string? ClaimedById, string? ClaimedAtUtc, string? ClaimExpiresAtUtc, string Status);
/// <summary>The outcome of one claim operation: an error code (<see cref="AgentToolStatus"/>, a <see cref="SyncRejection"/> or <c>agents-disabled</c>), or the claim as it now stands.</summary>
public sealed record ClaimOperation(string? Error, int? RetryAfterSeconds, ClaimDto? Claim);

/// <summary>
/// Server agents (PLAN3 12.3), their membership, claims (12.4), hourly counters (12.5) and revert (12.7).
/// An agent is a member with the Agent role, never a privileged path: what it writes reaches a space
/// through <see cref="SyncService.PushAsAgentAsync"/>, checked like any other change.
/// </summary>
public sealed class AgentService(
    ChaosDbContext db,
    TimeProvider clock,
    ServerChangeWriter changes,
    SpaceMembershipCache memberships,
    SyncBroadcaster broadcaster,
    IOptions<SharingOptions> options)
{
    private static readonly Dictionary<string, string> Refusals = new(StringComparer.Ordinal)
    {
        [AgentToolStatus.AlreadyClaimed] = "Someone else holds a live claim on this task.",
        [AgentToolStatus.NotClaimant] = "This claim is not yours to change.",
        [AgentToolStatus.Paused] = "This agent is paused.",
        [AgentToolStatus.RateLimited] = "The hourly limit is reached. Try again later.",
        [AgentToolStatus.Forbidden] = "This change is not allowed here.",
        [AgentToolStatus.NotFound] = "That task was not found.",
        [SyncRejection.NotMember] = "You are not a member of this space.",
        ["agents-disabled"] = "This space does not allow agents. An owner can turn them on in the space settings.",
        ["task-not-found"] = "That task was not found.",
    };

    // ---- Agents -------------------------------------------------------------------------

    public async Task<SpaceOperation<AgentDto>> CreateAsync(Guid accountId, Guid deviceId, CreateAgentRequest request, CancellationToken ct)
    {
        if (ValidName(request.Name) is not { } name)
        {
            return SpaceOperation<AgentDto>.Refuse("invalid-agent", $"The agent needs a name of 1 to {AgentLimits.MaxNameLength} characters.");
        }

        return await strategy(async tx =>
        {
            var account = await LockedAccountAsync(accountId, ct);
            if (account is null)
            {
                return SpaceOperation<AgentDto>.Refuse("account-unavailable", "This account was deleted.");
            }

            var owned = await db.Agents.CountAsync(a => a.OwnerAccountId == accountId && a.RemovedAt == null, ct);
            if (owned >= AgentLimits.MaxAgentsPerPerson)
            {
                return SpaceOperation<AgentDto>.Refuse("agent-limit", "This account has reached its agent limit.");
            }

            var now = clock.GetUtcNow();
            var agent = new AgentRow { Id = Guid.NewGuid(), OwnerAccountId = accountId, Name = name, CreatedAt = now };
            db.Agents.Add(agent);
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.AgentCreated, At = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SpaceOperation<AgentDto>.Success(new(agent.Id, agent.Name, agent.CreatedAt, null, []));
        });
    }

    public async Task<SpaceOperation<IReadOnlyList<AgentDto>>> ListAsync(Guid accountId, CancellationToken ct)
    {
        var agents = await db.Agents.AsNoTracking().Where(a => a.OwnerAccountId == accountId && a.RemovedAt == null)
            .OrderBy(a => a.Name).ThenBy(a => a.Id).ToListAsync(ct);
        var ids = agents.Select(a => a.Id).ToArray();
        var rows = ids.Length == 0 ? []
            : await db.SpaceMembers.AsNoTracking().Where(m => m.MemberKind == SpaceMember.AgentKind && m.RemovedAt == null && ids.Contains(m.MemberId)).ToListAsync(ct);
        return SpaceOperation<IReadOnlyList<AgentDto>>.Success([.. agents.Select(agent => ToDto(agent, rows.Where(m => m.MemberId == agent.Id)))]);
    }

    public async Task<SpaceOperation<AgentDto>> UpdateAsync(Guid accountId, Guid deviceId, Guid agentId, UpdateAgentRequest request, CancellationToken ct)
    {
        string? name = request.Name is null ? null : ValidName(request.Name);
        if (request.Name is not null && name is null)
        {
            return SpaceOperation<AgentDto>.Refuse("invalid-agent", $"The agent needs a name of 1 to {AgentLimits.MaxNameLength} characters.");
        }

        if (request.Paused is { } paused)
        {
            return await SetPausedAsync(accountId, deviceId, agentId, paused, spaceId: null, ct);
        }

        return await strategy(async tx =>
        {
            var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == agentId && a.RemovedAt == null, ct);
            if (agent is null || agent.OwnerAccountId != accountId)
            {
                return SpaceOperation<AgentDto>.Refuse("agent-not-found", "That agent does not exist.");
            }

            agent.Name = name!;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SpaceOperation<AgentDto>.Success(await DtoAsync(agent, ct));
        });
    }

    public async Task<SpaceOperation<bool>> RemoveAsync(Guid accountId, Guid deviceId, Guid agentId, CancellationToken ct)
    {
        var affected = new List<Guid>();
        var result = await strategy(async tx =>
        {
            var agent = await db.Agents.FromSqlInterpolated($"""SELECT * FROM "Agents" WHERE "Id" = {agentId} FOR UPDATE""")
                .SingleOrDefaultAsync(ct);
            if (agent is null || agent.RemovedAt is not null || agent.OwnerAccountId != accountId)
            {
                affected = [];
                return SpaceOperation<bool>.Refuse("agent-not-found", "That agent does not exist.");
            }

            var now = clock.GetUtcNow();
            agent.RemovedAt = now;
            affected = await LeaveSpacesAsync(agent, now, ct);
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.AgentRemoved, At = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SpaceOperation<bool>.Success(true);
        });
        foreach (var stream in affected.Distinct())
        {
            await StreamNotifications.NotifyAsync(db, broadcaster, stream, CancellationToken.None);
        }

        return result;
    }

    /// <summary>Account deletion, inside its transaction: the account's agents go with it, out of every space.</summary>
    internal async Task RemoveOwnedAsync(Guid accountId, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var agent in await db.Agents.Where(a => a.OwnerAccountId == accountId && a.RemovedAt == null).ToListAsync(ct))
        {
            agent.RemovedAt = now;
            await LeaveSpacesAsync(agent, now, ct);
        }
    }

    public async Task<SpaceOperation<IReadOnlyList<SpaceAgentDto>>> SpaceAgentsAsync(Guid accountId, Guid spaceId, CancellationToken ct)
    {
        if (!await db.SpaceMembers.AsNoTracking().AnyAsync(m => m.SpaceId == spaceId && m.AccountId == accountId
                && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null, ct))
        {
            return SpaceOperation<IReadOnlyList<SpaceAgentDto>>.Refuse(SyncRejection.NotMember, "You are not a member of this space.");
        }

        var people = await db.SpaceMembers.AsNoTracking()
            .Where(m => m.SpaceId == spaceId && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null && m.AccountId != null)
            .Select(m => new { m.DisplayName, m.AccountId }).ToListAsync(ct);
        var ownerAccounts = people.Select(p => p.AccountId!.Value).Distinct().ToArray();
        var agents = await db.Agents.AsNoTracking().Where(a => a.RemovedAt == null && ownerAccounts.Contains(a.OwnerAccountId))
            .OrderBy(a => a.Name).ThenBy(a => a.Id).ToListAsync(ct);
        var ids = agents.Select(a => a.Id).ToArray();
        var rows = ids.Length == 0 ? []
            : await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == spaceId && m.MemberKind == SpaceMember.AgentKind && ids.Contains(m.MemberId)).ToListAsync(ct);
        var names = people.GroupBy(p => p.AccountId!.Value).ToDictionary(group => group.Key, group => group.First().DisplayName);
        return SpaceOperation<IReadOnlyList<SpaceAgentDto>>.Success([.. agents.Select(agent =>
            new SpaceAgentDto(ToDto(agent, rows.Where(m => m.MemberId == agent.Id && m.RemovedAt == null)),
                names.GetValueOrDefault(agent.OwnerAccountId, "A member"), rows.Any(m => m.MemberId == agent.Id && m.RemovedAt == null)))]);
    }

    /// <summary>
    /// Members → Add agent (12.3): an owner adds their own agent, or one whose owner is a live member, with the
    /// Agent role and optional allowed lists. The space's <c>AgentsAllowed</c> must be on.
    /// </summary>
    public async Task<SpaceOperation<MemberDto>> AddToSpaceAsync(Guid accountId, Guid deviceId, Guid spaceId, AddAgentRequest request, CancellationToken ct)
    {
        if (request.AgentId == Guid.Empty)
        {
            return SpaceOperation<MemberDto>.Refuse("agent-not-found", "That agent does not exist.");
        }

        return await strategy(async tx =>
        {
            if (!await db.SyncStreams.AsNoTracking().AnyAsync(s => s.Id == spaceId && s.Kind == SyncStream.SpaceKind && s.DeletedAt == null, ct))
            {
                return SpaceOperation<MemberDto>.Refuse("space-unavailable", "That space is not available.");
            }

            await db.SyncStreams.FromSqlInterpolated($"""SELECT * FROM "SyncStreams" WHERE "Id" = {spaceId} FOR UPDATE""").ToListAsync(ct);
            await db.SpaceMembers.FromSqlInterpolated($"""
                SELECT * FROM "SpaceMembers" WHERE "SpaceId" = {spaceId} ORDER BY "MemberId" FOR UPDATE
                """).ToListAsync(ct);
            var owner = await db.SpaceMembers.SingleOrDefaultAsync(m => m.SpaceId == spaceId && m.AccountId == accountId
                && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null, ct);
            if (owner is null)
            {
                return SpaceOperation<MemberDto>.Refuse(SyncRejection.NotMember, "You are not a member of this space.");
            }

            if (owner.Role != SpaceMember.OwnerRole)
            {
                return SpaceOperation<MemberDto>.Refuse(SyncRejection.Forbidden, "Only an owner can add an agent.");
            }

            // A removed agent keeps its row (the key is space and member), so adding it again revives that row.
            var previous = await db.SpaceMembers.SingleOrDefaultAsync(m => m.SpaceId == spaceId && m.MemberId == request.AgentId, ct);
            if (previous is { RemovedAt: null })
            {
                return SpaceOperation<MemberDto>.Refuse("agent-already-member", "That agent is already in this space.");
            }

            var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == request.AgentId && a.RemovedAt == null, ct);
            var agentOwner = agent is null ? null : await db.SpaceMembers.AsNoTracking().SingleOrDefaultAsync(m => m.SpaceId == spaceId
                && m.MemberKind == SpaceMember.PersonKind && m.AccountId == agent.OwnerAccountId && m.RemovedAt == null, ct);
            if (agent is null || agentOwner is null)
            {
                return SpaceOperation<MemberDto>.Refuse("agent-not-found", "Choose one of your agents, or one whose owner is a member of this space.");
            }

            if (!await SpaceAllowsAgentsAsync(spaceId, ct))
            {
                return SpaceOperation<MemberDto>.Refuse("agents-disabled", "This space does not allow agents. An owner can turn them on in the space settings.");
            }

            if (await db.SpaceMembers.CountAsync(m => m.SpaceId == spaceId && m.MemberKind == SpaceMember.AgentKind && m.RemovedAt == null, ct) >= options.Value.MaxAgentsPerSpace)
            {
                return SpaceOperation<MemberDto>.Refuse("agent-limit", "This space has reached its agent limit.");
            }

            string? allowed = null;
            if (request.AllowedContainerIds is { Count: > 0 } lists)
            {
                var distinct = lists.Distinct().Select(id => id.ToString("D")).Order(StringComparer.Ordinal).ToArray();
                if (await db.SyncEntities.AsNoTracking().CountAsync(e => e.AccountId == spaceId && e.Kind == "container"
                        && e.PurgedAt == null && e.MovedTo == null && distinct.Contains(e.EntityKey), ct) != distinct.Length)
                {
                    return SpaceOperation<MemberDto>.Refuse("container-not-found", "Choose lists of this space.");
                }

                allowed = JsonSerializer.Serialize(distinct);
            }

            var now = clock.GetUtcNow();
            var member = previous ?? new SpaceMember { SpaceId = spaceId, MemberId = agent.Id, DisplayName = agent.Name };
            member.MemberKind = SpaceMember.AgentKind;
            member.AccountId = agent.OwnerAccountId;
            member.Role = SpaceMember.AgentRole;
            member.DisplayName = agent.Name;
            member.JoinedAt = now;
            member.InvitedByMemberId = owner.MemberId;
            member.AllowedContainers = allowed;
            member.RemovedAt = null;
            if (previous is null)
            {
                db.SpaceMembers.Add(member);
            }
            await db.SaveChangesAsync(ct);
            var key = MemberKey(spaceId, agent.Id);
            var hlc = await changes.NextHlcAsync(spaceId, "space_member", key, ct);
            await changes.AppendAsync(spaceId, owner.MemberId, SyncChange.Upsert(Guid.NewGuid().ToString("D"), "space_member", key,
                new Dictionary<string, SyncGroupState>
                {
                    ["create"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?>
                    {
                        ["SpaceId"] = spaceId.ToString("D"), ["MemberId"] = agent.Id.ToString("D"), ["MemberKind"] = (long)SpaceMember.AgentKind,
                        ["OwnerMemberId"] = agentOwner.MemberId.ToString("D"), ["JoinedAtUtc"] = Instant(now),
                    })),
                    ["profile"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?> { ["DisplayName"] = agent.Name, ["GitHubLogin"] = null })),
                    ["role"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?> { ["Role"] = (long)SpaceMember.AgentRole })),
                    ["state"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?> { ["RemovedAtUtc"] = null })),
                }), ct);
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.MemberAdded, At = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await StreamNotifications.NotifyAsync(db, broadcaster, spaceId, CancellationToken.None);
            return SpaceOperation<MemberDto>.Success(new MemberDto(member.MemberId, member.DisplayName, member.Role, member.MemberKind, member.JoinedAt, null));
        });
    }

    /// <summary>Members → Remove, for an agent (12.3): only an owner may, and its claims in the space are released first. Inside the caller's transaction.</summary>
    internal async Task<SpaceOperation<MemberDto>> RemoveFromSpaceAsync(SpaceMember remover, SpaceMember target, DateTimeOffset now, CancellationToken ct)
    {
        if (remover.Role != SpaceMember.OwnerRole)
        {
            return SpaceOperation<MemberDto>.Refuse(SyncRejection.Forbidden, "Only an owner can remove an agent.");
        }

        target.RemovedAt = now;
        var agent = await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == target.MemberId, ct);
        if (agent is not null)
        {
            foreach (var taskId in await ClaimedTasksAsync(target.SpaceId, new ActorId(ActorKind.Agent, agent.Id).ToString(), ct))
            {
                await ExpireAsync(target.SpaceId, taskId, now, "the agent was removed from the space", onlyExpired: false, ct);
            }
        }

        var key = MemberKey(target.SpaceId, target.MemberId);
        var hlc = await changes.NextHlcAsync(target.SpaceId, "space_member", key, ct);
        await changes.AppendAsync(target.SpaceId, remover.MemberId, SyncChange.Upsert(Guid.NewGuid().ToString("D"), "space_member", key,
            new Dictionary<string, SyncGroupState>
            {
                ["state"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?> { ["RemovedAtUtc"] = Instant(now) })),
            }), ct);
        return SpaceOperation<MemberDto>.Success(new MemberDto(target.MemberId, target.DisplayName, target.Role, target.MemberKind, target.JoinedAt, now));
    }

    /// <summary>
    /// Pauses or resumes an agent. Its owner may do so anywhere; a space owner may do so for an agent in their
    /// space (12.3). A pause is the agent's, so its claims are released in every space; a paused agent's tools answer <c>paused</c>.
    /// </summary>
    public async Task<SpaceOperation<AgentDto>> SetPausedAsync(Guid accountId, Guid deviceId, Guid agentId, bool paused, Guid? spaceId, CancellationToken ct)
    {
        var affected = new List<Guid>();
        var result = await strategy(async tx =>
        {
            var agent = await db.Agents.FromSqlInterpolated($"""SELECT * FROM "Agents" WHERE "Id" = {agentId} FOR UPDATE""")
                .SingleOrDefaultAsync(ct);
            if (agent is null || agent.RemovedAt is not null)
            {
                affected = [];
                return SpaceOperation<AgentDto>.Refuse("agent-not-found", "That agent does not exist.");
            }

            if (spaceId is { } space)
            {
                var owner = agent.OwnerAccountId == accountId ? null : await db.SpaceMembers.AsNoTracking()
                    .SingleOrDefaultAsync(m => m.SpaceId == space && m.AccountId == accountId && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null, ct);
                if (agent.OwnerAccountId != accountId && (owner is null || owner.Role != SpaceMember.OwnerRole))
                {
                    affected = [];
                    return SpaceOperation<AgentDto>.Refuse(owner is null ? SyncRejection.NotMember : SyncRejection.Forbidden,
                        "Only the agent's owner or an owner of the space can pause or resume it.");
                }

                if (!await db.SpaceMembers.AsNoTracking().AnyAsync(m => m.SpaceId == space && m.MemberId == agentId && m.RemovedAt == null, ct))
                {
                    affected = [];
                    return SpaceOperation<AgentDto>.Refuse("agent-not-found", "That agent is not in this space.");
                }
            }
            else if (agent.OwnerAccountId != accountId)
            {
                affected = [];
                return SpaceOperation<AgentDto>.Refuse(SyncRejection.Forbidden, "Only the agent's owner can pause or resume it.");
            }

            // A pause is the agent's, in every space; a space owner may pause it, but only its owner resumes it.
            if (!paused && agent.PausedAt is not null && agent.OwnerAccountId != accountId)
            {
                affected = [];
                return SpaceOperation<AgentDto>.Refuse(SyncRejection.Forbidden, "Only the agent's owner can resume it.");
            }

            var now = clock.GetUtcNow();
            if (paused && agent.PausedAt is null)
            {
                agent.PausedAt = now;
                affected = await ReleaseClaimsAsync(agent, "the agent was paused", now, ct);
                db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.AgentPaused, At = now });
            }
            else
            {
                affected = [];
                if (!paused)
                {
                    agent.PausedAt = null;
                }
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SpaceOperation<AgentDto>.Success(await DtoAsync(agent, ct));
        });
        return await NotifyAfterAsync(result, affected);
    }

    private async Task<SpaceOperation<T>> NotifyAfterAsync<T>(SpaceOperation<T> result, List<Guid> affected)
    {
        if (result.Succeeded)
        {
            foreach (var stream in affected.Distinct())
            {
                await StreamNotifications.NotifyAsync(db, broadcaster, stream, CancellationToken.None);
            }
        }

        return result;
    }

    // ---- Claims (12.4) ------------------------------------------------------------------

    /// <summary>
    /// A person's claim, heartbeat, release or take over through REST. The task is found among the caller's
    /// readable streams; in a space the caller must be an owner or editor (8.4), and a viewer is refused.
    /// </summary>
    public async Task<SpaceOperation<ClaimDto>> PersonClaimAsync(Guid accountId, string taskId, ClaimAction action, CancellationToken ct)
    {
        if (!Guid.TryParseExact(taskId, "D", out _))
        {
            return SpaceOperation<ClaimDto>.Refuse("task-not-found", "That task was not found.");
        }

        var readable = (await memberships.GetAsync(db, accountId, ct)).Roles.Keys.Append(accountId).ToArray();
        var located = await transaction(async tx =>
        {
            var account = await LockedAccountAsync(accountId, ct);
            if (account?.ProfileId is not { } profile)
            {
                return new ClaimOperation("account-unavailable", null, null);
            }

            var streams = await TaskStreamsAsync(readable, taskId, ct);
            if (streams.Count != 1)
            {
                // The 8.4 matrix distinguishes a task the caller cannot reach (not a member) from one that is not there.
                var elsewhere = await db.SyncEntities.AsNoTracking().AnyAsync(e => e.Kind == "task" && e.EntityKey == taskId
                    && e.PurgedAt == null && e.MovedTo == null && db.SyncStreams.Any(s => s.Id == e.AccountId && s.Kind == SyncStream.SpaceKind), ct);
                return new ClaimOperation(elsewhere ? SyncRejection.NotMember : "task-not-found", null, null);
            }

            var outcome = await ApplyClaimAsync(streams[0], taskId, new ActorId(ActorKind.Person, profile), action, account.Id, ct);
            if (outcome.Error is null)
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }

            return outcome;
        });
        if (located.Error is { } error)
        {
            return SpaceOperation<ClaimDto>.Refuse(error, Refusals.GetValueOrDefault(error, "The claim was refused."));
        }

        await StreamNotifications.NotifyAsync(db, broadcaster, located.Claim!.StreamId, CancellationToken.None);
        return SpaceOperation<ClaimDto>.Success(located.Claim);
    }

    /// <summary>An agent's claim tool (12.4): the task is found among the spaces the agent is a live member of.</summary>
    internal async Task<ClaimOperation> AgentClaimAsync(Guid agentId, string taskId, ClaimAction action, CancellationToken ct)
    {
        if (!Guid.TryParseExact(taskId, "D", out _))
        {
            return new(AgentToolStatus.NotFound, null, null);
        }

        var spaces = await db.SpaceMembers.AsNoTracking()
            .Where(m => m.MemberId == agentId && m.MemberKind == SpaceMember.AgentKind && m.RemovedAt == null)
            .Select(m => m.SpaceId).ToListAsync(ct);
        var streams = await TaskStreamsAsync(spaces, taskId, ct);
        if (streams.Count != 1)
        {
            return new(AgentToolStatus.NotFound, null, null);
        }

        var outcome = await transaction(async tx =>
        {
            // The agent row first, as pause and push take it: a pause then waits for this claim (and releases it), the
            // hourly counters are not lost to a concurrent claim in another space, and the lock order stays agent, then space.
            await db.Agents.FromSqlInterpolated($"""SELECT * FROM "Agents" WHERE "Id" = {agentId} FOR UPDATE""").SingleOrDefaultAsync(ct);
            var result = await ApplyClaimAsync(streams[0], taskId, new ActorId(ActorKind.Agent, agentId), action, null, ct);
            if (result.Error is null)
            {
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }

            return result;
        });
        if (outcome.Claim is { } claim)
        {
            await StreamNotifications.NotifyAsync(db, broadcaster, claim.StreamId, CancellationToken.None);
        }

        return outcome;
    }

    /// <summary>
    /// The compare-and-set under the stream lock (PLAN3 5.6): one live claim per task; a claim by someone else is
    /// refused; the claimant's heartbeat extends the expiry; the claimant releases it, and an owner or editor may take
    /// a task over. Runs inside the caller's transaction.
    /// </summary>
    private async Task<ClaimOperation> ApplyClaimAsync(Guid streamId, string taskId, ActorId claimant, ClaimAction action, Guid? accountStream, CancellationToken ct)
    {
        var stream = await db.SyncStreams.FromSqlInterpolated($"""SELECT * FROM "SyncStreams" WHERE "Id" = {streamId} AND "DeletedAt" IS NULL FOR UPDATE""")
            .SingleOrDefaultAsync(ct);
        var entity = stream is null ? null : await db.SyncEntities.FindAsync([streamId, "task", taskId], ct);
        if (entity is null || entity.PurgedAt is not null || entity.MovedTo is not null)
        {
            return new(AgentToolStatus.NotFound, null, null);
        }

        var stored = SyncJson.ReadGroups(entity.Groups);
        var status = stored.GetValueOrDefault("status")?.Values.Values;
        var current = status?.GetValueOrDefault("Status") is long s ? s : 0L;
        if (stored.GetValueOrDefault("deleted")?.Values.Values.GetValueOrDefault("DeletedAtUtc") is not null || TaskStatuses.IsTerminal(current))
        {
            return new(AgentToolStatus.NotFound, null, null);
        }

        var now = clock.GetUtcNow();
        var me = claimant.ToString();
        var claim = stored.GetValueOrDefault("claim")?.Values.Values;
        var claimedBy = claim?.GetValueOrDefault("ClaimedById") as string;
        var live = claimedBy is not null && Instant(claim?.GetValueOrDefault("ClaimExpiresAtUtc") as string) > now;
        var same = string.Equals(claimedBy, me, StringComparison.OrdinalIgnoreCase);
        var settings = await SettingsAsync(streamId, ct);
        var minutes = settings?.GetValueOrDefault("ClaimMinutes") is long configured
            ? Math.Clamp((int)configured, AgentLimits.MinClaimMinutes, AgentLimits.MaxClaimMinutes)
            : AgentLimits.DefaultClaimMinutes;

        short role = 0;
        if (claimant.Kind == ActorKind.Agent)
        {
            var agent = await db.Agents.SingleOrDefaultAsync(a => a.Id == claimant.Id, ct);
            if (agent is null || agent.RemovedAt is not null)
            {
                return new(SyncRejection.NotMember, null, null);
            }

            if (agent.PausedAt is not null)
            {
                return new(AgentToolStatus.Paused, null, null);
            }

            var member = await db.SpaceMembers.AsNoTracking().SingleOrDefaultAsync(m => m.SpaceId == streamId && m.MemberId == claimant.Id
                && m.MemberKind == SpaceMember.AgentKind && m.RemovedAt == null, ct);
            if (member is null)
            {
                return new(SyncRejection.NotMember, null, null);
            }

            if (settings?.GetValueOrDefault("AgentsAllowed") is not 1L)
            {
                return new("agents-disabled", null, null);
            }

            if (member.AllowedContainers is { } listsJson && JsonSerializer.Deserialize<string[]>(listsJson) is { Length: > 0 } lists
                && (stored.GetValueOrDefault("place")?.Values.Values.GetValueOrDefault("ContainerId")?.ToString() is not { } container
                    || !lists.Contains(container, StringComparer.OrdinalIgnoreCase)))
            {
                return new(AgentToolStatus.Forbidden, null, null);
            }

            if (stored.GetValueOrDefault("agent")?.Values.Values.GetValueOrDefault("AgentAssigneeId") is string { Length: > 0 } assigned
                && !string.Equals(assigned.ToString(), claimant.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                return new(AgentToolStatus.Forbidden, null, null);
            }

            if (action == ClaimAction.Claim && !CountUse(agent, now, adds: false))
            {
                return new(AgentToolStatus.RateLimited, RetryAfter(agent, now), null);
            }
        }
        else
        {
            if (streamId == accountStream)
            {
                return new(AgentToolStatus.Forbidden, null, null); // Personal tasks are claimed on the device
            }

            var member = await db.SpaceMembers.AsNoTracking().SingleOrDefaultAsync(m => m.SpaceId == streamId && m.MemberId == claimant.Id
                && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null, ct);
            if (member is null)
            {
                return new(SyncRejection.NotMember, null, null);
            }

            role = member.Role;
            if (role == SpaceMember.ViewerRole)
            {
                return new(AgentToolStatus.Forbidden, null, null);
            }
        }

        var writes = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        string kind;
        switch (action)
        {
            case ClaimAction.Claim when live && !same:
                return new(AgentToolStatus.AlreadyClaimed, null, null);
            case ClaimAction.Claim:
                writes["claim"] = ClaimValues(me, Instant(now), Instant(now.AddMinutes(minutes)));
                if (current == TaskStatuses.NotStarted)
                {
                    writes["status"] = await StatusValuesAsync(streamId, TaskStatuses.InProgress, stored, ct);
                }

                kind = "task.claimed";
                break;
            case ClaimAction.Heartbeat:
                if (!live || !same)
                {
                    return new(AgentToolStatus.NotClaimant, null, null);
                }

                writes["claim"] = ClaimValues(me, claim?.GetValueOrDefault("ClaimedAtUtc") as string ?? Instant(now), Instant(now.AddMinutes(minutes)));
                kind = "task.claim-extended";
                break;
            case ClaimAction.Release:
                if (claimedBy is null)
                {
                    return new(null, null, Dto(streamId, taskId, stored, current));
                }

                if (!same && !(claimant.Kind == ActorKind.Person && role is SpaceMember.OwnerRole or SpaceMember.EditorRole))
                {
                    return new(AgentToolStatus.NotClaimant, null, null);
                }

                // The agent group is left as it is: a claim never clears Ready for agent, so a task from the queue goes back
                // to it, and a task that was never Ready does not become Ready by a claim and release (E5).
                writes["claim"] = ClaimValues(null, null, null);
                if (current == TaskStatuses.InProgress)
                {
                    writes["status"] = await StatusValuesAsync(streamId, TaskStatuses.NotStarted, stored, ct);
                }

                kind = "task.unclaimed";
                break;
            case ClaimAction.TakeOver:
                if (claimant.Kind != ActorKind.Person || role is not (SpaceMember.OwnerRole or SpaceMember.EditorRole))
                {
                    return new(AgentToolStatus.Forbidden, null, null);
                }

                writes["claim"] = ClaimValues(me, Instant(now), Instant(now.AddMinutes(minutes)));
                kind = "task.takeover";
                break;
            default:
                return new(SyncRejection.Invalid, null, null);
        }

        await WriteTaskGroupsAsync(streamId, taskId, claimant.Id, writes, ct);
        await RecordAsync(streamId, taskId, me, kind, [.. writes.Keys], now, ct);
        var merged = SyncJson.ReadGroups((await db.SyncEntities.FindAsync([streamId, "task", taskId], ct))!.Groups);
        var after = (merged.GetValueOrDefault("status")?.Values.Values.GetValueOrDefault("Status") as long?) ?? 0L;
        return new(null, null, Dto(streamId, taskId, merged, after));
    }

    /// <summary>Writes field groups of a task as one server change by <paramref name="actor"/>.</summary>
    private async Task WriteTaskGroupsAsync(Guid streamId, string taskId, Guid actor, Dictionary<string, Dictionary<string, object?>> writes, CancellationToken ct)
    {
        var groups = new Dictionary<string, SyncGroupState>();
        foreach (var (name, values) in writes)
        {
            groups[name] = new(await changes.NextHlcAsync(streamId, "task", taskId, ct), new SyncGroupValues(values));
        }

        await changes.AppendAsync(streamId, actor, SyncChange.Upsert(Guid.NewGuid().ToString("D"), "task", taskId, groups), ct);
    }

    private static Dictionary<string, object?> ClaimValues(string? by, string? at, string? expires) => new()
    {
        ["ClaimedById"] = by, ["ClaimedAtUtc"] = at, ["ClaimExpiresAtUtc"] = expires,
    };

    /// <summary>The status group as a claim sets it: the base status, with a custom state only while it is based on that status.</summary>
    private async Task<Dictionary<string, object?>> StatusValuesAsync(Guid streamId, long status, Dictionary<string, SyncGroupState> stored, CancellationToken ct)
    {
        var custom = stored.GetValueOrDefault("status")?.Values.Values.GetValueOrDefault("CustomStateId")?.ToString();
        if (custom is { Length: > 0 } && !await db.SyncEntities.FromSqlInterpolated($"""
                SELECT * FROM "SyncEntities" WHERE "StreamId" = {streamId} AND "Kind" = 'space_state' AND "EntityKey" = {custom} AND "PurgedAt" IS NULL
                  AND "Groups" -> 'base' -> 'values' ->> 'BaseStatus' = {status.ToString()}
                """).AnyAsync(ct))
        {
            custom = null;
        }

        return new Dictionary<string, object?> { ["Status"] = status, ["CustomStateId"] = custom, ["CompletedAtUtc"] = null, ["CancelledAtUtc"] = null };
    }


    /// <summary>A history row for a server-made task change: who, what, when; no aggregate snapshots (PLAN2 §6.4 keeps them content-free here).</summary>
    private async Task RecordAsync(Guid streamId, string taskId, string actor, string kind, IReadOnlyList<string> fields, DateTimeOffset now, CancellationToken ct)
    {
        var key = Guid.NewGuid().ToString("D");
        var hlc = await changes.NextHlcAsync(streamId, "task_history", key, ct);
        var actorId = Guid.TryParse(actor[(actor.IndexOf(':') + 1)..], out var id) ? id : Guid.Empty;
        await changes.AppendAsync(streamId, actorId, SyncChange.Upsert(Guid.NewGuid().ToString("D"), "task_history", key,
            new Dictionary<string, SyncGroupState>
            {
                ["record"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?>
                {
                    ["TaskId"] = taskId, ["OperationId"] = Guid.NewGuid().ToString("D"), ["EffectiveAtUtc"] = Instant(now), ["Actor"] = actor,
                    ["EventKind"] = kind, ["ChangedFields"] = string.Join(",", fields), ["SnapshotBefore"] = null, ["SnapshotAfter"] = null,
                    ["AggregateVersion"] = 0L, ["RecordedAtUtc"] = Instant(now),
                })),
            }), ct);
    }

    private static ClaimDto Dto(Guid streamId, string taskId, Dictionary<string, SyncGroupState> stored, long status)
    {
        var claim = stored.GetValueOrDefault("claim")?.Values.Values;
        return new ClaimDto(Guid.Parse(taskId), streamId, claim?.GetValueOrDefault("ClaimedById") as string,
            claim?.GetValueOrDefault("ClaimedAtUtc") as string, claim?.GetValueOrDefault("ClaimExpiresAtUtc") as string, TaskStatuses.Name(status));
    }

    /// <summary>The claim on a task has ended: the claim goes, the task is Ready for agent and Not started again unless its status moved on, and the diary says whose claim ended.</summary>
    private async Task<bool> ExpireAsync(Guid streamId, string taskId, DateTimeOffset now, string? note, bool onlyExpired, CancellationToken ct)
    {
        var stream = await db.SyncStreams.FromSqlInterpolated($"""SELECT * FROM "SyncStreams" WHERE "Id" = {streamId} AND "DeletedAt" IS NULL FOR UPDATE""")
            .SingleOrDefaultAsync(ct);
        var entity = stream is null ? null : await db.SyncEntities.FindAsync([streamId, "task", taskId], ct);
        if (entity is null || entity.PurgedAt is not null || entity.MovedTo is not null)
        {
            return false;
        }

        var stored = SyncJson.ReadGroups(entity.Groups);
        var claim = stored.GetValueOrDefault("claim")?.Values.Values;
        if (claim?.GetValueOrDefault("ClaimedById") is not string claimedBy
            || (onlyExpired && Instant(claim.GetValueOrDefault("ClaimExpiresAtUtc") as string) is { } until && until > now))
        {
            return false;
        }

        var status = stored.GetValueOrDefault("status")?.Values.Values;
        var current = status?.GetValueOrDefault("Status") is long s ? s : 0L;
        var writes = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal) { ["claim"] = ClaimValues(null, null, null) };
        if (current == TaskStatuses.InProgress)
        {
            writes["status"] = await StatusValuesAsync(streamId, TaskStatuses.NotStarted, stored, ct);
        }

        // A claim on a task already done or cancelled just goes: no note, the task is not put back in the queue.
        var closed = TaskStatuses.IsTerminal(current);
        if (closed)
        {
            note = "silent";
        }

        // Ready for agent is left as it is (a claim never clears it): see the release above (E5).
        var claimant = Guid.TryParse(claimedBy[(claimedBy.IndexOf(':') + 1)..], out var claimantId) ? claimantId : Guid.Empty;
        await WriteTaskGroupsAsync(streamId, taskId, claimantId, writes, ct);
        await RecordAsync(streamId, taskId, claimedBy, "task.claim-expired", ["Claim"], now, ct);
        if (note != "silent")
        {
            var entryKey = Guid.NewGuid().ToString("D");
            var hlc = await changes.NextHlcAsync(streamId, "diary_entry", entryKey, ct);
            var name = await ClaimantNameAsync(streamId, claimantId, ct);
            var text = note is null ? $"Claim by {name} expired" : $"Claim by {name} ended: {note}";
            await changes.AppendAsync(streamId, claimantId, SyncChange.Upsert(Guid.NewGuid().ToString("D"), "diary_entry", entryKey,
                new Dictionary<string, SyncGroupState>
                {
                    ["create"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?>
                    {
                        ["TaskId"] = taskId, ["AuthorProfileId"] = claimantId.ToString("D"), ["EntryAtUtc"] = Instant(now), ["CreatedAtUtc"] = Instant(now),
                    })),
                    ["entry"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?> { ["RawMarkdown"] = text, ["EditedAtUtc"] = null })),
                }), ct);
        }

        return true;
    }

    /// <summary>Ends the claims an agent holds in every space, inside the caller's transaction; returns the streams it wrote to.</summary>
    private async Task<List<Guid>> ReleaseClaimsAsync(AgentRow agent, string note, DateTimeOffset now, CancellationToken ct)
    {
        var affected = new List<Guid>();
        foreach (var streamId in await db.SpaceMembers.AsNoTracking().Where(m => m.MemberId == agent.Id && m.MemberKind == SpaceMember.AgentKind)
                     .Select(m => m.SpaceId).Distinct().ToListAsync(ct))
        {
            if (!await db.SyncStreams.AsNoTracking().AnyAsync(s => s.Id == streamId && s.DeletedAt == null, ct))
            {
                continue;
            }

            foreach (var taskId in await ClaimedTasksAsync(streamId, new ActorId(ActorKind.Agent, agent.Id).ToString(), ct))
            {
                if (await ExpireAsync(streamId, taskId, now, note, onlyExpired: false, ct))
                {
                    affected.Add(streamId);
                }
            }
        }

        return affected;
    }

    /// <summary>Task keys of a stream whose claim names <paramref name="actor"/>, as stored (<c>person:…</c> or <c>agent:…</c>).</summary>
    private Task<List<string>> ClaimedTasksAsync(Guid streamId, string actor, CancellationToken ct) =>
        db.SyncEntities.FromSqlInterpolated($"""
            SELECT * FROM "SyncEntities" WHERE "StreamId" = {streamId} AND "Kind" = 'task' AND "PurgedAt" IS NULL AND "MovedTo" IS NULL
              AND "Groups" -> 'claim' -> 'values' ->> 'ClaimedById' = {actor}
            """).AsNoTracking().Select(e => e.EntityKey).ToListAsync(ct);

    /// <summary>The claim expiry sweep (<c>ClaimExpiryService</c>, every minute): every space task whose claim time ran out goes back to the queue with a note.</summary>
    public async Task<int> ExpireDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var candidates = await db.Database.SqlQuery<ExpiredClaim>($"""
            SELECT e."StreamId" AS "StreamId", e."EntityKey" AS "EntityKey", e."Groups" -> 'claim' -> 'values' ->> 'ClaimExpiresAtUtc' AS "Until"
            FROM "SyncEntities" e WHERE e."Kind" = 'task' AND e."PurgedAt" IS NULL AND e."MovedTo" IS NULL
              AND e."Groups" -> 'claim' -> 'values' ->> 'ClaimedById' IS NOT NULL
              AND EXISTS (SELECT 1 FROM "SyncStreams" s WHERE s."Id" = e."StreamId" AND s."Kind" = 2 AND s."DeletedAt" IS NULL)
            """).ToListAsync(ct);
        var affected = new List<Guid>();
        var count = 0;
        foreach (var row in candidates.Where(row => Instant(row.Until) is { } until && until <= now))
        {
            var ended = await transaction(async tx =>
            {
                var done = await ExpireAsync(row.StreamId, row.EntityKey, now, note: null, onlyExpired: true, ct);
                if (done)
                {
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                }

                return done;
            });
            if (ended)
            {
                count++;
                affected.Add(row.StreamId);
            }
        }

        foreach (var stream in affected.Distinct())
        {
            await StreamNotifications.NotifyAsync(db, broadcaster, stream, CancellationToken.None);
        }

        return count;
    }

    private async Task<string> ClaimantNameAsync(Guid streamId, Guid claimant, CancellationToken ct)
    {
        var member = await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == streamId && m.MemberId == claimant)
            .Select(m => m.DisplayName).FirstOrDefaultAsync(ct);
        if (member is not null)
        {
            return member;
        }

        return await db.Agents.AsNoTracking().Where(a => a.Id == claimant).Select(a => a.Name).FirstOrDefaultAsync(ct) ?? "someone";
    }

    // ---- Revert (12.7) ------------------------------------------------------------------

    /// <summary>
    /// Revert the last <paramref name="hours"/> of an agent in a space, by its owner or a space owner. Each group the agent
    /// wrote goes back to the value it had before the agent's first write in the window, only while the stored group still
    /// carries the agent's clock; a group a person changed later is listed, not touched. What the agent created, and nobody
    /// touched since, is trashed rather than purged.
    /// </summary>
    public async Task<SpaceOperation<RevertAgentDto>> RevertAsync(Guid accountId, Guid deviceId, Guid agentId, RevertAgentRequest request, CancellationToken ct)
    {
        var hours = Math.Clamp(request.Hours, 1, 24 * 7);
        return await strategy(async tx =>
        {
            var stream = await db.SyncStreams.FromSqlInterpolated($"""SELECT * FROM "SyncStreams" WHERE "Id" = {request.SpaceId} AND "DeletedAt" IS NULL FOR UPDATE""")
                .SingleOrDefaultAsync(ct);
            var agent = stream is null ? null : await db.Agents.AsNoTracking().SingleOrDefaultAsync(a => a.Id == agentId, ct);
            if (stream is null || agent is null)
            {
                return SpaceOperation<RevertAgentDto>.Refuse("agent-not-found", "That agent or space does not exist.");
            }

            Guid actor;
            if (agent.OwnerAccountId == accountId)
            {
                // The agent's owner reverts only while they can still write in the space: a person who left it cannot.
                var self = await db.SpaceMembers.AsNoTracking().SingleOrDefaultAsync(m => m.SpaceId == request.SpaceId && m.AccountId == accountId
                    && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null, ct);
                if (self is null)
                {
                    return SpaceOperation<RevertAgentDto>.Refuse(SyncRejection.NotMember, "You are not a member of this space.");
                }

                if (self.Role is not (SpaceMember.OwnerRole or SpaceMember.EditorRole))
                {
                    return SpaceOperation<RevertAgentDto>.Refuse(SyncRejection.Forbidden, "Only an owner or an editor of the space can undo an agent's changes there.");
                }

                actor = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.ProfileId!.Value).FirstAsync(ct);
            }
            else
            {
                var owner = await db.SpaceMembers.AsNoTracking().SingleOrDefaultAsync(m => m.SpaceId == request.SpaceId && m.AccountId == accountId
                    && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null, ct);
                if (owner is null)
                {
                    return SpaceOperation<RevertAgentDto>.Refuse(SyncRejection.NotMember, "You are not a member of this space.");
                }

                if (owner.Role != SpaceMember.OwnerRole)
                {
                    return SpaceOperation<RevertAgentDto>.Refuse(SyncRejection.Forbidden, "Only the agent's owner or an owner of the space can revert its changes.");
                }

                actor = owner.MemberId;
            }

            var since = clock.GetUtcNow().AddHours(-hours);
            var log = await db.SyncChanges.AsNoTracking()
                .Where(c => c.AccountId == request.SpaceId && c.ActorId == agentId && c.CreatedAt >= since && c.Op == "upsert")
                .OrderBy(c => c.Seq).ToListAsync(ct);
            var skipped = new List<string>();
            var restored = 0;
            var trashed = 0;
            var touchedTasks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entityRows in log.GroupBy(row => (row.Kind, row.EntityKey)))
            {
                var (kind, key) = entityRows.Key;
                var entity = await db.SyncEntities.FindAsync([request.SpaceId, kind, key], ct);
                if (entity is null || entity.PurgedAt is not null || entity.MovedTo is not null)
                {
                    continue; // gone: nothing to restore
                }

                var current = SyncJson.ReadGroups(entity.Groups);
                var replaced = entityRows.ToDictionary(row => row, row => ReplacedGroups(row.PreviousGroups));
                var earliest = entityRows.First();
                var latest = entityRows.Last();
                var created = earliest.PreviousGroups is null && !await db.SyncChanges.AsNoTracking().AnyAsync(c => c.AccountId == request.SpaceId
                    && c.Kind == kind && c.EntityKey == key && c.Seq < earliest.Seq, ct);
                var touchedSince = await db.SyncChanges.AsNoTracking().AnyAsync(c => c.AccountId == request.SpaceId && c.Kind == kind
                    && c.EntityKey == key && c.Seq > latest.Seq, ct);
                if (created && !touchedSince)
                {
                    if (SyncKinds.Find(kind)?.Group("deleted") is not null && current.GetValueOrDefault("deleted")?.Values.Values.GetValueOrDefault("DeletedAtUtc") is null)
                    {
                        var hlc = await changes.NextHlcAsync(request.SpaceId, kind, key, ct);
                        await changes.AppendAsync(request.SpaceId, actor, SyncChange.Upsert(Guid.NewGuid().ToString("D"), kind, key,
                            new Dictionary<string, SyncGroupState>
                            {
                                ["deleted"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?> { ["DeletedAtUtc"] = Instant(clock.GetUtcNow()) })),
                            }), ct);
                        trashed++;
                    }

                    continue;
                }

                var restorations = new Dictionary<string, SyncGroupState>();
                foreach (var group in entityRows.SelectMany(row => SyncJson.ReadGroups(row.Groups).Keys).Distinct(StringComparer.Ordinal))
                {
                    var first = entityRows.First(row => SyncJson.ReadGroups(row.Groups).ContainsKey(group));
                    var last = entityRows.Last(row => SyncJson.ReadGroups(row.Groups).ContainsKey(group));
                    var before = replaced[first]?.GetValueOrDefault(group);
                    if (before is null || !current.TryGetValue(group, out var storedGroup) || storedGroup.Hlc != SyncJson.ReadGroups(last.Groups)[group].Hlc)
                    {
                        skipped.Add($"{kind}:{key}:{group}"); // changed by a person after the agent, or nothing to go back to
                        continue;
                    }

                    restorations[group] = new(await changes.NextHlcAsync(request.SpaceId, kind, key, ct), before);
                }

                if (restorations.Count == 0)
                {
                    continue;
                }

                restored += restorations.Count;
                await changes.AppendAsync(request.SpaceId, actor, SyncChange.Upsert(Guid.NewGuid().ToString("D"), kind, key, restorations), ct);
                if (kind == "task")
                {
                    touchedTasks.Add(key);
                }
            }

            foreach (var task in touchedTasks)
            {
                await RecordAsync(request.SpaceId, task, new ActorId(ActorKind.Person, actor).ToString(), "task.agent-reverted", ["AgentChanges"], clock.GetUtcNow(), ct);
            }

            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.AgentReverted, At = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await StreamNotifications.NotifyAsync(db, broadcaster, request.SpaceId, CancellationToken.None);
            return SpaceOperation<RevertAgentDto>.Success(new(restored, trashed, skipped.Distinct().Order(StringComparer.Ordinal).ToList()));
        });
    }

    // ---- Helpers ------------------------------------------------------------------------

    /// <summary>The groups a change replaced, as they were before it: group → column → value, with values back in the storage shapes of <see cref="SyncJson"/>.</summary>
    private static Dictionary<string, SyncGroupValues>? ReplacedGroups(string? json)
    {
        if (json is null)
        {
            return null;
        }

        // The shape is the one ServerChangeWriter stores: group → { "Values": { column: value } }.
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(group => group.Name,
            group => new SyncGroupValues(group.Value.GetProperty("Values").EnumerateObject().ToDictionary(column => column.Name, column => Scalar(column.Value), StringComparer.Ordinal)),
            StringComparer.Ordinal);
    }

    private static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var whole) ? whole : value.GetDouble(),
        JsonValueKind.True => 1L,
        JsonValueKind.False => 0L,
        JsonValueKind.Null => null,
        _ => throw new FormatException("A stored value is neither text, a number nor null."),
    };

    /// <summary>Counts one claim (or, for the push seam, one added task) against the agent's hourly limit (E8); false when the limit is reached.</summary>
    internal static bool CountUse(AgentRow agent, DateTimeOffset now, bool adds)
    {
        var (counters, allowed, _) = new AgentCounters(agent.RateWindowStart, agent.AddsInWindow, agent.ClaimsInWindow).Use(now, adds);
        if (allowed)
        {
            agent.RateWindowStart = counters.WindowStart;
            agent.AddsInWindow = counters.Adds;
            agent.ClaimsInWindow = counters.Claims;
        }

        return allowed;
    }

    internal static int RetryAfter(AgentRow agent, DateTimeOffset now) =>
        (int)Math.Max(1, ((agent.RateWindowStart ?? now) + AgentLimits.Window - now).TotalSeconds);

    /// <summary>The streams among <paramref name="candidates"/> that hold the task; one, in practice.</summary>
    private async Task<List<Guid>> TaskStreamsAsync(IReadOnlyList<Guid> candidates, string taskId, CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        return await db.SyncEntities.AsNoTracking()
            .Where(e => candidates.Contains(e.AccountId) && e.Kind == "task" && e.EntityKey == taskId && e.PurgedAt == null && e.MovedTo == null)
            .Select(e => e.AccountId).Distinct().ToListAsync(ct);
    }

    private async Task<IReadOnlyDictionary<string, object?>?> SettingsAsync(Guid spaceId, CancellationToken ct)
    {
        var json = await db.SyncEntities.AsNoTracking().Where(e => e.AccountId == spaceId && e.Kind == "space" && e.PurgedAt == null)
            .Select(e => (string?)e.Groups).FirstOrDefaultAsync(ct);
        return json is null ? null : SyncJson.ReadGroups(json).GetValueOrDefault("settings")?.Values.Values;
    }

    private async Task<bool> SpaceAllowsAgentsAsync(Guid spaceId, CancellationToken ct) =>
        (await SettingsAsync(spaceId, ct))?.GetValueOrDefault("AgentsAllowed") is 1L;

    /// <summary>One row of the expiry sweep's candidate query.</summary>
    public sealed record ExpiredClaim(Guid StreamId, string EntityKey, string? Until);

    private async Task<AgentDto> DtoAsync(AgentRow agent, CancellationToken ct) =>
        ToDto(agent, await db.SpaceMembers.AsNoTracking().Where(m => m.MemberKind == SpaceMember.AgentKind && m.MemberId == agent.Id && m.RemovedAt == null).ToListAsync(ct));

    private static AgentDto ToDto(AgentRow agent, IEnumerable<SpaceMember> memberships) => new(agent.Id, agent.Name, agent.CreatedAt, agent.PausedAt,
        [.. memberships.Select(m => new AgentMembershipDto(m.SpaceId, m.JoinedAt, m.AllowedContainers is { } json ? JsonSerializer.Deserialize<Guid[]>(json) : null))]);

    /// <summary>Removes the agent from every space it is a member of, inside the caller's transaction; the claims go first.</summary>
    private async Task<List<Guid>> LeaveSpacesAsync(AgentRow agent, DateTimeOffset now, CancellationToken ct)
    {
        var affected = await ReleaseClaimsAsync(agent, "the agent was removed", now, ct);
        foreach (var member in await db.SpaceMembers.Where(m => m.MemberId == agent.Id && m.MemberKind == SpaceMember.AgentKind && m.RemovedAt == null).ToListAsync(ct))
        {
            member.RemovedAt = now;
            if (!await db.SyncStreams.AsNoTracking().AnyAsync(s => s.Id == member.SpaceId && s.DeletedAt == null, ct))
            {
                continue;
            }

            var key = MemberKey(member.SpaceId, member.MemberId);
            var hlc = await changes.NextHlcAsync(member.SpaceId, "space_member", key, ct);
            await changes.AppendAsync(member.SpaceId, member.InvitedByMemberId ?? member.MemberId, SyncChange.Upsert(Guid.NewGuid().ToString("D"), "space_member", key,
                new Dictionary<string, SyncGroupState>
                {
                    ["state"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?> { ["RemovedAtUtc"] = Instant(now) })),
                }), ct);
            affected.Add(member.SpaceId);
        }

        return affected;
    }

    /// <summary>One transaction on a fresh change tracker, retried by the execution strategy when PostgreSQL asks for it.</summary>
    private async Task<T> transaction<T>(Func<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction, Task<T>> body)
    {
        var execution = db.Database.CreateExecutionStrategy();
        return await execution.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync();
            return await body(tx);
        });
    }

    private async Task<SpaceOperation<T>> strategy<T>(Func<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction, Task<SpaceOperation<T>>> body) =>
        await transaction(body);

    private async Task<Account?> LockedAccountAsync(Guid accountId, CancellationToken ct) =>
        await db.Accounts.FromSqlInterpolated($"""SELECT * FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(ct);

    private static string? ValidName(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.EnumerateRunes().Count() > AgentLimits.MaxNameLength ? null : trimmed;
    }

    private static string Instant(DateTimeOffset value) => value.UtcDateTime.ToString("o");

    private static DateTimeOffset? Instant(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;

    private static string MemberKey(Guid spaceId, Guid memberId) =>
        NameBasedId.Create($"spacemember:{spaceId:D}:{memberId:D}").ToString("D");
}
