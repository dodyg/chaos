using System.ComponentModel;
using Chaos.Server.Web;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Chaos.Server.Sync;

/// <summary>
/// The pad and page tools of PLAN3 12.2, 12.6 on the service surface. Pads are read only: an agent's permission table has no
/// pad group, so it can only suggest tasks, as <c>task_proposal</c> rows that a person decides (nothing becomes a task). Pages
/// are written as <c>title</c> and <c>text</c> groups; a member's concurrent edit is merged with them on the member's device
/// (decision E2). Spaces are the agent's reachable ones; results carry user text only in <c>text</c>, <c>title</c> and <c>data</c>.
/// </summary>
public sealed partial class AgentWorkTools
{
    private const int MaxProposals = 20;
    private const int MaxProposalTitleRunes = 500;

    private sealed partial class Bound
    {
        private IEnumerable<AIFunction> PadPageFunctions()
        {
            var type = typeof(Bound);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(ReadPadAsync))!, this, AgentWorkToolNames.ReadPad, AgentWorkToolNames.ReadPadDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(ProposeTasksAsync))!, this, AgentWorkToolNames.ProposeTasks, AgentWorkToolNames.ProposeTasksDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(ReadPageAsync))!, this, AgentWorkToolNames.ReadPage, AgentWorkToolNames.ReadPageDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(WritePageAsync))!, this, AgentWorkToolNames.WritePage, AgentWorkToolNames.WritePageDescription);
        }

        /// <summary>The spaces a call may touch: the named one, or all the agent can reach when none is named. Only spaces with a list are the agent's.</summary>
        private static (string? Refusal, IReadOnlyList<SpaceReach> Spaces) Choose(Reach reach, string? spaceId)
        {
            var usable = reach.Spaces.Where(space => space.Lists.Count > 0).ToList();
            if (spaceId is not { Length: > 0 })
            {
                return (null, usable);
            }

            return Guid.TryParseExact(spaceId, "D", out var id) && usable.FirstOrDefault(space => space.Id == id) is { } chosen
                ? (null, [chosen])
                : (AgentToolStatus.Forbidden, []);
        }

        private async Task<List<(SpaceReach Space, WebEntity Entity, string Key, DateTimeOffset UpdatedAt)>> EntitiesAsync(IReadOnlyList<SpaceReach> spaces, string kind, CancellationToken ct)
        {
            var found = new List<(SpaceReach, WebEntity, string, DateTimeOffset)>();
            foreach (var space in spaces)
            {
                var rows = await _owner.db.SyncEntities.AsNoTracking()
                    .Where(e => e.AccountId == space.Id && e.Kind == kind && e.PurgedAt == null && e.MovedTo == null).ToListAsync(ct);
                foreach (var row in rows)
                {
                    var entity = new WebEntity(row);
                    if (!entity.IsDeleted)
                    {
                        found.Add((space, entity, row.EntityKey, row.UpdatedAt));
                    }
                }
            }

            return found;
        }

        public async Task<ReadPadResult> ReadPadAsync(
            [Description("The space ID (from list_spaces). Optional when this agent has only one space.")] string? spaceId = null,
            [Description("Skip this many notes (paging).")] int offset = 0,
            CancellationToken cancellationToken = default)
        {
            var (refusal, reach) = await _owner.ReachAsync(_agentId, cancellationToken);
            if (reach is null)
            {
                return new ReadPadResult(refusal!, [], false);
            }

            var (denied, spaces) = Choose(reach, spaceId);
            if (denied is not null)
            {
                return new ReadPadResult(denied, [], false);
            }

            var notes = (await EntitiesAsync(spaces, "pad", cancellationToken))
                .Where(pad => pad.Entity.Text("archived", "ArchivedAtUtc") is null && (pad.Entity.Number("create", "Kind") ?? 0) == 0)
                .OrderByDescending(pad => pad.UpdatedAt).ThenBy(pad => pad.Key, StringComparer.Ordinal)
                .Skip(Math.Max(0, offset)).Take(PageSize + 1).ToList();
            return new ReadPadResult(AgentToolStatus.Ok,
                [.. notes.Take(PageSize).Select(pad => new AgentPadNote(pad.Key, pad.Space.Id.ToString("D"), pad.Entity.Text("text", "RawMarkdown") ?? string.Empty, Instant(pad.UpdatedAt)))],
                notes.Count > PageSize);
        }

        public async Task<ProposeToolResult> ProposeTasksAsync(
            [Description("The suggested tasks, at most 20.")] ProposedTaskArgument[] tasks,
            [Description("The pad note the suggestions come from (from read_pad). Optional.")] string? padItemId = null,
            [Description("The space ID (from list_spaces). Optional when this agent has only one space.")] string? spaceId = null,
            CancellationToken cancellationToken = default)
        {
            var (refusal, reach) = await _owner.ReachAsync(_agentId, cancellationToken);
            if (reach is null)
            {
                return new ProposeToolResult(refusal!, []);
            }

            var (denied, spaces) = Choose(reach, spaceId);
            if (denied is not null)
            {
                return new ProposeToolResult(denied, []);
            }

            SpaceReach? space = null;
            if (padItemId is { Length: > 0 })
            {
                var pad = (await EntitiesAsync(spaces, "pad", cancellationToken)).FirstOrDefault(item => string.Equals(item.Key, padItemId, StringComparison.OrdinalIgnoreCase));
                if (pad.Entity is null)
                {
                    return new ProposeToolResult(AgentToolStatus.NotFound, []);
                }

                space = pad.Space;
            }
            else if (spaces.Count == 1)
            {
                space = spaces[0];
            }

            if (space is null)
            {
                return new ProposeToolResult(spaces.Count == 0 ? AgentToolStatus.Forbidden : AgentToolStatus.Invalid, [], Field: spaces.Count == 0 ? null : "spaceId");
            }

            var list = (tasks ?? []).ToList();
            if (list.Count is 0 or > MaxProposals || list.Any(task => task is null || task.Title is not { Length: > 0 } || task.Title.Trim().Length is 0 || task.Title.Trim().EnumerateRunes().Count() > MaxProposalTitleRunes))
            {
                return new ProposeToolResult(AgentToolStatus.Invalid, [], Field: "tasks");
            }

            if (list.Any(task => (task.Notes?.Length ?? 0) > MaxText || (task.QuickAddText?.Length ?? 0) > 2_000))
            {
                return new ProposeToolResult(AgentToolStatus.Invalid, [], Field: "tasks");
            }

            var now = _owner.clock.GetUtcNow();
            var pending = (await EntitiesAsync([space], "task_proposal", cancellationToken)).Count(row => (row.Entity.Number("state", "State") ?? 0) == 0);
            if (pending + list.Count > 100)
            {
                return new ProposeToolResult(AgentToolStatus.Invalid, [], Field: "tasks");
            }

            var ids = new List<string>();
            var pushes = new List<IncomingChange>();
            foreach (var task in list)
            {
                var key = Guid.NewGuid().ToString("D");
                var hlc = await _owner.changes.NextHlcAsync(space.Id, "task_proposal", key, cancellationToken);
                // Only the create group: the decision groups are a person's to write (permission matrix), and a missing one is "pending".
                pushes.Add(Upsert("task_proposal", key, new Dictionary<string, SyncGroupState>
                {
                    ["create"] = Group(hlc, ("SpaceId", space.Id.ToString("D")), ("AgentId", _agentId.ToString("D")),
                        ("SourcePadItemId", padItemId is { Length: > 0 } ? padItemId.ToLowerInvariant() : null), ("Title", task.Title.Trim()),
                        ("NotesMarkdown", task.Notes ?? string.Empty), ("QuickAddText", string.IsNullOrWhiteSpace(task.QuickAddText) ? null : task.QuickAddText.Trim()),
                        ("CreatedAtUtc", Instant(now))),
                }));
                ids.Add(key);
            }

            return await PushKindAsync(pushes, "tasks", failure => new ProposeToolResult(failure.Status, [], failure.RetryAfterSeconds, failure.Field),
                () => new ProposeToolResult(AgentToolStatus.Created, ids), cancellationToken);
        }

        /// <summary>Pushes and turns a refusal into the result's own type; only the code leaves, never text.</summary>
        private async Task<T> PushKindAsync<T>(IReadOnlyList<IncomingChange> pushes, string field, Func<TaskToolResult, T> failed, Func<T> success, CancellationToken ct)
        {
            var refused = await _owner.PushAsync(_agentId, string.Empty, pushes, field, ct);
            return refused is null ? success() : failed(refused);
        }

        public async Task<ReadPageResult> ReadPageAsync(
            [Description("The page ID. Omit to list pages.")] string? pageId = null,
            [Description("The space ID (from list_spaces). Optional when this agent has only one space.")] string? spaceId = null,
            [Description("Skip this many pages when listing (paging).")] int offset = 0,
            CancellationToken cancellationToken = default)
        {
            var (refusal, reach) = await _owner.ReachAsync(_agentId, cancellationToken);
            if (reach is null)
            {
                return new ReadPageResult(refusal!, [], false);
            }

            var (denied, spaces) = Choose(reach, spaceId);
            if (denied is not null)
            {
                return new ReadPageResult(denied, [], false);
            }

            var pages = (await EntitiesAsync(spaces, "page", cancellationToken)).Where(page => page.Entity.Text("create", "DailyDate") is null).ToList();
            if (pageId is { Length: > 0 })
            {
                var page = pages.FirstOrDefault(item => string.Equals(item.Key, pageId, StringComparison.OrdinalIgnoreCase));
                return page.Entity is null
                    ? new ReadPageResult(AgentToolStatus.NotFound, [], false, pageId)
                    : new ReadPageResult(AgentToolStatus.Ok, [], false, page.Key, new PageDataBlock(page.Entity.Text("title", "Title") ?? string.Empty, page.Entity.Text("text", "RawMarkdown") ?? string.Empty));
            }

            var listed = pages.OrderBy(page => page.Entity.Text("title", "Title") ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(page => page.Key, StringComparer.Ordinal)
                .Skip(Math.Max(0, offset)).Take(PageSize + 1).ToList();
            return new ReadPageResult(AgentToolStatus.Ok,
                [.. listed.Take(PageSize).Select(page => new AgentPageInfo(page.Key, page.Space.Id.ToString("D"), page.Entity.Text("tree", "ParentPageId"), page.Entity.Text("title", "Title") ?? string.Empty, Instant(page.UpdatedAt)))],
                listed.Count > PageSize);
        }

        public async Task<PageToolResult> WritePageAsync(
            [Description("The page title.")] string title,
            [Description("The page text, in Markdown.")] string markdown,
            [Description("The ID of the page to replace. Omit to create a page.")] string? pageId = null,
            [Description("When creating: the ID of the parent page. Optional.")] string? parentPageId = null,
            [Description("The space ID (from list_spaces). Optional when this agent has only one space.")] string? spaceId = null,
            CancellationToken cancellationToken = default)
        {
            var (refusal, reach) = await _owner.ReachAsync(_agentId, cancellationToken);
            if (reach is null)
            {
                return new PageToolResult(refusal!, pageId);
            }

            var (denied, spaces) = Choose(reach, spaceId);
            if (denied is not null)
            {
                return new PageToolResult(denied, pageId);
            }

            var cleanTitle = (title ?? string.Empty).Trim();
            if (cleanTitle.Length is 0 || cleanTitle.EnumerateRunes().Count() > 200)
            {
                return new PageToolResult(AgentToolStatus.Invalid, pageId, Field: "title");
            }

            if ((markdown?.Length ?? 0) > 200_000)
            {
                return new PageToolResult(AgentToolStatus.Invalid, pageId, Field: "markdown");
            }

            var pages = await EntitiesAsync(spaces, "page", cancellationToken);
            if (pageId is { Length: > 0 })
            {
                var page = pages.FirstOrDefault(item => string.Equals(item.Key, pageId, StringComparison.OrdinalIgnoreCase));
                if (page.Entity is null || page.Entity.Text("create", "DailyDate") is not null)
                {
                    return new PageToolResult(AgentToolStatus.NotFound, pageId);
                }

                var existing = page.Space.Id;
                var hlc = await _owner.changes.NextHlcAsync(existing, "page", page.Key, cancellationToken);
                return await PushKindAsync([Upsert("page", page.Key, new Dictionary<string, SyncGroupState>
                {
                    ["title"] = Group(hlc, ("Title", cleanTitle)),
                    ["text"] = Group(hlc, ("RawMarkdown", markdown ?? string.Empty)),
                })], "markdown", failure => new PageToolResult(failure.Status, pageId, failure.RetryAfterSeconds, failure.Field), () => new PageToolResult(AgentToolStatus.Ok, page.Key), cancellationToken);
            }

            if (spaces.Count != 1)
            {
                return new PageToolResult(spaces.Count == 0 ? AgentToolStatus.Forbidden : AgentToolStatus.Invalid, null, Field: spaces.Count == 0 ? null : "spaceId");
            }

            var space = spaces[0];
            string? parent = null;
            if (parentPageId is { Length: > 0 })
            {
                var found = pages.FirstOrDefault(item => item.Space.Id == space.Id && string.Equals(item.Key, parentPageId, StringComparison.OrdinalIgnoreCase));
                if (found.Entity is null)
                {
                    return new PageToolResult(AgentToolStatus.NotFound, null, Field: "parentPageId");
                }

                parent = found.Key;
            }

            var key = Guid.NewGuid().ToString("D");
            var now = _owner.clock.GetUtcNow();
            var last = pages.Where(item => item.Space.Id == space.Id && item.Entity.Text("tree", "ParentPageId") == parent)
                .Select(item => item.Entity.Number("position", "Position") ?? 0).DefaultIfEmpty(-1024).Max();
            var createdHlc = await _owner.changes.NextHlcAsync(space.Id, "page", key, cancellationToken);
            return await PushKindAsync([Upsert("page", key, new Dictionary<string, SyncGroupState>
            {
                ["create"] = Group(createdHlc, ("SpaceId", space.Id.ToString("D")), ("DailyDate", null), ("CreatedAtUtc", Instant(now))),
                ["title"] = Group(createdHlc, ("Title", cleanTitle)),
                ["text"] = Group(createdHlc, ("RawMarkdown", markdown ?? string.Empty)),
                ["tree"] = Group(createdHlc, ("ParentPageId", parent)),
                ["position"] = Group(createdHlc, ("Position", Math.Max(0, last + 1024))),
                ["deleted"] = Group(createdHlc, ("DeletedAtUtc", null)),
            })], "markdown", failure => new PageToolResult(failure.Status, null, failure.RetryAfterSeconds, failure.Field), () => new PageToolResult(AgentToolStatus.Created, key), cancellationToken);
        }
    }
}
