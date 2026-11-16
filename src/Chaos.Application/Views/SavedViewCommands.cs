using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Application.Tasks;
using Chaos.Domain.Tags;
using Chaos.Domain.Validation;
using Chaos.Domain.Views;

namespace Chaos.Application.Views;

public sealed record SaveViewCommand(
    string OperationId,
    string? ViewId,
    long ExpectedVersion,
    string Name,
    string? Icon,
    string? Color,
    string DefinitionJson,
    string? LayoutJson,
    bool Pinned,
    bool AlertsEnabled);

public sealed record SavedViewResult(SavedViewRecord View, bool Replayed);

/// <summary>
/// Saved custom views (PLAN2 §11.2): create, update, duplicate, delete, restore, reorder and pin.
/// The definition and layout are parsed and re-serialized before they are stored, so a stored
/// view is always valid and canonical. Deleting only tombstones the row, so it can be restored
/// (the app's "Undo"). Enabling alerts stores the current matches as a silent baseline.
/// </summary>
public sealed class SavedViewCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public const int MaxViews = 200;

    /// <summary>The fixed icon set a view can use (lucide names, PLAN2 §11.2).</summary>
    public static readonly IReadOnlyList<string> Icons =
    [
        "list", "star", "flag", "bug", "calendar", "clock", "inbox", "briefcase", "home", "user", "users", "tag",
        "bookmark", "heart", "target", "zap", "bell", "square-check", "folder", "git-branch", "github", "book-open",
        "coffee", "sun", "moon", "layers", "rocket", "wrench",
    ];

    public async Task<IReadOnlyList<SavedViewRecord>> ListAsync(bool includeDeleted = false, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
            return await session.SavedViews.ListAsync(context.Space.Id, includeDeleted, token);
        }, ct);

    public async Task<SavedViewRecord> GetAsync(string viewId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) => await RequireAsync(session, viewId, token), ct);

    /// <summary>What quick add inside this view pre-fills, from its top-level "all" conditions.</summary>
    public async Task<ViewContextDefaults> ContextDefaultsAsync(string viewId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var view = await RequireAsync(session, viewId, token);
            var context = await TaskQueries.ContextAsync(session, clock.GetUtcNow(), token);
            return ViewFields.ContextDefaults(ParseDefinition(view.DefinitionJson), context.Today, context.Profile.FirstWeekday);
        }, ct);

    public async Task<SavedViewResult> SaveAsync(SaveViewCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = Normalize(command);
        var kind = normalized.ViewId is null ? "view.create" : "view.update";

        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, normalized.OperationId, kind, normalized, now, async inner =>
                {
                    var space = await TaskQueries.ContextAsync(session, now, inner);
                    var existing = normalized.ViewId is null ? null : await RequireAsync(session, normalized.ViewId, inner);
                    if (existing is { DeletedAtUtc: not null })
                    {
                        throw AppException.NotFound("That view was deleted. Restore it before changing it.");
                    }

                    SavedViewRecord saved;
                    if (existing is null)
                    {
                        var all = await session.SavedViews.ListAsync(space.Space.Id, includeDeleted: false, inner);
                        if (all.Count >= MaxViews)
                        {
                            throw AppException.Validation("name", $"You can keep at most {MaxViews} views. Delete one first.");
                        }

                        saved = new SavedViewRecord
                        {
                            Id = StorageFormats.Id(ids.NewId()),
                            SpaceId = space.Space.Id,
                            Name = normalized.Name,
                            Icon = normalized.Icon!,
                            Color = normalized.Color,
                            Pinned = normalized.Pinned,
                            Position = all.Count == 0 ? 0 : all.Max(view => view.Position) + 1,
                            DefinitionJson = normalized.DefinitionJson,
                            LayoutJson = normalized.LayoutJson!,
                            AlertsEnabled = normalized.AlertsEnabled,
                            Version = 1,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now,
                        };
                        await session.SavedViews.AddAsync(saved, inner);
                    }
                    else
                    {
                        RequireVersion(existing, normalized.ExpectedVersion);
                        saved = await session.SavedViews.UpdateAsync(existing with
                        {
                            Name = normalized.Name,
                            Icon = normalized.Icon!,
                            Color = normalized.Color,
                            Pinned = normalized.Pinned,
                            DefinitionJson = normalized.DefinitionJson,
                            LayoutJson = normalized.LayoutJson!,
                            AlertsEnabled = normalized.AlertsEnabled,
                            Version = existing.Version + 1,
                            UpdatedAtUtc = now,
                        }, existing.Version, inner) ?? throw ConflictError();
                    }

                    // Alerts start silent: the current matches become the baseline, and a changed
                    // definition re-baselines, so editing a view never notifies about old tasks.
                    var rebaseline = saved.AlertsEnabled
                        && (existing is null || !existing.AlertsEnabled || existing.DefinitionJson != saved.DefinitionJson);
                    if (rebaseline)
                    {
                        await ViewAlertBaseline.StoreAsync(session, saved, now, inner);
                    }
                    else if (!saved.AlertsEnabled && existing is { AlertsEnabled: true })
                    {
                        await session.SavedViews.ReplaceMatchesAsync(saved.Id, [], inner);
                    }

                    return new SavedViewResult(saved, Replayed: false);
                }, token);

            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    public async Task<SavedViewResult> DuplicateAsync(string operationId, string viewId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var source = await GetAsync(viewId, ct);
        if (source.DeletedAtUtc is not null)
        {
            throw AppException.NotFound("That view was deleted.");
        }

        var copyName = $"{source.Name} (copy)";
        if (copyName.Length > 80)
        {
            copyName = copyName[..80];
        }

        // The duplicate is a normal create with its own payload; alerts stay off until switched on.
        return await SaveAsync(new SaveViewCommand(
            operationId, null, 0, copyName, source.Icon, source.Color, source.DefinitionJson, source.LayoutJson, source.Pinned, AlertsEnabled: false), ct);
    }

    public async Task<SavedViewResult> SetPinnedAsync(string operationId, string viewId, long expectedVersion, bool pinned, CancellationToken ct = default) =>
        await MutateAsync(operationId, "view.pin", new { viewId, expectedVersion, pinned }, viewId, expectedVersion, requireActive: true,
            (view, now) => view with { Pinned = pinned, Version = view.Version + 1, UpdatedAtUtc = now }, ct);

    public async Task<SavedViewResult> DeleteAsync(string operationId, string viewId, long expectedVersion, CancellationToken ct = default) =>
        await MutateAsync(operationId, "view.delete", new { viewId, expectedVersion }, viewId, expectedVersion, requireActive: true,
            (view, now) => view with { DeletedAtUtc = now, Version = view.Version + 1, UpdatedAtUtc = now }, ct);

    public async Task<SavedViewResult> RestoreAsync(string operationId, string viewId, long expectedVersion, CancellationToken ct = default) =>
        await MutateAsync(operationId, "view.restore", new { viewId, expectedVersion }, viewId, expectedVersion, requireActive: false,
            (view, now) => view with { DeletedAtUtc = null, Version = view.Version + 1, UpdatedAtUtc = now }, ct);

    /// <summary>Puts the listed views in this order (positions 0..n-1); views not listed keep their relative order after them.</summary>
    public async Task<IReadOnlyList<SavedViewRecord>> ReorderAsync(string operationId, IReadOnlyList<string> viewIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (viewIds.Count == 0 || viewIds.Count > MaxViews || viewIds.Distinct(StringComparer.Ordinal).Count() != viewIds.Count)
        {
            throw AppException.Validation("viewIds", "List each view once.");
        }

        var payload = new { viewIds };
        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, _) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, operationId, "view.reorder", payload, now, async inner =>
                {
                    var space = await TaskQueries.ContextAsync(session, now, inner);
                    var active = await session.SavedViews.ListAsync(space.Space.Id, includeDeleted: false, inner);
                    var byId = active.ToDictionary(view => view.Id, StringComparer.Ordinal);
                    if (viewIds.Any(id => !byId.ContainsKey(id)))
                    {
                        throw AppException.NotFound("A view in the new order no longer exists. Refresh and try again.");
                    }

                    var order = viewIds.Concat(active.Select(view => view.Id).Where(id => !viewIds.Contains(id, StringComparer.Ordinal))).ToList();
                    var changed = new List<SavedViewRecord>();
                    for (var position = 0; position < order.Count; position++)
                    {
                        var view = byId[order[position]];
                        if (view.Position != position)
                        {
                            view = await session.SavedViews.UpdateAsync(view with { Position = position, Version = view.Version + 1, UpdatedAtUtc = now }, view.Version, inner)
                                ?? throw ConflictError();
                        }

                        changed.Add(view);
                    }

                    return new ReorderEnvelope(changed);
                }, token);
            return result.Views;
        }, ct);
    }

    private sealed record ReorderEnvelope(IReadOnlyList<SavedViewRecord> Views);

    private async Task<SavedViewResult> MutateAsync(
        string operationId, string kind, object payload, string viewId, long expectedVersion, bool requireActive,
        Func<SavedViewRecord, DateTimeOffset, SavedViewRecord> change, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(viewId))
        {
            throw AppException.Validation("viewId", "Choose a view.");
        }

        return await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, operationId, kind, payload, now, async inner =>
                {
                    var existing = await RequireAsync(session, viewId, inner);
                    if (requireActive && existing.DeletedAtUtc is not null)
                    {
                        throw AppException.NotFound("That view was already deleted.");
                    }

                    RequireVersion(existing, expectedVersion);
                    var saved = await session.SavedViews.UpdateAsync(change(existing, now), existing.Version, inner) ?? throw ConflictError();
                    if (saved.AlertsEnabled && existing.DeletedAtUtc is not null && saved.DeletedAtUtc is null)
                    {
                        // A restored view re-baselines: tasks that matched while it was deleted are not news.
                        await ViewAlertBaseline.StoreAsync(session, saved, now, inner);
                    }

                    return new SavedViewResult(saved, Replayed: false);
                }, token);

            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    private static async Task<SavedViewRecord> RequireAsync(IDataSession session, string viewId, CancellationToken token) =>
        await session.SavedViews.GetAsync(viewId, token) ?? throw AppException.NotFound("That view no longer exists. Refresh and try again.");

    private static void RequireVersion(SavedViewRecord view, long expectedVersion)
    {
        if (view.Version != expectedVersion)
        {
            throw ConflictError();
        }
    }

    private static AppException ConflictError() =>
        AppException.VersionConflict("This view changed elsewhere. Reload it and try again.");

    internal static bool CanParseDefinition(string json)
    {
        try
        {
            ViewDefinition.Parse(json);
            return true;
        }
        catch (DomainValidationException)
        {
            return false;
        }
    }

    internal static ViewDefinition ParseDefinition(string json)
    {
        try
        {
            return ViewDefinition.Parse(json);
        }
        catch (DomainValidationException error)
        {
            throw AppException.Validation("definition", error.Message);
        }
    }

    private static SaveViewCommand Normalize(SaveViewCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.OperationId))
        {
            throw AppException.Validation("operationId", "An operation ID is required.");
        }

        var name = (command.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 80)
        {
            throw AppException.Validation("name", "Give the view a name of 1 to 80 characters.");
        }

        var icon = string.IsNullOrWhiteSpace(command.Icon) ? "list" : command.Icon.Trim();
        if (!Icons.Contains(icon))
        {
            throw AppException.Validation("icon", "Choose one of the view icons.");
        }

        string? color;
        try
        {
            color = TagColors.Normalize(command.Color);
        }
        catch (DomainValidationException error)
        {
            throw AppException.Validation("color", error.Message);
        }

        var definition = ParseDefinition(command.DefinitionJson ?? string.Empty);
        ViewLayout layout;
        try
        {
            layout = string.IsNullOrWhiteSpace(command.LayoutJson) ? ViewLayout.Default : ViewLayout.Parse(command.LayoutJson);
        }
        catch (DomainValidationException error)
        {
            throw AppException.Validation("layout", error.Message);
        }

        if (layout.SortField == "manual" && !HasSingleContainer(definition))
        {
            throw AppException.Validation("layout", "Manual order needs the view to show exactly one list or project.");
        }

        return command with
        {
            Name = name,
            Icon = icon,
            Color = color,
            DefinitionJson = definition.ToJson(),
            LayoutJson = layout.ToJson(),
            ViewId = string.IsNullOrWhiteSpace(command.ViewId) ? null : command.ViewId,
        };
    }

    private static bool HasSingleContainer(ViewDefinition definition) =>
        definition.Root.Match == ViewMatch.All
        && definition.Root.Items.OfType<ViewCondition>().Any(condition => condition is { Field: "container", Operator: "is" or "in", Values.Count: 1 });
}
