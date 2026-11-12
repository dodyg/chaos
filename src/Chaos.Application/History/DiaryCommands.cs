using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;

namespace Chaos.Application.History;

public sealed record AddDiaryEntryCommand(string OperationId, string TaskId, string Markdown, DateTimeOffset? EntryAtUtc, bool PostToGitHub = false);

public sealed record EditDiaryEntryCommand(string OperationId, string EntryId, long ExpectedVersion, string Markdown);

public sealed record DiaryEntryChangeCommand(string OperationId, string EntryId, long ExpectedVersion, bool DeleteOnGitHub = false);

public sealed record DiaryMutationResult(DiaryEntryRecord Entry, bool Replayed);

/// <summary>One item of a task's timeline: a diary entry or a field-history event, in time order.</summary>
public sealed record TimelineItem(DateTimeOffset AtUtc, DiaryEntryRecord? Diary, TaskHistoryRecord? Event);

/// <summary>
/// The task diary (PLAN §9): dated raw-Markdown updates authored by the local profile. The
/// entry instant is captured explicitly when written; edits keep every earlier text as a
/// revision; deletion is reversible. Each change is a task-history event and undoable.
/// </summary>
public sealed class DiaryCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public async Task<IReadOnlyList<DiaryEntryRecord>> ListAsync(string taskId, bool includeDeleted, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            _ = await session.Tasks.GetIncludingDeletedAsync(taskId, token) ?? throw AppException.NotFound("No task with that ID exists on this computer.");
            return await session.Diary.ListByTaskAsync(taskId, includeDeleted, token);
        }, ct);

    public async Task<IReadOnlyList<DiaryRevisionRecord>> RevisionsAsync(string entryId, CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) =>
        {
            _ = await session.Diary.GetIncludingDeletedAsync(entryId, token) ?? throw AppException.NotFound("That diary entry does not exist.");
            return await session.Diary.ListRevisionsAsync(entryId, token);
        }, ct);

    /// <summary>Active diary entries and history events of one task, oldest first (diary before an event at the same instant).</summary>
    public async Task<IReadOnlyList<TimelineItem>> TimelineAsync(string taskId, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<TimelineItem>>(async (session, token) =>
        {
            _ = await session.Tasks.GetIncludingDeletedAsync(taskId, token) ?? throw AppException.NotFound("No task with that ID exists on this computer.");
            var diary = await session.Diary.ListByTaskAsync(taskId, includeDeleted: false, token);
            var history = await session.History.ListByTaskAsync(taskId, token);
            return [.. diary.Select((entry, index) => (Item: new TimelineItem(entry.EntryAtUtc, entry, null), Rank: 0, Index: index))
                .Concat(history.Select((entry, index) => (Item: new TimelineItem(entry.EffectiveAtUtc, null, entry), Rank: 1, Index: index)))
                .OrderBy(item => item.Item.AtUtc).ThenBy(item => item.Rank).ThenBy(item => item.Index)
                .Select(item => item.Item)];
        }, ct);

    public async Task<DiaryMutationResult> AddAsync(AddDiaryEntryCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = command with { Markdown = Markdown(command.Markdown) };
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "diary.add", normalized, now, async inner =>
            {
                var task = await session.Tasks.GetAsync(normalized.TaskId, inner)
                    ?? throw AppException.NotFound("That task no longer exists. Refresh and try again.");
                GitHubIssueLinkRecord? github = null;
                if (normalized.PostToGitHub)
                {
                    github = await session.GitHub.FindLinkByTaskAsync(task.Id, inner);
                    if (github is not { State: 0 }) throw AppException.Validation("postToGitHub", "Connect this task to an active GitHub issue before posting.");
                    if (normalized.Markdown.Length > 65450) throw AppException.Validation("markdown", "A GitHub comment can hold at most 65,450 characters.");
                }
                var entry = new DiaryEntryRecord
                {
                    Id = ids.NewId().ToString(),
                    TaskId = task.Id,
                    AuthorProfileId = profile.Id,
                    EntryAtUtc = normalized.EntryAtUtc?.ToUniversalTime() ?? now,
                    RawMarkdown = normalized.Markdown,
                    Revision = 1,
                    Version = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await session.Diary.AddAsync(entry, inner);
                if (github is not null) await session.GitHub.UpsertCommentLinkAsync(new() { DiaryEntryId = entry.Id, IssueLinkId = github.Id, Origin = 1, State = 1 }, inner);
                await RegisterAsync(session, task.Id, "diary.added", inner);
                return new DiaryMutationResult(entry, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    public async Task<DiaryMutationResult> EditAsync(EditDiaryEntryCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var normalized = command with { Markdown = Markdown(command.Markdown) };
        return await ChangeAsync(normalized.OperationId, "diary.edit", normalized, normalized.EntryId, normalized.ExpectedVersion, "diary.edited",
            (entry, now) => entry.RawMarkdown == normalized.Markdown ? null : entry with { RawMarkdown = normalized.Markdown, EditedAtUtc = now }, requireActive: true, ct);
    }

    public async Task<DiaryMutationResult> DeleteAsync(DiaryEntryChangeCommand command, CancellationToken ct = default) =>
        await ChangeAsync(command.OperationId, "diary.delete", command, command.EntryId, command.ExpectedVersion, "diary.deleted",
            (entry, now) => entry.DeletedAtUtc is not null ? null : entry with { DeletedAtUtc = now }, requireActive: false, ct, command.DeleteOnGitHub);

    public async Task<DiaryMutationResult> RestoreAsync(DiaryEntryChangeCommand command, CancellationToken ct = default) =>
        await ChangeAsync(command.OperationId, "diary.restore", command, command.EntryId, command.ExpectedVersion, "diary.restored",
            (entry, _) => entry.DeletedAtUtc is null ? null : entry with { DeletedAtUtc = null }, requireActive: false, ct);

    private async Task<DiaryMutationResult> ChangeAsync(
        string operationId,
        string commandKind,
        object payload,
        string entryId,
        long expectedVersion,
        string eventKind,
        Func<DiaryEntryRecord, DateTimeOffset, DiaryEntryRecord?> change,
        bool requireActive,
        CancellationToken ct,
        bool deleteOnGitHub = false)
    {
        ct.ThrowIfCancellationRequested();
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (result, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, operationId, commandKind, payload, now, async inner =>
            {
                var entry = await session.Diary.GetIncludingDeletedAsync(entryId, inner)
                    ?? throw AppException.NotFound("That diary entry no longer exists. Refresh and try again.");
                if (requireActive && entry.DeletedAtUtc is not null)
                {
                    throw AppException.Validation("entryId", "Restore this diary entry before editing it.");
                }

                if (entry.Version != expectedVersion)
                {
                    throw AppException.VersionConflict("This diary entry changed elsewhere while you were working. Refresh and try again.");
                }

                _ = await session.Tasks.GetAsync(entry.TaskId, inner)
                    ?? throw AppException.Validation("taskId", "Restore the task before changing its diary.");
                var link = await session.GitHub.FindLinkByTaskAsync(entry.TaskId, inner);
                var mapping = link is null or { State: 4 } ? null : (await session.GitHub.ListCommentLinksAsync(link.Id, inner)).FirstOrDefault(m => m.DiaryEntryId == entry.Id);
                if (deleteOnGitHub && (mapping is null || mapping.CommentId is null)) throw AppException.Validation("entryId", "This diary entry has no GitHub comment to delete.");
                if (mapping is not null && (commandKind == "diary.edit" || deleteOnGitHub))
                {
                    var connection = await session.GitHub.GetConnectionAsync(link!.ConnectionId, inner);
                    if (connection is null || mapping.Origin != 1 && !string.Equals(mapping.AuthorLogin, connection.AccountLogin, StringComparison.OrdinalIgnoreCase))
                        throw AppException.Validation("entryId", "Only the GitHub comment author can change it on GitHub.");
                }
                if (change(entry, now) is not { } updated)
                {
                    return new DiaryMutationResult(entry, Replayed: false); // nothing to change
                }

                var applied = await session.Diary.UpdateAsync(
                    updated with { Revision = entry.Revision + 1, Version = entry.Version + 1, UpdatedAtUtc = now },
                    entry.Version, inner)
                    ?? throw AppException.VersionConflict("This diary entry changed elsewhere while you were working. Refresh and try again.");
                if (commandKind == "diary.delete" && mapping is not null)
                {
                    var current = (await session.GitHub.ListCommentLinksAsync(link!.Id, inner)).First(m => m.DiaryEntryId == entry.Id);
                    await session.GitHub.UpsertCommentLinkAsync(current with { State = deleteOnGitHub && current.CommentId is not null ? 3 : 5 }, inner);
                }
                await RegisterAsync(session, entry.TaskId, eventKind, inner);
                return new DiaryMutationResult(applied, Replayed: false);
            }, token);
            return replayed ? result with { Replayed = true } : result;
        }, ct);
    }

    private async Task RegisterAsync(IDataSession session, string taskId, string eventKind, CancellationToken token)
    {
        var task = await session.Tasks.GetIncludingDeletedAsync(taskId, token);
        if (task is not null)
        {
            await TaskMutationSupport.WriteTaskHistoryAsync(session, ids, string.Empty, null, new TaskAggregateSnapshot(task, []), eventKind, ["Diary"], clock.GetUtcNow(), token);
        }
    }

    private static string Markdown(string? raw)
    {
        var text = TaskFieldValidation.Notes(raw, "markdown");
        return string.IsNullOrWhiteSpace(text)
            ? throw AppException.Validation("markdown", "Write something in the diary entry first.")
            : text;
    }

    private async Task<T> InWriteAsync<T>(Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<T>> body, CancellationToken ct) =>
        await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, body, ct);
}
