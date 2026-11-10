using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;

namespace Chaos.Application.History;

public sealed record UndoOperationCommand(string OperationId, string TargetOperationId);

/// <summary>An undoable operation as the undo list shows it.</summary>
public sealed record UndoEntrySummary(string OperationId, string Label, DateTimeOffset AppliedAtUtc);

public sealed record UndoResult(string Label, int ChangedCount, bool Replayed);

/// <summary>
/// Operation-level undo (PLAN §9). Every recorded operation keeps its change set; undoing it
/// restores each before-image (or tombstones what the operation created) and reverses its
/// tag-link changes, in one transaction. Each affected row must still be at the version the
/// operation left it at: any later change anywhere in the set makes the whole undo a
/// conflict and nothing is applied. The undo writes its own history ("undo") but no undo
/// entry; redo is out of scope.
/// </summary>
public sealed class UndoCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    /// <summary>Newest undoable operations first.</summary>
    public async Task<IReadOnlyList<UndoEntrySummary>> ListAsync(int limit, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<UndoEntrySummary>>(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token);
            if (profile is null)
            {
                return [];
            }

            var rows = await session.Undo.ListOpenAsync(profile.Id, Math.Clamp(limit, 1, RecordingDataSession.UndoLimit), token);
            return [.. rows.Select(row => new UndoEntrySummary(row.OperationId, row.Label, row.AppliedAtUtc))];
        }, ct);

    public async Task<UndoResult> UndoAsync(UndoOperationCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(command.TargetOperationId))
        {
            throw AppException.Validation("targetOperationId", "Choose the change to undo.");
        }

        return await TaskMutationSupport.InRecordedTransactionAsync(store, clock, ids, async (session, profile, now, token) =>
        {
            session.SuppressUndoEntry = true;
            session.HistoryEventKind = "undo";
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(
                session, profile.Id, command.OperationId, "undo.apply", command, now, async inner =>
                {
                    var entry = await session.Undo.FindByOperationAsync(profile.Id, command.TargetOperationId, inner)
                        ?? throw AppException.NotFound("That change can no longer be undone. Only the latest 100 changes are kept.");
                    if (entry.UndoneAtUtc is not null)
                    {
                        throw AppException.OperationConflict($"“{entry.Label}” was already undone.");
                    }

                    var changes = StoredJsonFormats.ReadSnapshot<ChangeSet>(entry.InversePayload)
                        ?? throw new AppException(AppErrorKind.StorageUnavailable, "The saved undo information could not be read. Nothing was changed.");
                    var successorId = changes.Entities.Any(item => item.Kind == ChangeKind.RecurrenceOccurrence)
                        ? changes.Entities.FirstOrDefault(item => item.Kind == ChangeKind.Task && item.Before is null)?.Id : null;

                    // Check every row first: a single later change refuses the undo before anything is written.
                    foreach (var change in changes.Entities)
                    {
                        var (version, name) = await CurrentAsync(session, change, inner);
                        if (version != change.AfterVersion)
                        {
                            throw successorId is null ? Conflict(entry.Label, name) : RecurrenceConflict(entry.Label, successorId);
                        }

                        // A diary or tag change does not necessarily increment the task row.
                        // Never remove an occurrence created by this operation if later work
                        // has been recorded against it, even when its row version is unchanged.
                        if (change.Kind == ChangeKind.Task && change.Before is null &&
                            changes.Entities.Any(item => item.Kind == ChangeKind.RecurrenceOccurrence))
                        {
                            var history = await session.History.ListByTaskAsync(change.Id, inner);
                            if (history.Any(item => item.OperationId != entry.OperationId))
                                throw RecurrenceConflict(entry.Label, successorId!);
                        }
                    }

                    foreach (var link in changes.Links.Reverse())
                    {
                        await RevertLinkAsync(session, link, entry.Label, inner);
                    }

                    var rewritten = new Dictionary<(ChangeKind Kind, string Id, long Version), long>();
                    foreach (var change in changes.Entities.Reverse())
                    {
                        var (beforeVersion, newVersion) = await RevertEntityAsync(session, change, entry.Label, now, inner);
                        if (beforeVersion is { } version)
                        {
                            rewritten[(change.Kind, change.Id, version)] = newVersion;
                        }
                    }

                    if (!await session.Undo.MarkUndoneAsync(entry.Id, now, inner))
                    {
                        throw AppException.OperationConflict($"“{entry.Label}” was already undone.");
                    }

                    await RepointOlderEntriesAsync(session, profile.Id, rewritten, inner);

                    return new UndoResult(entry.Label, changes.Entities.Count + changes.Links.Count, Replayed: false);
                }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    private static AppException Conflict(string label, string? name) => AppException.VersionConflict(name is null
        ? $"Undo “{label}” would overwrite a later change. Nothing was undone."
        : $"Undo “{label}” would overwrite a later change to “{name}”. Nothing was undone.");

    private static AppException RecurrenceConflict(string label, string successorId) => new(AppErrorKind.VersionConflict,
        $"Undo “{label}” would remove an occurrence with later work. Nothing was undone. Review the successor before deciding what to do.",
        [new AppErrorField("recurrenceSuccessorId", successorId)]);

    /// <summary>The row's current version and display name, or (null, null) when it is gone.</summary>
    private static async Task<(long? Version, string? Name)> CurrentAsync(IDataSession session, EntityChange change, CancellationToken ct) => change.Kind switch
    {
        ChangeKind.Task => await session.Tasks.GetIncludingDeletedAsync(change.Id, ct) is { } row ? (row.Version, row.Title) : (null, null),
        ChangeKind.Subtask => await session.Subtasks.GetIncludingDeletedAsync(change.Id, ct) is { } row ? (row.Version, row.Title) : (null, null),
        ChangeKind.Container => await session.Containers.GetIncludingDeletedAsync(change.Id, ct) is { } row ? (row.Version, row.Name) : (null, null),
        ChangeKind.Section => await session.Sections.GetIncludingDeletedAsync(change.Id, ct) is { } row ? (row.Version, row.Title) : (null, null),
        ChangeKind.Template => await session.Templates.GetIncludingDeletedAsync(change.Id, ct) is { } row ? (row.Version, row.Name) : (null, null),
        ChangeKind.Tag => await session.Tags.GetIncludingDeletedAsync(change.Id, ct) is { } row ? (row.Version, row.DisplayName) : (null, null),
        ChangeKind.Diary => await session.Diary.GetIncludingDeletedAsync(change.Id, ct) is { } row ? (row.Version, "diary entry") : (null, null),
        ChangeKind.Pad => await session.Pad.GetIncludingDeletedAsync(change.Id, ct) is { } row ? (row.Version, PadExcerpt.Of(row.RawMarkdown)) : (null, null),
        ChangeKind.RecurrenceSeries => await session.Recurrence.GetSeriesAsync(change.Id, ct) is { } row ? (row.Version, "recurrence series") : (null, null),
        ChangeKind.RecurrenceOccurrence => await session.Recurrence.GetOccurrenceAsync(change.Id, ct) is { } row ? (row.Version, "recurrence occurrence") : (null, null),
        ChangeKind.ReminderRule => await session.ReminderRules.GetAsync(change.Id, ct) is { } row ? (row.Version, "reminder rule") : (null, null),
        ChangeKind.Waiting => await session.Waiting.GetAsync(change.Id, ct) is { } row ? (row.Version, "waiting details") : (null, null),
        ChangeKind.FollowUp => await session.Waiting.GetFollowUpAsync(change.Id, ct) is { } row ? (row.Version, "follow-up") : (null, null),
        _ => (null, null),
    };

    private static async Task RevertLinkAsync(IDataSession session, LinkChange link, string label, CancellationToken ct)
    {
        // Undo applies the opposite set operation; if it changes nothing, someone already
        // changed that link after the operation, so the inverse would not be exact.
        var applied = (link.Kind, link.PresentAfter) switch
        {
            (LinkKind.TaskTag, true) => await session.Tags.RemoveTaskLinkAsync(link.TargetId, link.TagId, ct),
            (LinkKind.TaskTag, false) => await session.Tags.AddTaskLinkAsync(link.TargetId, link.TagId, ct),
            (LinkKind.SubtaskTag, true) => await session.Tags.RemoveSubtaskLinkAsync(link.TargetId, link.TagId, ct),
            (LinkKind.SubtaskTag, false) => await session.Tags.AddSubtaskLinkAsync(link.TargetId, link.TagId, ct),
            (LinkKind.PadTag, true) => await session.Tags.RemovePadLinkAsync(link.TargetId, link.TagId, ct),
            _ => await session.Tags.AddPadLinkAsync(link.TargetId, link.TagId, ct),
        };
        if (!applied)
        {
            var tag = await session.Tags.GetIncludingDeletedAsync(link.TagId, ct);
            throw Conflict(label, tag?.DisplayName);
        }
    }

    private static async Task<(long? BeforeVersion, long NewVersion)> RevertEntityAsync(IDataSession session, EntityChange change, string label, DateTimeOffset now, CancellationToken ct)
    {
        switch (change.Kind)
        {
            case ChangeKind.ReminderRule:
                return await RevertAsync(change, label, _ => "reminder rule",
                    id => session.ReminderRules.GetAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with { Version = current.Version + 1, UpdatedAtUtc = now },
                    (row, expected) => session.ReminderRules.UpdateAsync(row, expected, ct));
            case ChangeKind.Waiting:
                return await RevertAsync(change, label, _ => "waiting details",
                    id => session.Waiting.GetAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with { Version = current.Version + 1, UpdatedAtUtc = now },
                    (row, expected) => session.Waiting.UpdateAsync(row, expected, ct));
            case ChangeKind.FollowUp:
                return await RevertAsync(change, label, _ => "follow-up",
                    id => session.Waiting.GetFollowUpAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with { Version = current.Version + 1, UpdatedAtUtc = now },
                    (row, expected) => session.Waiting.UpdateFollowUpAsync(row, expected, ct));
            case ChangeKind.RecurrenceSeries:
                return await RevertAsync(change, label, _ => "recurrence series",
                    id => session.Recurrence.GetSeriesAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with { Version = current.Version + 1, UpdatedAtUtc = now },
                    (row, expected) => session.Recurrence.UpdateSeriesAsync(row, expected, ct));
            case ChangeKind.RecurrenceOccurrence:
                return await RevertAsync(change, label, _ => "recurrence occurrence",
                    id => session.Recurrence.GetOccurrenceAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with { Version = current.Version + 1, UpdatedAtUtc = now },
                    (row, expected) => session.Recurrence.UpdateOccurrenceAsync(row, expected, ct));
            case ChangeKind.Task:
                return await RevertAsync(change, label, row => row.Title,
                    id => session.Tasks.GetIncludingDeletedAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with { Version = current.Version + 1, UpdatedAtUtc = now },
                    (row, expected) => session.Tasks.UpdateAsync(row, expected, ct));
            case ChangeKind.Subtask:
                return await RevertAsync(change, label, row => row.Title,
                    id => session.Subtasks.GetIncludingDeletedAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with { Version = current.Version + 1, UpdatedAtUtc = now },
                    (row, expected) => session.Subtasks.UpdateAsync(row, expected, ct));
            case ChangeKind.Container:
                return await RevertAsync(change, label, row => row.Name,
                    id => session.Containers.GetIncludingDeletedAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with { Version = current.Version + 1, UpdatedAtUtc = now },
                    (row, expected) => session.Containers.UpdateAsync(row, expected, ct));
            case ChangeKind.Section:
                return await RevertAsync(change, label, row => row.Title,
                    id => session.Sections.GetIncludingDeletedAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with { Version = current.Version + 1, UpdatedAtUtc = now },
                    (row, expected) => session.Sections.UpdateAsync(row, expected, ct));
            case ChangeKind.Template:
                return await RevertAsync(change, label, row => row.Name,
                    id => session.Templates.GetIncludingDeletedAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with { Version = current.Version + 1, UpdatedAtUtc = now },
                    (row, expected) => session.Templates.UpdateAsync(row, expected, ct));
            case ChangeKind.Tag:
                return await RevertAsync(change, label, row => row.DisplayName,
                    id => session.Tags.GetIncludingDeletedAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with { Version = current.Version + 1, UpdatedAtUtc = now },
                    (row, expected) => session.Tags.UpdateAsync(row, expected, ct));
            case ChangeKind.Diary:
                // A diary undo is a new revision with the earlier text, never a rewind of the trail.
                return await RevertAsync(change, label, _ => "diary entry",
                    id => session.Diary.GetIncludingDeletedAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with
                    {
                        Revision = current.Revision + 1,
                        EditedAtUtc = now,
                        Version = current.Version + 1,
                        UpdatedAtUtc = now,
                    },
                    (row, expected) => session.Diary.UpdateAsync(row, expected, ct));
            case ChangeKind.Pad:
                // The editor revision only moves forward, so a later autosave of the restored
                // text is never mistaken for a stale one.
                return await RevertAsync(change, label, row => PadExcerpt.Of(row.RawMarkdown) ?? "pad note",
                    id => session.Pad.GetIncludingDeletedAsync(id, ct),
                    (current, before) => (before ?? current with { DeletedAtUtc = now }) with { DraftVersion = current.DraftVersion, Version = current.Version + 1, UpdatedAtUtc = now },
                    (row, expected) => session.Pad.UpdateAsync(row, expected, ct));
            default:
                throw new AppException(AppErrorKind.StorageUnavailable, "The saved undo information names an unknown kind of item. Nothing was changed.");
        }
    }

    private static async Task<(long? BeforeVersion, long NewVersion)> RevertAsync<T>(
        EntityChange change,
        string label,
        Func<T, string> name,
        Func<string, Task<T?>> load,
        Func<T, T?, T> inverse,
        Func<T, long, Task<T?>> write)
        where T : AggregateRecord
    {
        var current = await load(change.Id) ?? throw Conflict(label, null);
        if (current.Version != change.AfterVersion)
        {
            throw Conflict(label, name(current));
        }

        var before = change.Before is null ? null : JsonSerializer.Deserialize<T>(change.Before, StoredJsonFormats.Options);
        var written = await write(inverse(current, before), current.Version) ?? throw Conflict(label, name(current));
        return (before?.Version, written.Version);
    }

    /// <summary>
    /// After an undo, a restored row has the content it had at its before-version but a new
    /// version number. Older open entries that expected the before-version are re-pointed to
    /// the new number, so undoing several operations newest-first stays exact while any real
    /// later edit is still a conflict.
    /// </summary>
    private static async Task RepointOlderEntriesAsync(
        IDataSession session,
        string profileId,
        Dictionary<(ChangeKind Kind, string Id, long Version), long> rewritten,
        CancellationToken ct)
    {
        if (rewritten.Count == 0)
        {
            return;
        }

        foreach (var older in await session.Undo.ListOpenAsync(profileId, RecordingDataSession.UndoLimit, ct))
        {
            if (StoredJsonFormats.ReadSnapshot<ChangeSet>(older.InversePayload) is not { } changes)
            {
                continue;
            }

            var touched = false;
            var entities = changes.Entities.Select(change =>
            {
                if (!rewritten.TryGetValue((change.Kind, change.Id, change.AfterVersion), out var version))
                {
                    return change;
                }

                touched = true;
                return change with { AfterVersion = version };
            }).ToList();
            if (!touched)
            {
                continue;
            }

            var updated = changes with { Entities = entities };
            await session.Undo.ReplacePayloadAsync(
                older.Id,
                StoredJsonFormats.Snapshot(updated),
                JsonSerializer.Serialize(entities.ToDictionary(change => $"{change.Kind}:{change.Id}", change => change.AfterVersion), StoredJsonFormats.Options),
                ct);
        }
    }
}
