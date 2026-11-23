using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Queries;
using Chaos.Application.Reminders;

namespace Chaos.Application.Views;

/// <summary>An alert as the person sees it: the view it belongs to and what newly matched.</summary>
public sealed record ViewAlertItem(ViewAlertRecord Alert, string ViewName, bool ViewAvailable);

/// <summary>
/// Alerts for custom views (PLAN2 §11.2). Switching alerts on stores the current matches silently as
/// a baseline (see <see cref="ViewAlertBaseline"/>). Each evaluation compares the view's current
/// matches with that baseline on this device: a task never seen before is new; one that left and came
/// back is new again only after 24 hours; everything else is quiet. New tasks are grouped into one
/// alert per view ("3 new tasks in My open bugs"), held until quiet hours end, then delivered. An alert
/// is shown inside the app; a view that was deleted, or whose alerts were switched off, cancels its
/// pending alert instead of showing it.
/// </summary>
public sealed class ViewAlertService(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    /// <summary>A task that left a view and returns sooner than this is not announced again.</summary>
    public static readonly TimeSpan ReturnQuietPeriod = TimeSpan.FromHours(24);

    /// <summary>Baseline rows of tasks that left are kept this long, then forgotten (a later return then counts as new).</summary>
    public static readonly TimeSpan LeftRetention = TimeSpan.FromDays(7);

    public const int MaxTaskIdsKept = 50;
    private const int DeliveryBatch = 50;

    /// <summary>Compares every alerting view with its baseline and raises grouped alerts; returns how many views got new matches.</summary>
    public async Task<int> EvaluateAsync(CancellationToken ct = default) =>
        await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var profile = await session.Profiles.GetPrimaryAsync(token)
                ?? throw new AppException(AppErrorKind.StorageUnavailable, "No local profile exists yet. Restart Chaos to complete first-run setup.");
            var context = await TaskQueries.ContextAsync(session, now, token);
            var zone = ReminderSync.ProfileZone(profile);
            var raised = 0;
            foreach (var view in await SavedViewCommands.ListVisibleAsync(session, context, includeDeleted: false, token))
            {
                if (!view.AlertsEnabled || !SavedViewCommands.CanParseDefinition(view.DefinitionJson))
                {
                    continue;
                }

                if (!await session.SavedViews.HasAlertBaselineAsync(view.Id, token))
                {
                    await ViewAlertBaseline.StoreAsync(session, view, now, token);
                    continue;
                }
                var current = await ViewAlertBaseline.CurrentMatchesAsync(session, view, now, token);
                var fresh = await ApplyBaselineAsync(session, view, current, now, token);
                if (fresh.Count == 0)
                {
                    continue;
                }

                await RaiseAsync(session, view, fresh, DeliveryInstant(profile, zone, now), now, token);
                raised++;
            }

            return raised;
        }, ct);

    /// <summary>Delivers every due alert (outside quiet hours) and returns them for the host to show.</summary>
    public async Task<IReadOnlyList<ViewAlertItem>> DeliverDueAsync(CancellationToken ct = default) =>
        await store.ExecuteAsync<IReadOnlyList<ViewAlertItem>>(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var delivered = new List<ViewAlertItem>();
            foreach (var alert in await session.SavedViews.ListDueAlertsAsync(now, DeliveryBatch, token))
            {
                var view = await session.SavedViews.GetAsync(alert.ViewId, token);
                if (view is null || view.DeletedAtUtc is not null || !view.AlertsEnabled)
                {
                    await session.SavedViews.UpdateAlertAsync(alert with { State = ViewAlertState.Cancelled, UpdatedAtUtc = now }, ViewAlertState.Pending, token);
                    continue;
                }

                // An older alert of the same view still on screen is replaced by this one.
                foreach (var older in (await session.SavedViews.ListDeliveredAlertsAsync(100, token)).Where(item => item.ViewId == alert.ViewId))
                {
                    await session.SavedViews.UpdateAlertAsync(older with { State = ViewAlertState.Acknowledged, AcknowledgedAtUtc = now, UpdatedAtUtc = now }, ViewAlertState.Delivered, token);
                }

                var shown = alert with
                {
                    State = ViewAlertState.Delivered,
                    DeliveredAtUtc = now,
                    Presentation = NotificationPresentation.InApp,
                    UpdatedAtUtc = now,
                };
                if (await session.SavedViews.UpdateAlertAsync(shown, ViewAlertState.Pending, token))
                {
                    delivered.Add(new ViewAlertItem(shown, view.Name, ViewAvailable: true));
                }
            }

            return delivered;
        }, ct);

    /// <summary>Delivered alerts still waiting to be dismissed, newest first.</summary>
    public async Task<IReadOnlyList<ViewAlertItem>> ListAsync(int limit = 20, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<ViewAlertItem>>(async (session, token) =>
        {
            var items = new List<ViewAlertItem>();
            foreach (var alert in await session.SavedViews.ListDeliveredAlertsAsync(Math.Clamp(limit, 1, 100), token))
            {
                var view = await session.SavedViews.GetAsync(alert.ViewId, token);
                var available = view is { DeletedAtUtc: null };
                items.Add(new ViewAlertItem(alert, view?.Name ?? "(deleted view)", available));
            }

            return items;
        }, ct);

    /// <summary>Dismisses a delivered alert; dismissing one already dismissed (or gone) is not an error.</summary>
    public async Task AcknowledgeAsync(string alertId, CancellationToken ct = default) =>
        await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            if (await session.SavedViews.GetAlertAsync(alertId, token) is { State: ViewAlertState.Delivered } alert)
            {
                await session.SavedViews.UpdateAlertAsync(alert with { State = ViewAlertState.Acknowledged, AcknowledgedAtUtc = now, UpdatedAtUtc = now }, ViewAlertState.Delivered, token);
            }

            return 0;
        }, ct);

    /// <summary>
    /// Updates one view's baseline from its current matches and returns the tasks to announce, in the
    /// view's order. New tasks and tasks back after more than 24 hours are announced; tasks back sooner
    /// are re-marked present silently; tasks that left are timestamped, and forgotten after a week.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ApplyBaselineAsync(
        IDataSession session, SavedViewRecord view, IReadOnlyList<string> current, DateTimeOffset now, CancellationToken token)
    {
        var stored = (await session.SavedViews.ListMatchesAsync(view.Id, token)).ToDictionary(match => match.TaskId, StringComparer.Ordinal);
        var currentSet = current.ToHashSet(StringComparer.Ordinal);
        var upserts = new List<SavedViewMatchRecord>();
        var removed = new List<string>();
        var fresh = new List<string>();

        foreach (var taskId in current)
        {
            if (!stored.TryGetValue(taskId, out var row))
            {
                fresh.Add(taskId);
                upserts.Add(new SavedViewMatchRecord(view.Id, taskId, now, null));
            }
            else if (row.LeftAtUtc is { } left)
            {
                if (now - left >= ReturnQuietPeriod)
                {
                    fresh.Add(taskId);
                    upserts.Add(new SavedViewMatchRecord(view.Id, taskId, now, null));
                }
                else
                {
                    upserts.Add(row with { LeftAtUtc = null });
                }
            }
        }

        foreach (var row in stored.Values.Where(row => !currentSet.Contains(row.TaskId)))
        {
            if (row.LeftAtUtc is null)
            {
                upserts.Add(row with { LeftAtUtc = now });
            }
            else if (now - row.LeftAtUtc.Value > LeftRetention)
            {
                removed.Add(row.TaskId);
            }
        }

        if (upserts.Count > 0 || removed.Count > 0)
        {
            await session.SavedViews.ApplyMatchChangesAsync(view.Id, upserts, removed, token);
        }

        return fresh;
    }

    private async Task RaiseAsync(IDataSession session, SavedViewRecord view, IReadOnlyList<string> fresh, DateTimeOffset deliverAt, DateTimeOffset now, CancellationToken token)
    {
        var pending = await session.SavedViews.FindPendingAlertAsync(view.Id, token);
        if (pending is not null)
        {
            // New matches found before the alert is delivered join it: one alert per view.
            var merged = pending.TaskIds.Concat(fresh).Distinct(StringComparer.Ordinal).Take(MaxTaskIdsKept).ToList();
            await session.SavedViews.UpdateAlertAsync(pending with { TaskCount = pending.TaskCount + fresh.Count, TaskIds = merged, UpdatedAtUtc = now }, ViewAlertState.Pending, token);
            return;
        }

        var first = await session.Tasks.GetAsync(fresh[0], token);
        await session.SavedViews.AddAlertAsync(new ViewAlertRecord
        {
            Id = StorageFormats.Id(ids.NewId()),
            ViewId = view.Id,
            TaskCount = fresh.Count,
            TaskIds = [.. fresh.Take(MaxTaskIdsKept)],
            FirstTaskTitle = first?.Title ?? string.Empty,
            DeliverAtUtc = deliverAt,
            State = ViewAlertState.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        }, token);
    }

    private static DateTimeOffset DeliveryInstant(ProfileRecord profile, TimeZoneInfo zone, DateTimeOffset now) =>
        ReminderSync.DeliveryInstant(profile, zone, now);
}
