using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Sync;

namespace Chaos.Application.Agents;

/// <summary>One proposed task as an agent writes it. <paramref name="QuickAddText"/> is kept for the person's Accept to parse.</summary>
public sealed record ProposedTask(string Title, string? Notes, string? QuickAddText);

public sealed record ProposeTasksCommand(string AgentId, string? SourcePadItemId, IReadOnlyList<ProposedTask> Tasks);

/// <param name="Draft">The task as the person confirmed it (the screen parsed the quick-add text); null makes a task from the title and notes in <paramref name="ContainerId"/>.</param>
public sealed record AcceptProposalCommand(string OperationId, string ProposalId, long ExpectedVersion, string? ContainerId, CaptureTaskDraft? Draft = null);

public sealed record DismissProposalCommand(string OperationId, string ProposalId, long ExpectedVersion);

public sealed record ProposalMutationResult(TaskProposalRecord Proposal, TaskRecord? Task, bool Replayed);

/// <summary>An agent proposal with the agent's name, as the pad shows "Suggested by Build bot".</summary>
public sealed record ProposalItem(TaskProposalRecord Proposal, string AgentName);

/// <summary>
/// Pad proposals (PLAN3 12.6). An agent's <c>propose_tasks</c> writes <see cref="TaskProposalRecord"/> rows only; nothing
/// becomes a task until a person accepts it, and accepting runs the ordinary task creation as that person. Dismissing
/// keeps the row as the record of the decision.
/// </summary>
public sealed class ProposalCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public const int MaxPerCall = 20;
    public const int MaxPending = 100;
    public const int MaxTitleLength = 500;
    public const int MaxNotesLength = 20_000;

    /// <summary>
    /// Records proposals for the Personal space. The agent must be live, not paused and hold at least one ticked list; the
    /// pad note, when named, must be a live note of the space. Runs under the agent's activity scope.
    /// </summary>
    public async Task<IReadOnlyList<TaskProposalRecord>> ProposeAsync(ProposeTasksCommand command, CancellationToken ct = default)
    {
        if (command.Tasks.Count is 0 or > MaxPerCall)
        {
            throw AppException.Validation("tasks", $"Propose between 1 and {MaxPerCall} tasks at a time.");
        }

        var cleaned = command.Tasks.Select(task =>
        {
            var title = (task.Title ?? string.Empty).Trim();
            if (title.Length is 0 or > MaxTitleLength) throw AppException.Validation("title", "Each proposal needs a title of at most 500 characters.");
            if ((task.Notes?.Length ?? 0) > MaxNotesLength) throw AppException.Validation("notes", "A proposal's notes are too long.");
            return task with { Title = title, QuickAddText = string.IsNullOrWhiteSpace(task.QuickAddText) ? null : task.QuickAddText.Trim() };
        }).ToList();

        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var agent = await session.Agents.GetAsync(command.AgentId, token) ?? throw AppException.NotFound("That agent no longer exists.");
            if (agent.PausedAtUtc is not null) throw AppException.Validation("agent", "That agent is paused.");
            if ((await session.Agents.ListAccessAsync(agent.Id, includeRevoked: false, token)).Count == 0)
                throw AppException.Validation("agent", "That agent has no lists to work in.");
            var personal = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token) ?? throw AppException.NotFound("There is no Personal space yet.");
            if (command.SourcePadItemId is { } padId
                && (await session.Pad.GetAsync(padId, token) is not { } pad || pad.SpaceId != personal.Id))
                throw AppException.NotFound("That pad note no longer exists.");
            var pending = (await session.Agents.ListProposalsAsync(personal.Id, token)).Count(p => p.State == ProposalState.Pending);
            if (pending + cleaned.Count > MaxPending)
                throw AppException.Validation("tasks", "There are too many suggestions waiting. A person must accept or dismiss some first.");

            var made = new List<TaskProposalRecord>();
            foreach (var task in cleaned)
            {
                var row = new TaskProposalRecord
                {
                    Id = ids.NewId().ToString(),
                    SpaceId = personal.Id,
                    AgentId = agent.Id,
                    SourcePadItemId = command.SourcePadItemId,
                    Title = task.Title,
                    NotesMarkdown = task.Notes ?? string.Empty,
                    QuickAddText = task.QuickAddText,
                    State = ProposalState.Pending,
                    Version = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await session.Agents.AddProposalAsync(row, token);
                made.Add(row);
            }

            return (IReadOnlyList<TaskProposalRecord>)made;
        }, ct);
    }

    /// <summary>The Personal space's proposals still waiting for a person, oldest first.</summary>
    public async Task<IReadOnlyList<ProposalItem>> ListPendingAsync(string? padItemId = null, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<ProposalItem>>(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("No local profile exists yet.");
            var personal = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token);
            if (personal is null) return [];
            var names = (await session.Agents.ListAsync(token)).ToDictionary(agent => agent.Id, agent => agent.Name, StringComparer.OrdinalIgnoreCase);
            return [.. (await session.Agents.ListProposalsAsync(personal.Id, token))
                .Where(p => p.State == ProposalState.Pending && (padItemId is null || string.Equals(p.SourcePadItemId, padItemId, StringComparison.OrdinalIgnoreCase)))
                .Select(p => new ProposalItem(p, names.GetValueOrDefault(p.AgentId, "An agent")))];
        }, ct);

    /// <summary>The person's click: creates the task and, when the proposal came from a pad note that still quotes its title, links it back.</summary>
    public async Task<ProposalMutationResult> AcceptAsync(AcceptProposalCommand command, CancellationToken ct = default)
    {
        Reminders.ReminderCommands.RequireOperation(command.OperationId);
        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "proposals.accept", command, now, async inner =>
            {
                var proposal = await RequirePendingAsync(session, command.ProposalId, command.ExpectedVersion, inner);
                var draft = CaptureCommands.Normalize(command.Draft ?? new CaptureTaskDraft
                {
                    Title = proposal.Title,
                    Notes = proposal.NotesMarkdown,
                    ContainerId = command.ContainerId,
                }, "task");
                var task = await CaptureCommands.CreateInSessionAsync(session, ids, profile, draft, command.OperationId, now,
                    proposal.SourcePadItemId is null ? [] : ["ScratchPad"], inner);

                if (proposal.SourcePadItemId is { } padId && await session.Pad.GetAsync(padId, inner) is { } item)
                {
                    var at = item.RawMarkdown.IndexOf(proposal.Title, StringComparison.OrdinalIgnoreCase);
                    if (at >= 0)
                    {
                        var end = at + proposal.Title.Length;
                        var text = item.RawMarkdown;
                        const int context = Pad.PadCommands.ContextLength;
                        await session.Pad.AddTaskLinkAsync(new ScratchTaskLinkRecord(item.Id, task.Id, item.DraftVersion, at, end, text[at..end], now,
                            ContextBefore: text[Math.Max(0, at - context)..at], ContextAfter: text[end..Math.Min(text.Length, end + context)]), inner);
                    }
                }

                var decided = await session.Agents.UpdateProposalAsync(proposal with
                {
                    State = ProposalState.Accepted, CreatedTaskId = task.Id, DecidedAtUtc = now, Version = proposal.Version + 1, UpdatedAtUtc = now,
                }, proposal.Version, inner) ?? throw AppException.VersionConflict("That suggestion changed elsewhere. Refresh and try again.");
                return new ProposalMutationResult(decided, task, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>The person's other click: the suggestion is closed and no task is made.</summary>
    public async Task<ProposalMutationResult> DismissAsync(DismissProposalCommand command, CancellationToken ct = default)
    {
        Reminders.ReminderCommands.RequireOperation(command.OperationId);
        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "proposals.dismiss", command, now, async inner =>
            {
                var proposal = await RequirePendingAsync(session, command.ProposalId, command.ExpectedVersion, inner);
                var decided = await session.Agents.UpdateProposalAsync(proposal with
                {
                    State = ProposalState.Dismissed, DecidedAtUtc = now, Version = proposal.Version + 1, UpdatedAtUtc = now,
                }, proposal.Version, inner) ?? throw AppException.VersionConflict("That suggestion changed elsewhere. Refresh and try again.");
                return new ProposalMutationResult(decided, null, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    private static async Task<TaskProposalRecord> RequirePendingAsync(IDataSession session, string proposalId, long expectedVersion, CancellationToken ct)
    {
        var profile = await session.Profiles.GetPrimaryAsync(ct) ?? throw AppException.NotFound("No local profile exists yet.");
        var personal = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, ct);
        var proposal = personal is null ? null
            : (await session.Agents.ListProposalsAsync(personal.Id, ct)).FirstOrDefault(p => string.Equals(p.Id, proposalId, StringComparison.OrdinalIgnoreCase));
        if (proposal is null) throw AppException.NotFound("That suggestion no longer exists. Refresh and try again.");
        if (proposal.Version != expectedVersion) throw AppException.VersionConflict("That suggestion changed elsewhere. Refresh and try again.");
        if (proposal.State != ProposalState.Pending) throw AppException.Validation("proposal", "That suggestion was already decided.");
        return proposal;
    }
}
