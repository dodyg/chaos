using System.ComponentModel;
using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Contracts;
using Chaos.Application.Pages;
using Chaos.Sync;
using Microsoft.Extensions.AI;

namespace Chaos.Application.Agents;

/// <summary>
/// The pad and page tools of PLAN3 12.2, 12.6 for a local agent: it reads the Personal pad, suggests tasks that only a person's
/// click turns into tasks, and reads and writes pages. All of them need at least one ticked list (the agent's access to the
/// Personal space); results carry user text only in <c>text</c>, <c>title</c> and <c>data</c>.
/// </summary>
public sealed partial class LocalAgentTools
{
    private sealed partial class Bound
    {
        private ProposalCommands Proposals => new(_store, _ids, _clock);
        private PageCommands Pages => new(_store, _ids, _clock);

        private IEnumerable<AIFunction> PadPageFunctions()
        {
            var type = typeof(Bound);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(ReadPadAsync))!, this, AgentWorkToolNames.ReadPad, AgentWorkToolNames.ReadPadDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(ProposeTasksAsync))!, this, AgentWorkToolNames.ProposeTasks, AgentWorkToolNames.ProposeTasksDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(ReadPageAsync))!, this, AgentWorkToolNames.ReadPage, AgentWorkToolNames.ReadPageDescription);
            yield return AIFunctionFactory.Create(type.GetMethod(nameof(WritePageAsync))!, this, AgentWorkToolNames.WritePage, AgentWorkToolNames.WritePageDescription);
        }

        private static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

        /// <summary>Only the Personal space is local; a named space other than it is not this agent's.</summary>
        private static bool WrongSpace(Reach reach, string? spaceId) =>
            spaceId is { Length: > 0 } && !string.Equals(spaceId, reach.Personal.Id, StringComparison.OrdinalIgnoreCase);

        public Task<ReadPadResult> ReadPadAsync(
            [Description("The space ID (from list_spaces). Optional when this agent has only one space.")] string? spaceId = null,
            [Description("Skip this many notes (paging).")] int offset = 0,
            CancellationToken cancellationToken = default) =>
            _store.QueryAsync(async (session, token) =>
            {
                var (refusal, reach) = await ReachAsync(session, token);
                if (reach is null) return new ReadPadResult(refusal!, [], false);
                if (reach.Lists.Count == 0 || WrongSpace(reach, spaceId)) return new ReadPadResult(AgentToolStatus.Forbidden, [], false);
                var notes = (await session.Pad.ListBySpaceAsync(reach.Personal.Id, archived: false, token))
                    .Where(item => item.Kind == PadItemKind.Text).OrderByDescending(item => item.UpdatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal)
                    .Skip(Math.Max(0, offset)).Take(PageSize + 1).ToList();
                return new ReadPadResult(AgentToolStatus.Ok,
                    [.. notes.Take(PageSize).Select(item => new AgentPadNote(item.Id, item.SpaceId, item.RawMarkdown, Instant(item.UpdatedAtUtc)))], notes.Count > PageSize);
            }, cancellationToken);

        public async Task<ProposeToolResult> ProposeTasksAsync(
            [Description("The suggested tasks, at most 20.")] ProposedTaskArgument[] tasks,
            [Description("The pad note the suggestions come from (from read_pad). Optional.")] string? padItemId = null,
            [Description("The space ID (from list_spaces). Optional when this agent has only one space.")] string? spaceId = null,
            CancellationToken cancellationToken = default)
        {
            var operationId = Guid.NewGuid().ToString("D");
            using var scope = ActivityScope.Open(ActivitySources.Agent, ActivityActions.AgentProposeTasks, operationId: operationId, actorId: _actor);
            var denied = await _store.QueryAsync<ProposeToolResult?>(async (session, token) =>
            {
                var (refusal, reach) = await ReachAsync(session, token);
                if (reach is null) return new ProposeToolResult(refusal!, []);
                if (reach.Lists.Count == 0 || WrongSpace(reach, spaceId)) return new ProposeToolResult(AgentToolStatus.Forbidden, []);
                var now = _clock.GetUtcNow();
                var since = now - AgentLimits.Window;
                if (await session.Agents.CountActionsAsync(_actor, ActivityActions.AgentProposeTasks, since, token) >= AgentLimits.TasksAddedPerHour)
                {
                    var first = await session.Agents.FirstActionAsync(_actor, ActivityActions.AgentProposeTasks, since, token) ?? now;
                    return new ProposeToolResult(AgentToolStatus.RateLimited, [], (int)Math.Max(1, (first + AgentLimits.Window - now).TotalSeconds));
                }

                return null;
            }, cancellationToken);
            if (denied is not null) return denied;

            try
            {
                var made = await Proposals.ProposeAsync(new ProposeTasksCommand(_agentId, string.IsNullOrWhiteSpace(padItemId) ? null : padItemId,
                    [.. (tasks ?? []).Select(task => new ProposedTask(task.Title, task.Notes, task.QuickAddText))]), cancellationToken);
                return new ProposeToolResult(AgentToolStatus.Created, [.. made.Select(proposal => proposal.Id)]);
            }
            catch (AppException error) when (error.Kind is AppErrorKind.Validation or AppErrorKind.NotFound)
            {
                return new ProposeToolResult(error.Kind == AppErrorKind.NotFound ? AgentToolStatus.NotFound : AgentToolStatus.Invalid, [], Field: error.Fields.FirstOrDefault()?.FieldPath);
            }
        }

        public Task<ReadPageResult> ReadPageAsync(
            [Description("The page ID. Omit to list pages.")] string? pageId = null,
            [Description("The space ID (from list_spaces). Optional when this agent has only one space.")] string? spaceId = null,
            [Description("Skip this many pages when listing (paging).")] int offset = 0,
            CancellationToken cancellationToken = default) =>
            _store.QueryAsync(async (session, token) =>
            {
                var (refusal, reach) = await ReachAsync(session, token);
                if (reach is null) return new ReadPageResult(refusal!, [], false);
                if (reach.Lists.Count == 0 || WrongSpace(reach, spaceId)) return new ReadPageResult(AgentToolStatus.Forbidden, [], false);
                if (pageId is { Length: > 0 })
                {
                    var page = await session.Pages.GetAsync(pageId, token);
                    if (page is null) return new ReadPageResult(AgentToolStatus.NotFound, [], false, pageId);
                    if (page.SpaceId != reach.Personal.Id) return new ReadPageResult(AgentToolStatus.Forbidden, [], false, pageId);
                    return new ReadPageResult(AgentToolStatus.Ok, [], false, page.Id, new PageDataBlock(page.Title, page.RawMarkdown));
                }

                var pages = (await session.Pages.ListBySpaceAsync(reach.Personal.Id, token)).Where(page => page.DailyDate is null)
                    .OrderBy(page => page.Title, StringComparer.OrdinalIgnoreCase).ThenBy(page => page.Id, StringComparer.Ordinal)
                    .Skip(Math.Max(0, offset)).Take(PageSize + 1).ToList();
                return new ReadPageResult(AgentToolStatus.Ok,
                    [.. pages.Take(PageSize).Select(page => new AgentPageInfo(page.Id, page.SpaceId, page.ParentPageId, page.Title, Instant(page.UpdatedAtUtc)))], pages.Count > PageSize);
            }, cancellationToken);

        public async Task<PageToolResult> WritePageAsync(
            [Description("The page title.")] string title,
            [Description("The page text, in Markdown.")] string markdown,
            [Description("The ID of the page to replace. Omit to create a page.")] string? pageId = null,
            [Description("When creating: the ID of the parent page. Optional.")] string? parentPageId = null,
            [Description("The space ID (from list_spaces). Optional when this agent has only one space.")] string? spaceId = null,
            CancellationToken cancellationToken = default)
        {
            var operationId = Guid.NewGuid().ToString("D");
            using var scope = ActivityScope.Open(ActivitySources.Agent, ActivityActions.AgentWritePage, operationId: operationId, actorId: _actor);
            var gate = await _store.QueryAsync<(string? Refusal, PageRecord? Page)>(async (session, token) =>
            {
                var (refusal, reach) = await ReachAsync(session, token);
                if (reach is null) return (refusal, null);
                if (reach.Lists.Count == 0 || WrongSpace(reach, spaceId)) return (AgentToolStatus.Forbidden, null);
                if (pageId is not { Length: > 0 }) return (null, null);
                var page = await session.Pages.GetAsync(pageId, token);
                return page is null ? (AgentToolStatus.NotFound, null) : page.SpaceId != reach.Personal.Id ? (AgentToolStatus.Forbidden, null) : (null, page);
            }, cancellationToken);
            if (gate.Refusal is not null) return new PageToolResult(gate.Refusal, pageId);

            try
            {
                if (gate.Page is { } existing)
                {
                    // Replacing: the next editor revision wins over the stored draft; a person's later edit merges on their device (9.9).
                    var saved = await Pages.SaveAsync(new SavePageCommand(existing.Id, existing.DraftVersion + 1, title, markdown), cancellationToken);
                    return new PageToolResult(saved.Applied ? AgentToolStatus.Ok : AgentToolStatus.Conflict, existing.Id);
                }

                var created = await Pages.CreateAsync(new CreatePageCommand(operationId, string.IsNullOrWhiteSpace(parentPageId) ? null : parentPageId, title, markdown), cancellationToken);
                return new PageToolResult(AgentToolStatus.Created, created.Page.Id);
            }
            catch (AppException error) when (error.Kind is AppErrorKind.Validation or AppErrorKind.NotFound or AppErrorKind.VersionConflict)
            {
                return new PageToolResult(error.Kind switch
                {
                    AppErrorKind.Validation => AgentToolStatus.Invalid,
                    AppErrorKind.NotFound => AgentToolStatus.NotFound,
                    _ => AgentToolStatus.Conflict,
                }, pageId, Field: error.Kind == AppErrorKind.Validation ? error.Fields.FirstOrDefault()?.FieldPath : null);
            }
        }
    }
}
