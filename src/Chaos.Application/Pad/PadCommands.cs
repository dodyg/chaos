using Chaos.Application.Abstractions;
using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Pad;

public sealed record CreatePadItemCommand(string OperationId, string Markdown);

/// <summary>
/// An autosave of one item's text. <see cref="ClientRevision"/> is the editor's monotonically
/// increasing revision: the store applies the text only when it is newer than the revision
/// it already acknowledged, so a delayed, repeated or reordered save never replaces newer text.
/// </summary>
public sealed record SavePadTextCommand(string ItemId, long ClientRevision, string Markdown);

public sealed record PadItemChange(string ItemId, long ExpectedVersion);

public sealed record PadItemsCommand(string OperationId, IReadOnlyList<PadItemChange> Items);

public sealed record PadItemCommand(string OperationId, string ItemId, long ExpectedVersion);

public sealed record ReorderPadItemCommand(string OperationId, string ItemId, long ExpectedVersion, int InsertionIndex);

/// <summary>
/// "Make task" on a selection of a pad item (PLAN §9). Start/End are UTF-16 offsets into the
/// item's text at <see cref="SourceRevision"/> and <see cref="Quote"/> is the selected text.
/// The renderer parses the selection with quick add and sends the confirmed <see cref="Draft"/>;
/// without one, the explicit title/notes/list/date/tag fields describe the task.
/// </summary>
public sealed record MakeTaskFromPadCommand(
    string OperationId,
    string PadItemId,
    long SourceRevision,
    int Start,
    int End,
    string Quote,
    string Title,
    string? Notes,
    string? ContainerId,
    DateOnly? PlannedDate,
    DateOnly? DueDate,
    IReadOnlyList<string> TagIds,
    CaptureTaskDraft? Draft = null);

/// <summary>Finishes the weekly pad review, archiving the items the user chose (possibly none).</summary>
public sealed record CompletePadReviewCommand(string OperationId, IReadOnlyList<PadItemChange> Archive);

public sealed record PadTaskLink(ScratchTaskLinkRecord Link, string Title, bool Deleted);

/// <summary>A pad item as the pad shows it: the row, its active tags and the tasks made from it.</summary>
public sealed record PadItemView(PadItemRecord Item, IReadOnlyList<string> TagIds, IReadOnlyList<PadTaskLink> Tasks);

public sealed record PadMutationResult(PadItemRecord Item, bool Replayed);

public sealed record PadItemsResult(IReadOnlyList<PadItemRecord> Items, bool Replayed);

/// <summary>Applied is false when the store already held this or a newer revision; Item is then the stored state.</summary>
public sealed record PadSaveResult(PadItemRecord Item, bool Applied);

public sealed record MakeTaskFromPadResult(TaskRecord Task, ScratchTaskLinkRecord Link, bool Replayed);

public enum PadAnchorState
{
    /// <summary>The item text is unchanged since the task was made: the stored offsets are valid.</summary>
    Exact = 0,
    /// <summary>The text changed but the quoted text is still there: these are its current offsets.</summary>
    Relocated = 1,
    /// <summary>The quoted text is gone: open the item and show the old quote, never another spot.</summary>
    Missing = 2,
}

/// <summary>Where a task's pad backlink points now (PLAN §9 stale-anchor fallback).</summary>
public sealed record PadAnchor(string PadItemId, string TaskId, PadAnchorState State, int? Start, int? End, string Quote, bool Archived, bool Deleted);

public sealed record PadLinkTarget(string Id, string Excerpt, bool Deleted);

/// <summary>The weekly review prompt: due when a week passed since the last review and old items exist.</summary>
public sealed record PadReviewStatus(bool Due, DateTimeOffset? LastReviewedAtUtc, IReadOnlyList<PadItemRecord> Suggestions);

/// <summary>
/// The Personal scratch pad (PLAN §9, §15 WP5): untitled Markdown items with order, tags and
/// archive; revision-aware autosave; "Make task" from a selection with a backlink that
/// survives later edits; a weekly review that suggests archiving; and search. Structural
/// changes are recorded operations (receipt, history, undo). Autosaves are not: typing keeps
/// its own text undo in the editor, and the revision check makes them idempotent.
/// </summary>
public sealed class PadCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    /// <summary>Items untouched this long are suggested for archive in the weekly review.</summary>
    public static readonly TimeSpan ReviewAge = TimeSpan.FromDays(7);

    public async Task<IReadOnlyList<PadItemView>> ListAsync(bool archived, string? tagId, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PadItemView>>(async (session, token) =>
        {
            var (profile, space) = await PersonalSpaceAsync(session, token);
            var items = await session.Pad.ListBySpaceAsync(space.Id, archived, token);
            HashSet<string>? scope = null;
            if (!string.IsNullOrEmpty(tagId))
            {
                scope = await TagScopeAsync(session, profile.Id, tagId, token);
            }

            var views = new List<PadItemView>(items.Count);
            foreach (var item in items)
            {
                var view = await ViewAsync(session, item, token);
                if (scope is null || view.TagIds.Any(scope.Contains))
                {
                    views.Add(view);
                }
            }

            return views;
        }, ct);

    public async Task<PadItemView> GetAsync(string itemId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var item = await session.Pad.GetIncludingDeletedAsync(itemId, token) ?? throw NotFound();
            return await ViewAsync(session, item, token);
        }, ct);

    public async Task<IReadOnlyList<PadItemRecord>> ListDeletedAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var (_, space) = await PersonalSpaceAsync(session, token);
            return await session.Pad.ListDeletedBySpaceAsync(space.Id, token);
        }, ct);

    /// <summary>Active items (archived included) whose typed text matches, best first.</summary>
    public async Task<IReadOnlyList<PadItemView>> SearchAsync(string text, int limit, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PadItemView>>(async (session, token) =>
        {
            var (_, space) = await PersonalSpaceAsync(session, token);
            var views = new List<PadItemView>();
            foreach (var id in await session.Pad.SearchAsync(space.Id, text, Math.Clamp(limit, 1, 100), token))
            {
                if (await session.Pad.GetAsync(id, token) is { } item)
                {
                    views.Add(await ViewAsync(session, item, token));
                }
            }

            return views;
        }, ct);

    /// <summary>Readable labels for `[[` pad links, resolved by ID so edits keep the link working.</summary>
    public async Task<IReadOnlyList<PadLinkTarget>> ResolveLinksAsync(IReadOnlyList<string> itemIds, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<PadLinkTarget>>(async (session, token) =>
        {
            var targets = new List<PadLinkTarget>();
            foreach (var id in itemIds.Distinct(StringComparer.OrdinalIgnoreCase).Take(200))
            {
                if (await session.Pad.GetIncludingDeletedAsync(id, token) is { } item)
                {
                    targets.Add(new PadLinkTarget(item.Id, PadExcerpt.Of(item.RawMarkdown) ?? "empty note", item.DeletedAtUtc is not null));
                }
            }

            return targets;
        }, ct);

    public async Task<PadMutationResult> CreateAsync(CreatePadItemCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = command with { Markdown = TaskFieldValidation.Notes(command.Markdown, "markdown") };
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "pad.create", normalized, now, async inner =>
            {
                var (_, space) = await PersonalSpaceAsync(session, inner);
                var item = new PadItemRecord
                {
                    Id = ids.NewId().ToString(),
                    SpaceId = space.Id,
                    RawMarkdown = normalized.Markdown,
                    Position = await session.Pad.GetNextPositionAsync(space.Id, inner),
                    DraftVersion = 0,
                    Version = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await session.Pad.AddAsync(item, inner);
                return new PadMutationResult(item, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>
    /// Autosave (PLAN §9): applies the text when <see cref="SavePadTextCommand.ClientRevision"/>
    /// is newer than the acknowledged revision, otherwise returns the stored item unchanged.
    /// Not an undo entry; the editor's own text undo covers typing.
    /// </summary>
    public async Task<PadSaveResult> SaveTextAsync(SavePadTextCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (command.ClientRevision < 1)
        {
            throw AppException.Validation("clientRevision", "The editor revision must be 1 or more.");
        }

        var markdown = TaskFieldValidation.Notes(command.Markdown, "markdown");
        return await InWriteAsync(async (session, _, now, token) =>
        {
            var item = await session.Pad.GetIncludingDeletedAsync(command.ItemId, token) ?? throw NotFound();
            if (item.DeletedAtUtc is not null)
            {
                throw AppException.Validation("itemId", "This note was deleted. Restore it to keep writing; your text is still in the editor.");
            }

            if (command.ClientRevision <= item.DraftVersion)
            {
                return new PadSaveResult(item, Applied: false);
            }

            var applied = await session.Pad.UpdateAsync(
                item with { RawMarkdown = markdown, DraftVersion = command.ClientRevision, Version = item.Version + 1, UpdatedAtUtc = now },
                item.Version, token) ?? throw Conflict();
            return new PadSaveResult(applied, Applied: true);
        }, ct);
    }

    public async Task<PadItemsResult> ArchiveAsync(PadItemsCommand command, CancellationToken ct = default) =>
        await ChangeManyAsync(command.OperationId, "pad.archive", command, command.Items,
            (item, now) => item.ArchivedAtUtc is null ? item with { ArchivedAtUtc = now } : null, ct);

    public async Task<PadItemsResult> UnarchiveAsync(PadItemsCommand command, CancellationToken ct = default) =>
        await ChangeManyAsync(command.OperationId, "pad.unarchive", command, command.Items,
            (item, _) => item.ArchivedAtUtc is null ? null : item with { ArchivedAtUtc = null }, ct);

    /// <summary>Reversible deletion; nothing on the pad is deleted without the user asking (PLAN §9).</summary>
    public async Task<PadMutationResult> DeleteAsync(PadItemCommand command, CancellationToken ct = default) =>
        await ChangeOneAsync(command, "pad.delete", (item, now) => item.DeletedAtUtc is null ? item with { DeletedAtUtc = now } : null, ct);

    public async Task<PadMutationResult> RestoreAsync(PadItemCommand command, CancellationToken ct = default) =>
        await ChangeOneAsync(command, "pad.restore", (item, _) => item.DeletedAtUtc is null ? null : item with { DeletedAtUtc = null }, ct);

    /// <summary>Manual order among the visible (unarchived) items, with the shared rank rules.</summary>
    public async Task<PadMutationResult> ReorderAsync(ReorderPadItemCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, command.OperationId, "pad.reorder", command, now, async inner =>
            {
                var item = await session.Pad.GetAsync(command.ItemId, inner) ?? throw NotFound();
                RequireVersion(item, command.ExpectedVersion);
                if (item.ArchivedAtUtc is not null)
                {
                    throw AppException.Validation("itemId", "Unarchive this note before moving it.");
                }

                var siblings = await session.Pad.ListBySpaceAsync(item.SpaceId, archived: false, inner);
                var moved = item;
                foreach (var assignment in RankOrdering.Place([.. siblings.Select(row => (row.Id, row.Position, row.Version))], item.Id, command.InsertionIndex))
                {
                    var row = siblings.Single(candidate => candidate.Id == assignment.Id);
                    var applied = await session.Pad.UpdateAsync(row with { Position = assignment.NewPosition, Version = row.Version + 1, UpdatedAtUtc = now }, row.Version, inner)
                        ?? throw Conflict();
                    if (applied.Id == item.Id)
                    {
                        moved = applied;
                    }
                }

                return new PadMutationResult(moved, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>
    /// Creates the task and its backlink in one transaction (PLAN §9). The item text is never
    /// changed. When the editor's revision is current the offsets must select the quote
    /// exactly; when the text moved on, the quote is found again (nearest occurrence) or the
    /// command is refused, so a task never points at unrelated text.
    /// </summary>
    public async Task<MakeTaskFromPadResult> MakeTaskAsync(MakeTaskFromPadCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var draft = CaptureCommands.Normalize(command.Draft ?? new CaptureTaskDraft
        {
            Title = command.Title,
            Notes = command.Notes,
            ContainerId = command.ContainerId,
            PlannedDate = command.PlannedDate,
            DueDate = command.DueDate,
            TagIds = command.TagIds,
        }, command.Draft is null ? "task" : "draft");
        var normalized = command with
        {
            Title = draft.Title,
            Notes = draft.Notes,
            ContainerId = draft.ContainerId,
            TagIds = draft.TagIds,
            Draft = draft,
        };
        if (string.IsNullOrWhiteSpace(normalized.Quote))
        {
            throw AppException.Validation("quote", "Select some text on the note first.");
        }

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "pad.make-task", normalized, now, async inner =>
            {
                var item = await session.Pad.GetAsync(normalized.PadItemId, inner) ?? throw NotFound();
                var (start, end) = AnchorForNewTask(item, normalized);
                var task = await CaptureCommands.CreateInSessionAsync(session, ids, profile, draft, normalized.OperationId, now, ["ScratchPad"], inner);

                var text = item.RawMarkdown;
                var link = new ScratchTaskLinkRecord(item.Id, task.Id, item.DraftVersion, start, end, text[start..end], now,
                    ContextBefore: text[Math.Max(0, start - ContextLength)..start], ContextAfter: text[end..Math.Min(text.Length, end + ContextLength)]);
                await session.Pad.AddTaskLinkAsync(link, inner);
                return new MakeTaskFromPadResult(task, link, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    /// <summary>Where the pad backlink of a task points now; null when the task was not made from the pad.</summary>
    public async Task<PadAnchor?> LocateAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var link = await session.Pad.FindTaskLinkAsync(taskId, token);
            if (link is null)
            {
                return null;
            }

            var item = await session.Pad.GetIncludingDeletedAsync(link.PadItemId, token) ?? throw NotFound();
            var found = Locate(item.RawMarkdown, item.DraftVersion, link);
            var state = found is null ? PadAnchorState.Missing
                : item.DraftVersion == link.SourceRevision && found.Value.Start == link.StartIndex ? PadAnchorState.Exact
                : PadAnchorState.Relocated;
            return new PadAnchor(item.Id, taskId, state, found?.Start, found?.End, link.Quote, item.ArchivedAtUtc is not null, item.DeletedAtUtc is not null);
        }, ct);

    public async Task<PadReviewStatus> ReviewStatusAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            var (profile, space) = await PersonalSpaceAsync(session, token);
            var now = clock.GetUtcNow();
            var receipts = await session.Receipts.ListByKindAsync(profile.Id, "pad.review", token);
            DateTimeOffset? last = receipts.Count == 0 ? null : receipts.Max(receipt => receipt.CommittedAtUtc);
            var suggestions = (await session.Pad.ListBySpaceAsync(space.Id, archived: false, token))
                .Where(item => now - item.UpdatedAtUtc >= ReviewAge)
                .ToList();
            var due = suggestions.Count > 0 && (last is null || now - last.Value >= ReviewAge);
            return new PadReviewStatus(due, last, suggestions);
        }, ct);

    /// <summary>Records that the weekly review happened and archives the chosen items as one undoable change.</summary>
    public async Task<PadItemsResult> CompleteReviewAsync(CompletePadReviewCommand command, CancellationToken ct = default) =>
        await ChangeManyAsync(command.OperationId, "pad.review", command, command.Archive,
            (item, now) => item.ArchivedAtUtc is null ? item with { ArchivedAtUtc = now } : null, ct);

    /// <summary>Characters of context kept on each side of a backlink's quote.</summary>
    public const int ContextLength = 32;

    /// <summary>
    /// The current offsets of a backlink's quote: the stored ones while the text is the one the
    /// task was made from; otherwise the occurrence whose surroundings best match the stored
    /// context (nearest to the old start on a tie); or null when the quote is gone.
    /// </summary>
    public static (int Start, int End)? Locate(string text, long currentRevision, ScratchTaskLinkRecord link)
    {
        if (link.Quote.Length == 0)
        {
            return null;
        }

        if (currentRevision == link.SourceRevision && Matches(text, link.StartIndex, link.EndIndex, link.Quote))
        {
            return (link.StartIndex, link.EndIndex);
        }

        int? best = null;
        var bestScore = -1;
        for (var index = text.IndexOf(link.Quote, StringComparison.Ordinal); index >= 0; index = text.IndexOf(link.Quote, index + 1, StringComparison.Ordinal))
        {
            var score = SharedSuffix(link.ContextBefore, text, index) + SharedPrefix(link.ContextAfter, text, index + link.Quote.Length);
            if (best is null || score > bestScore
                || (score == bestScore && Math.Abs(index - link.StartIndex) < Math.Abs(best.Value - link.StartIndex)))
            {
                best = index;
                bestScore = score;
            }
        }

        return best is { } start ? (start, start + link.Quote.Length) : null;
    }

    /// <summary>How many characters right before <paramref name="end"/> equal the end of <paramref name="context"/>.</summary>
    private static int SharedSuffix(string? context, string text, int end)
    {
        var count = 0;
        while (context is not null && count < context.Length && count < end && context[^(count + 1)] == text[end - count - 1])
        {
            count++;
        }

        return count;
    }

    /// <summary>How many characters from <paramref name="start"/> equal the start of <paramref name="context"/>.</summary>
    private static int SharedPrefix(string? context, string text, int start)
    {
        var count = 0;
        while (context is not null && count < context.Length && start + count < text.Length && context[count] == text[start + count])
        {
            count++;
        }

        return count;
    }

    private static (int Start, int End) AnchorForNewTask(PadItemRecord item, MakeTaskFromPadCommand command)
    {
        var text = item.RawMarkdown;
        if (command.SourceRevision == item.DraftVersion)
        {
            if (command.Start < 0 || command.End > text.Length || command.Start >= command.End)
            {
                throw AppException.Validation("start", "The selection is outside the note. Select the text again.");
            }

            if (SplitsSurrogatePair(text, command.Start) || SplitsSurrogatePair(text, command.End))
            {
                throw AppException.Validation("start", "The selection splits a character. Select whole characters.");
            }

            if (!Matches(text, command.Start, command.End, command.Quote))
            {
                throw AppException.Validation("quote", "The selected text does not match the saved note. Select the text again.");
            }

            return (command.Start, command.End);
        }

        var relocated = Locate(text, item.DraftVersion, new ScratchTaskLinkRecord(item.Id, string.Empty, command.SourceRevision, command.Start, command.End, command.Quote, default))
            ?? throw AppException.Validation("quote", "The selected text changed before the task was made. Select it again.");
        return relocated;
    }

    private static bool SplitsSurrogatePair(string text, int index) =>
        index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]);

    private static bool Matches(string text, int start, int end, string quote) =>
        start >= 0 && end <= text.Length && start <= end && end - start == quote.Length
        && string.CompareOrdinal(text, start, quote, 0, quote.Length) == 0;

    private async Task<PadMutationResult> ChangeOneAsync(PadItemCommand command, string kind, Func<PadItemRecord, DateTimeOffset, PadItemRecord?> change, CancellationToken ct)
    {
        var result = await ChangeManyAsync(command.OperationId, kind, command, [new PadItemChange(command.ItemId, command.ExpectedVersion)], change, ct);
        return new PadMutationResult(result.Items[0], result.Replayed);
    }

    /// <summary>One operation over several items: all versions must match or nothing changes.</summary>
    private async Task<PadItemsResult> ChangeManyAsync(
        string operationId,
        string kind,
        object payload,
        IReadOnlyList<PadItemChange> items,
        Func<PadItemRecord, DateTimeOffset, PadItemRecord?> change,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (kind != "pad.review" && items.Count == 0)
        {
            throw AppException.Validation("items", "Choose at least one note.");
        }

        if (items.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            throw AppException.Validation("items", "A note was chosen twice.");
        }

        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, operationId, kind, payload, now, async inner =>
            {
                var changed = new List<PadItemRecord>(items.Count);
                foreach (var request in items)
                {
                    var item = await session.Pad.GetIncludingDeletedAsync(request.ItemId, inner) ?? throw NotFound();
                    RequireVersion(item, request.ExpectedVersion);
                    if (item.DeletedAtUtc is not null && kind is not ("pad.delete" or "pad.restore"))
                    {
                        throw AppException.Validation("items", "A chosen note was deleted. Restore it first.");
                    }

                    if (change(item, now) is not { } updated)
                    {
                        changed.Add(item); // already in the requested state
                        continue;
                    }

                    changed.Add(await session.Pad.UpdateAsync(updated with { Version = item.Version + 1, UpdatedAtUtc = now }, item.Version, inner) ?? throw Conflict());
                }

                return new PadItemsResult(changed, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    private static async Task<PadItemView> ViewAsync(IDataSession session, PadItemRecord item, CancellationToken token)
    {
        var links = new List<PadTaskLink>();
        foreach (var link in await session.Pad.ListTaskLinksAsync(item.Id, token))
        {
            if (await session.Tasks.GetIncludingDeletedAsync(link.TaskId, token) is { } task)
            {
                links.Add(new PadTaskLink(link, task.Title, task.DeletedAtUtc is not null));
            }
        }

        return new PadItemView(item, await session.Tags.ListPadTagIdsAsync(item.Id, token), links);
    }

    /// <summary>The tag and every active tag nested inside it.</summary>
    private static async Task<HashSet<string>> TagScopeAsync(IDataSession session, string profileId, string tagId, CancellationToken token)
    {
        var all = await session.Tags.ListByOwnerAsync(profileId, token);
        var scope = new HashSet<string>(StringComparer.Ordinal) { tagId };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var tag in all)
            {
                if (tag.ParentTagId is { } parent && scope.Contains(parent) && scope.Add(tag.Id))
                {
                    grew = true;
                }
            }
        }

        return scope;
    }

    private static async Task<(ProfileRecord Profile, SpaceRecord Space)> PersonalSpaceAsync(IDataSession session, CancellationToken token)
    {
        var profile = await session.Profiles.GetPrimaryAsync(token)
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "No local profile exists yet. Restart Chaos to complete first-run setup.");
        var space = await session.Spaces.GetPersonalByOwnerAsync(profile.Id, token)
            ?? throw new AppException(AppErrorKind.StorageUnavailable, "No personal space exists yet. Restart Chaos to complete first-run setup.");
        return (profile, space);
    }

    private static void RequireVersion(PadItemRecord item, long expectedVersion)
    {
        if (item.Version != expectedVersion)
        {
            throw Conflict();
        }
    }

    private static AppException NotFound() => AppException.NotFound("That pad note no longer exists. Refresh and try again.");

    private static AppException Conflict() => AppException.VersionConflict("This note changed while you were working. Refresh and try again.");

    private async Task<T> InWriteAsync<T>(Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<T>> body, CancellationToken ct) =>
        await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, body, ct);
}
