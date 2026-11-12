using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Tasks;
using NeoAstra.Rpc;
using Chaos.Desktop.Platform;

namespace Chaos.Desktop.Services;

public sealed record HistoryTaskRequest(string TaskId);
public sealed record HistoryEntryResponse(string Id, string OperationId, string EventKind, IReadOnlyList<string> ChangedFields, string EffectiveAtUtc, string Version);
public sealed record HistoryListResponse(bool Ok, IReadOnlyList<HistoryEntryResponse> Entries, TaskErrorResponse? Error);
public sealed record HistoryAsOfRequest(string TaskId, string? AtUtc, string? EntryId);
public sealed record SnapshotSubtaskResponse(string Id, string Title, string Notes, string Status, IReadOnlyList<string> TagIds);
public sealed record TaskSnapshotResponse(TaskResponse Task, bool Deleted, IReadOnlyList<SnapshotSubtaskResponse> Subtasks, IReadOnlyList<string> TagIds);
public sealed record HistoryAsOfResponse(bool Ok, bool Existed, TaskSnapshotResponse? Snapshot, HistoryEntryResponse? Entry, TaskErrorResponse? Error);
public sealed record RestoreFieldsRequest(string OperationId, string TaskId, string ExpectedVersion, string EntryId, IReadOnlyList<string> Fields);

public sealed record DiaryEntryResponse(string Id, string TaskId, string EntryAtUtc, string Markdown, string Revision, string? EditedAtUtc, string Version, bool Deleted);
public sealed record DiaryListRequest(string TaskId, bool IncludeDeleted);
public sealed record DiaryListResponse(bool Ok, IReadOnlyList<DiaryEntryResponse> Entries, TaskErrorResponse? Error);
public sealed record TimelineItemResponse(string AtUtc, DiaryEntryResponse? Diary, HistoryEntryResponse? Event);
public sealed record TimelineResponse(bool Ok, IReadOnlyList<TimelineItemResponse> Items, TaskErrorResponse? Error);
public sealed record DiaryAddRequest(string OperationId, string TaskId, string Markdown, string? EntryAtUtc, bool PostToGitHub = false);
public sealed record DiaryEditRequest(string OperationId, string EntryId, string ExpectedVersion, string Markdown);
public sealed record DiaryChangeRequest(string OperationId, string EntryId, string ExpectedVersion, bool DeleteOnGitHub = false);
public sealed record DiaryOperationResponse(bool Ok, DiaryEntryResponse? Entry, TaskErrorResponse? Error);
public sealed record DiaryRevisionsRequest(string EntryId);
public sealed record DiaryRevisionResponse(string Revision, string Markdown, bool Deleted, string RecordedAtUtc);
public sealed record DiaryRevisionsResponse(bool Ok, IReadOnlyList<DiaryRevisionResponse> Revisions, TaskErrorResponse? Error);

public sealed record UndoListRequest(int Limit);
public sealed record UndoEntryResponse(string OperationId, string Label, string AppliedAtUtc);
public sealed record UndoListResponse(bool Ok, IReadOnlyList<UndoEntryResponse> Entries, TaskErrorResponse? Error);
public sealed record UndoRequest(string OperationId, string TargetOperationId);
public sealed record UndoResponse(bool Ok, string? Label, TaskErrorResponse? Error);

public sealed record ChecklistItemRequest(string ItemKey, string Title, bool Done);
public sealed record ChecklistConvertRequest(string OperationId, string TaskId, IReadOnlyList<ChecklistItemRequest> Items);
public sealed record ChecklistConvertResponse(bool Ok, IReadOnlyList<SubtaskResponse> Created, int AlreadyConverted, TaskErrorResponse? Error);

public sealed record LinkResolveRequest(IReadOnlyList<string> TaskIds);
public sealed record LinkTargetResponse(string Id, string Title, bool Deleted);
public sealed record LinkResolveResponse(bool Ok, IReadOnlyList<LinkTargetResponse> Targets, TaskErrorResponse? Error);

/// <summary>Task history viewer: the append-only feed, the state as of an instant or entry, restoring old values (PLAN §9).</summary>
[NeoRpcService("history", Version = 1)]
public sealed class HistoryRpcService(TaskHistoryCommands history, GitHubSyncEngine? sync = null)
{
    [NeoRpcMethod("list")]
    public async ValueTask<HistoryListResponse> ListAsync(HistoryTaskRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await history.ListAsync(request.TaskId, ct)).Select(HistoryRpc.Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("asOf")]
    public async ValueTask<HistoryAsOfResponse> AsOfAsync(HistoryAsOfRequest request, CancellationToken ct)
    {
        try
        {
            TaskAsOf result;
            if (!string.IsNullOrEmpty(request.EntryId))
            {
                result = await history.AtEntryAsync(request.TaskId, request.EntryId, ct);
            }
            else
            {
                var at = StorageFormats.ParseInstant(request.AtUtc ?? string.Empty)
                    ?? (DateTimeOffset.TryParse(request.AtUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : throw AppException.Validation("atUtc", "Choose a valid date and time."));
                result = await history.AsOfAsync(request.TaskId, at, ct);
            }

            return new(true, result.Existed, result.Snapshot is null ? null : HistoryRpc.Map(result.Snapshot), result.Entry is null ? null : HistoryRpc.Map(result.Entry), null);
        }
        catch (AppException error) { return new(false, false, null, null, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("restoreFields")]
    public async ValueTask<TaskOperationResponse> RestoreFieldsAsync(RestoreFieldsRequest request, CancellationToken ct)
    {
        try
        {
            var fields = request.Fields.Select(field => Enum.TryParse<RestorableTaskField>(field, ignoreCase: true, out var value) && Enum.IsDefined(value)
                ? value
                : throw AppException.Validation("fields", $"“{field}” cannot be restored from history.")).ToList();
            var result = await history.RestoreFieldsAsync(new(request.OperationId, request.TaskId, HistoryRpc.Version(request.ExpectedVersion), request.EntryId, fields), ct);
            sync?.RequestTaskSync(request.TaskId);
            return new(true, request.OperationId, TaskRpcService.ToResponse(result.Task), null, result.Warning);
        }
        catch (AppException error) { return new(false, request.OperationId, null, TaskRpcService.ToErrorResponse(error)); }
    }
}

/// <summary>Task diary: dated Markdown updates with revisions, plus the merged timeline (PLAN §9).</summary>
[NeoRpcService("diary", Version = 1)]
public sealed class DiaryRpcService(DiaryCommands diary, GitHubSyncEngine? sync = null)
{
    [NeoRpcMethod("list")]
    public async ValueTask<DiaryListResponse> ListAsync(DiaryListRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await diary.ListAsync(request.TaskId, request.IncludeDeleted, ct)).Select(HistoryRpc.Map)], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("timeline")]
    public async ValueTask<TimelineResponse> TimelineAsync(HistoryTaskRequest request, CancellationToken ct)
    {
        try
        {
            var items = await diary.TimelineAsync(request.TaskId, ct);
            return new(true, [.. items.Select(item => new TimelineItemResponse(StorageFormats.Instant(item.AtUtc), item.Diary is null ? null : HistoryRpc.Map(item.Diary), item.Event is null ? null : HistoryRpc.Map(item.Event)))], null);
        }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("revisions")]
    public async ValueTask<DiaryRevisionsResponse> RevisionsAsync(DiaryRevisionsRequest request, CancellationToken ct)
    {
        try
        {
            var rows = await diary.RevisionsAsync(request.EntryId, ct);
            return new(true, [.. rows.Select(row => new DiaryRevisionResponse(row.Revision.ToString(CultureInfo.InvariantCulture), row.RawMarkdown, row.Deleted, StorageFormats.Instant(row.RecordedAtUtc)))], null);
        }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("add")]
    public async ValueTask<DiaryOperationResponse> AddAsync(DiaryAddRequest request, CancellationToken ct) =>
        await RunAsync(() =>
        {
            DateTimeOffset? at = string.IsNullOrEmpty(request.EntryAtUtc) ? null : StorageFormats.ParseInstant(request.EntryAtUtc) ?? throw AppException.Validation("entryAtUtc", "Choose a valid entry time.");
            return diary.AddAsync(new(request.OperationId, request.TaskId, request.Markdown, at, request.PostToGitHub), ct);
        });

    [NeoRpcMethod("edit")]
    public async ValueTask<DiaryOperationResponse> EditAsync(DiaryEditRequest request, CancellationToken ct) =>
        await RunAsync(() => diary.EditAsync(new(request.OperationId, request.EntryId, HistoryRpc.Version(request.ExpectedVersion), request.Markdown), ct));

    [NeoRpcMethod("delete")]
    public async ValueTask<DiaryOperationResponse> DeleteAsync(DiaryChangeRequest request, CancellationToken ct) =>
        await RunAsync(() => diary.DeleteAsync(new(request.OperationId, request.EntryId, HistoryRpc.Version(request.ExpectedVersion), request.DeleteOnGitHub), ct));

    [NeoRpcMethod("restore")]
    public async ValueTask<DiaryOperationResponse> RestoreAsync(DiaryChangeRequest request, CancellationToken ct) =>
        await RunAsync(() => diary.RestoreAsync(new(request.OperationId, request.EntryId, HistoryRpc.Version(request.ExpectedVersion)), ct));

    private async ValueTask<DiaryOperationResponse> RunAsync(Func<Task<DiaryMutationResult>> action)
    {
        try { var result = await action(); sync?.RequestTaskSync(result.Entry.TaskId); return new(true, HistoryRpc.Map(result.Entry), null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }
}

/// <summary>Operation-level undo (PLAN §9): the newest undoable changes and undoing one of them.</summary>
[NeoRpcService("undo", Version = 1)]
public sealed class UndoRpcService(UndoCommands undo)
{
    [NeoRpcMethod("list")]
    public async ValueTask<UndoListResponse> ListAsync(UndoListRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await undo.ListAsync(request.Limit <= 0 ? 10 : request.Limit, ct)).Select(row => new UndoEntryResponse(row.OperationId, row.Label, StorageFormats.Instant(row.AppliedAtUtc)))], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("undo")]
    public async ValueTask<UndoResponse> UndoAsync(UndoRequest request, CancellationToken ct)
    {
        try { return new(true, (await undo.UndoAsync(new(request.OperationId, request.TargetOperationId), ct)).Label, null); }
        catch (AppException error) { return new(false, null, TaskRpcService.ToErrorResponse(error)); }
    }
}

public sealed record OpenExternalRequest(string Url);
public sealed record OpenExternalResponse(bool Ok, TaskErrorResponse? Error);

/// <summary>
/// Markdown editor services (PLAN §9): checklist-to-subtask conversion, internal link
/// resolution, and the host opener for external links. Rendered Markdown never navigates the
/// WebView; an external link is opened by the system browser only after a click and only
/// for absolute http, https or mailto URLs.
/// </summary>
[NeoRpcService("markdown", Version = 1)]
public sealed class MarkdownRpcService(ChecklistCommands checklists, TaskCommands tasks, Func<Uri, bool>? opener = null, GitHubSyncEngine? sync = null)
{
    [NeoRpcMethod("openExternal")]
    public ValueTask<OpenExternalResponse> OpenExternalAsync(OpenExternalRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto") || request.Url.Length > 2048)
        {
            return ValueTask.FromResult(new OpenExternalResponse(false, TaskRpcService.ToErrorResponse(AppException.Validation("url", "Only web (http, https) and email (mailto) links can be opened."))));
        }

        var opened = (opener ?? OpenWithShell)(uri);
        return ValueTask.FromResult(opened
            ? new OpenExternalResponse(true, null)
            : new OpenExternalResponse(false, TaskRpcService.ToErrorResponse(new AppException(AppErrorKind.UnsupportedCapability, "No program on this computer could open that link."))));
    }

    private static bool OpenWithShell(Uri uri)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [NeoRpcMethod("convertChecklist")]
    public async ValueTask<ChecklistConvertResponse> ConvertChecklistAsync(ChecklistConvertRequest request, CancellationToken ct)
    {
        try
        {
            var result = await checklists.ConvertAsync(new(request.OperationId, request.TaskId, [.. request.Items.Select(item => new ChecklistItem(item.ItemKey, item.Title, item.Done))]), ct);
            sync?.RequestTaskSync(request.TaskId);
            return new(true, [.. result.Created.Select(SubtaskRpcService.Map)], result.AlreadyConverted, null);
        }
        catch (AppException error) { return new(false, [], 0, TaskRpcService.ToErrorResponse(error)); }
    }

    [NeoRpcMethod("resolveLinks")]
    public async ValueTask<LinkResolveResponse> ResolveLinksAsync(LinkResolveRequest request, CancellationToken ct)
    {
        try { return new(true, [.. (await tasks.ResolveLinksAsync(request.TaskIds, ct)).Select(task => new LinkTargetResponse(task.Id, task.Title, task.DeletedAtUtc is not null))], null); }
        catch (AppException error) { return new(false, [], TaskRpcService.ToErrorResponse(error)); }
    }
}

internal static class HistoryRpc
{
    public static long Version(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0
            ? version
            : throw AppException.Validation("expectedVersion", "Invalid version. Refresh and try again.");

    public static HistoryEntryResponse Map(TaskHistoryRecord entry) => new(
        entry.Id, entry.OperationId, entry.EventKind, entry.ChangedFields,
        StorageFormats.Instant(entry.EffectiveAtUtc), entry.AggregateVersion.ToString(CultureInfo.InvariantCulture));

    public static DiaryEntryResponse Map(DiaryEntryRecord entry) => new(
        entry.Id, entry.TaskId, StorageFormats.Instant(entry.EntryAtUtc), entry.RawMarkdown,
        entry.Revision.ToString(CultureInfo.InvariantCulture),
        entry.EditedAtUtc is null ? null : StorageFormats.Instant(entry.EditedAtUtc.Value),
        entry.Version.ToString(CultureInfo.InvariantCulture), entry.DeletedAtUtc is not null);

    public static TaskSnapshotResponse Map(TaskAggregateSnapshot snapshot) => new(
        TaskRpcService.ToResponse(snapshot.Task),
        snapshot.Task.DeletedAtUtc is not null,
        [.. snapshot.Subtasks.Select(subtask => new SnapshotSubtaskResponse(
            subtask.Id, subtask.Title, subtask.Notes, subtask.Status.ToString(),
            snapshot.SubtaskTagIds?.GetValueOrDefault(subtask.Id) ?? []))],
        snapshot.TagIds ?? []);
}
