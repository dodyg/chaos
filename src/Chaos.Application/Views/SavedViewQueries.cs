using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;
using Chaos.Domain.Views;

namespace Chaos.Application.Views;

public sealed record ViewRunResult(TaskQueryPage Page, ViewLayout Layout);

/// <summary>
/// Running saved and draft views (PLAN2 §11.2). A saved view and a draft being edited go through
/// the same <see cref="TaskQueries"/> specification as built-in views, so the editor's live preview
/// count, the sidebar count and the opened list always agree.
/// </summary>
public sealed class SavedViewQueries(IApplicationStore store, TaskQueries queries, TimeProvider clock)
{
    public const int MaxExportRows = 5000;

    public async Task<ViewRunResult> RunAsync(string viewId, CapacityCategory? category, int limit, string? cursor, CancellationToken ct = default)
    {
        var view = await LoadAsync(viewId, ct);
        var layout = ParseLayout(view.LayoutJson);
        var page = await queries.RunAsync(Spec(view.DefinitionJson, layout, category, limit, cursor, view.SpaceId), ct);
        return new ViewRunResult(page, layout);
    }

    /// <summary>The unsaved definition from the editor: a small page of rows and the exact count.</summary>
    public async Task<TaskQueryPage> RunDraftAsync(string definitionJson, string? layoutJson, CapacityCategory? category, int limit, CancellationToken ct = default)
    {
        var layout = string.IsNullOrWhiteSpace(layoutJson) ? ViewLayout.Default : ParseLayout(layoutJson);
        return await queries.RunAsync(Spec(definitionJson, layout, category, limit, null), ct);
    }

    /// <summary>Counts of every active pinned view, from the same specification as its rows.</summary>
    public async Task<IReadOnlyDictionary<string, long>> CountsAsync(CapacityCategory? category, CancellationToken ct = default)
    {
        var views = await store.QueryAsync(async (session, token) =>
        {
            var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
            return await SavedViewCommands.ListVisibleAsync(session, context, includeDeleted: false, token);
        }, ct);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var view in views.Where(view => view.Pinned))
        {
            // A view saved by a newer build cannot be read here; the sidebar shows it without a number.
            if (!SavedViewCommands.CanParseDefinition(view.DefinitionJson))
            {
                continue;
            }

            counts[view.Id] = await queries.CountAsync(Spec(view.DefinitionJson, ViewLayout.Default, category, 1, null, view.SpaceId), ct);
        }

        return counts;
    }

    /// <summary>The GitHub repositories the person subscribed to, for the repository picker of the GitHub condition group.</summary>
    public async Task<IReadOnlyList<(string Id, string FullName)>> GitHubRepositoriesAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
            var spaceIds = new List<string> { context.Space.Id };
            foreach (var space in await session.Spaces.ListAsync(token))
                if (space.Id != context.Space.Id && space.DeletedAtUtc is null && (await session.People.ListAsync(space.Id, token))
                    .Any(person => !person.Former && person.ActorId == $"person:{context.Profile.Id}"))
                    spaceIds.Add(space.Id);
            var options = new List<(string Id, string FullName)>();
            foreach (var spaceId in spaceIds)
            {
                var connection = await session.GitHub.GetConnectionForSpaceAsync(spaceId, token);
                if (connection is null) continue;
                var repositories = await session.GitHub.ListSubscriptionsAsync(connection.Id, token);
                options.AddRange(repositories.Where(repository => repository.DeletedAtUtc is null)
                    .Select(repository => (repository.Id, repository.FullName)));
            }
            return (IReadOnlyList<(string Id, string FullName)>)[.. options.OrderBy(item => item.FullName, StringComparer.OrdinalIgnoreCase)];
        }, ct);

    /// <summary>Builds the CSV for a view's layout columns (rows up to <see cref="MaxExportRows"/>).</summary>
    public async Task<(string Csv, string FileName, int Rows)> BuildCsvAsync(string viewId, CapacityCategory? category, CancellationToken ct = default)
    {
        var view = await LoadAsync(viewId, ct);
        var layout = ParseLayout(view.LayoutJson);
        var rows = new List<TaskQueryRow>();
        string? cursor = null;
        do
        {
            var page = await queries.RunAsync(Spec(view.DefinitionJson, layout, category, TaskQueries.MaxLimit, cursor, view.SpaceId), ct);
            rows.AddRange(page.Rows);
            cursor = page.NextCursor;
        }
        while (cursor is not null && rows.Count < MaxExportRows);

        var tagNames = await store.QueryAsync(async (session, token) =>
        {
            var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
            var tags = await session.Tags.ListByOwnerAsync(context.Profile.Id, token);
            return (IReadOnlyDictionary<string, string>)tags.ToDictionary(tag => tag.Id, tag => tag.DisplayName, StringComparer.Ordinal);
        }, ct);
        return (ViewExport.Csv(rows, layout.Columns, tagNames), ViewExport.FileName(view.Name, clock.GetUtcNow()), rows.Count);
    }

    private async Task<SavedViewRecord> LoadAsync(string viewId, CancellationToken ct) =>
        await store.QueryAsync(async (session, token) =>
        {
            var view = await session.SavedViews.GetAsync(viewId, token) ?? throw AppException.NotFound("That view no longer exists. Refresh and try again.");
            return view.DeletedAtUtc is null ? view : throw AppException.NotFound("That view was deleted.");
        }, ct);

    private static ViewLayout ParseLayout(string json)
    {
        try
        {
            return ViewLayout.Parse(json);
        }
        catch (DomainValidationException error)
        {
            throw AppException.Validation("layout", error.Message);
        }
    }

    private static TaskQuerySpec Spec(string definitionJson, ViewLayout layout, CapacityCategory? category, int limit, string? cursor, string? viewSpaceId = null) => new()
    {
        View = TaskView.Custom,
        SharedViewSpaceId = viewSpaceId,
        Definition = SavedViewCommands.ParseDefinition(definitionJson),
        Sort = new ViewSort(layout.SortField, layout.SortDescending),
        Category = category,
        Limit = limit,
        Cursor = cursor,
    };
}
