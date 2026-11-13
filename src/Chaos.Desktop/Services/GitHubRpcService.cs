using Chaos.Desktop.Platform;
using System.Text.Json;
using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using NeoAstra.Rpc;
using Chaos.Infrastructure.GitHub;

namespace Chaos.Desktop.Services;

public sealed record GitHubStatusRequest();
public sealed record GitHubTaskSyncRequest(string TaskId);
public sealed record GitHubAttachmentRequest(string TaskId, string Url);
public sealed record GitHubAttachmentResponse(bool Ok, string? ContentType, string? Base64, string? IssueUrl, TaskErrorResponse? Error);
public sealed record GitHubSelfAssignmentRequest(string OperationId, string TaskId, bool Assigned);
public sealed record GitHubTaskActionRequest(string OperationId, string TaskId, string ExpectedVersion);
public sealed record GitHubTaskSyncResponse(bool Ok, bool Connected, bool Pending, string? State, TaskErrorResponse? Error, bool? AssignedToMe = null, bool PostDiaryByDefault = false,
    string? LinkVersion = null, bool HasConflict = false, string? SyncError = null, string? IssueUrl = null, string? Repository = null, bool DueDateManaged = false, bool Syncing = false);
public sealed record GitHubConnectRequest(string OperationId);
public sealed record GitHubCandidatesRequest(string? Cursor);
public sealed record GitHubLookupRequest(string Owner, string Name);
public sealed record GitHubChangeRequest(string OperationId, string Id, string ExpectedVersion, bool Paused);
public sealed record GitHubSettingsRequest(string OperationId, string Id, string ExpectedVersion, bool PostDiaryByDefault, bool CreateLabelsForTags);
public sealed record GitHubFilterDto(IReadOnlyList<string> Labels, string LabelMode, string Assignee, string? Milestone, bool NoMilestone, int IncludeClosedDays);
public sealed record GitHubAddRequest(string OperationId, string ConnectionId, string ExpectedVersion, string Owner, string Name, string RepositoryId, string TargetContainerId, string? TargetSectionId, GitHubFilterDto Filter, bool MilestonesAsProjects);
public sealed record GitHubUpdateRequest(string OperationId, string Id, string ExpectedVersion, string TargetContainerId, string? TargetSectionId, GitHubFilterDto Filter, bool MilestonesAsProjects);
public sealed record GitHubConnectionDto(string Id, string Version, string AccountId, string AccountLogin, string State, bool PostDiaryByDefault, bool CreateLabelsForTags);
public sealed record GitHubRepositoryDto(string Id, string Version, string RepositoryId, string FullName, string State, string TargetContainerId, string? TargetSectionId, GitHubFilterDto Filter, bool MilestonesAsProjects);
public sealed record GitHubCandidateDto(string RepositoryId, string Owner, string Name, string FullName, bool IsPrivate, bool HasIssues, bool AlreadyAdded);
public sealed record GitHubSectionDto(string Id, string Title);
public sealed record GitHubTargetDto(string Id, string Name, IReadOnlyList<GitHubSectionDto> Sections);
public sealed record GitHubStatusResponse(bool Ok, GitHubConnectionDto? Connection, IReadOnlyList<GitHubRepositoryDto> Repositories, IReadOnlyList<GitHubTargetDto> Targets, TaskErrorResponse? Error, GitHubSyncDto? Sync = null);
public sealed record GitHubOperationResponse(bool Ok, TaskErrorResponse? Error);
public sealed record GitHubCandidatesResponse(bool Ok, IReadOnlyList<GitHubCandidateDto> Repositories, string? NextCursor, TaskErrorResponse? Error);

public sealed record GitHubSyncRequest(string? RepositoryId);
public sealed record GitHubSyncDto(bool Running, string? StartedAtUtc, string? FinishedAtUtc, int? Created, int? Updated, int? CommentChanges, int? Conflicts, bool MorePages, TaskErrorResponse? Error);
public sealed record GitHubSyncResponse(bool Ok, GitHubSyncDto? Sync, TaskErrorResponse? Error);

[NeoRpcService("github", Version = 1)]
public sealed class GitHubRpcService(GitHubConnectionCommands commands, IApplicationStore store, GitHubSyncEngine? sync = null, GitHubCliClient? attachments = null, GitHubSyncScheduler? scheduler = null)
{
    [NeoRpcMethod("status")]
    public async ValueTask<GitHubStatusResponse> StatusAsync(GitHubStatusRequest request, CancellationToken ct)
    {
        try
        {
            var status = await commands.StatusAsync(ct);
            var targets = await store.QueryAsync(async (s, token) =>
            {
                var profile = await s.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
                var space = await s.Spaces.GetPersonalByOwnerAsync(profile.Id, token) ?? throw AppException.NotFound("Personal space is unavailable.");
                var result = new List<GitHubTargetDto>();
                foreach (var container in (await s.Containers.ListBySpaceAsync(space.Id, token)).Where(c => c.ArchivedAtUtc is null))
                    result.Add(new(container.Id, container.Name, (await s.Sections.ListByContainerAsync(container.Id, token)).Select(section => new GitHubSectionDto(section.Id, section.Title)).ToArray()));
                return result;
            }, ct);
            return new(true, status.Connection is { } c ? new(c.Id, Number(c.Version), Number(c.AccountId), c.AccountLogin, c.State switch { 0 => "Active", 1 => "Paused", _ => "NeedsSignIn" }, c.PostDiaryByDefault, c.CreateLabelsForTags) : null,
                status.Repositories.Select(r => new GitHubRepositoryDto(r.Id, Number(r.Version), Number(r.RepositoryId), r.FullName, r.State switch { 0 => "Active", 1 => "Paused", _ => "Unavailable" }, r.TargetContainerId, r.TargetSectionId, ReadFilter(r.FilterJson), r.MilestonesAsProjects)).ToArray(), targets, null, SyncStatus(status.Connection));
        }
        catch (AppException e) { return new(false, null, [], [], TaskRpcService.ToErrorResponse(e)); }
    }

    [NeoRpcMethod("taskSync")]
    public async ValueTask<GitHubTaskSyncResponse> TaskSyncAsync(GitHubTaskSyncRequest request, CancellationToken ct)
    {
        try
        {
            return await store.QueryAsync(async (s, token) =>
            {
                var profile = await s.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
                var space = await s.Spaces.GetPersonalByOwnerAsync(profile.Id, token) ?? throw AppException.NotFound("Personal space is unavailable.");
                var task = await s.Tasks.GetIncludingDeletedAsync(request.TaskId, token);
                if (task is null || task.SpaceId != space.Id) throw AppException.NotFound("Task was not found.");
                var link = await s.GitHub.FindLinkByTaskAsync(task.Id, token);
                if (link is null || link.State == 4) return new GitHubTaskSyncResponse(true, false, false, null, null);
                var c = await s.GitHub.GetConnectionAsync(link.ConnectionId, token);
                if (c is null || c.DeletedAtUtc is not null || c.ProfileId != profile.Id || c.SpaceId != space.Id) return new(true, false, false, null, null);
                var repo = await s.GitHub.GetRepositoryAsync(link.RepositorySubscriptionId, token);
                var state = c.State == 1 || repo?.State == 1 ? "Paused" : c.State == 2 ? "Needs sign-in" : link.State switch { 0 when link.LastErrorKind is not null => "Needs attention", 0 => "Linked", 1 => "Out of scope", 2 => "Unavailable", _ => "Ignored" };
                var baseline = StoredJsonFormats.ReadSnapshot<GitHubIssueSnapshot>(link.BaseJson);
                var assigned = link.LocalAssigneeIntent is { } intent ? intent == 1 : baseline?.AssigneeIds.Contains(c.AccountId);
                var error = link.LastErrorKind is { } kind && Enum.IsDefined((GitHubErrorKind)kind) ? Error(new((GitHubErrorKind)kind)).Message : null;
                var activity = sync?.TaskStatus(task.Id);
                if (link.LocalDirty && error is null && activity?.GitHubError is { } failure) error = Error(new(failure)).Message;
                if (link.LocalDirty && error is null && activity?.AppError is { } appError) error = TaskRpcService.ToErrorResponse(appError).Message;
                return new(true, true, link.LocalDirty, state, null, assigned, c.PostDiaryByDefault, Number(link.Version), link.ConflictNoticeUtc is not null,
                    error, GitHubIssueAddress.TryParse(link.HtmlUrl, out var address) ? address.WebUrl : null, repo?.FullName, baseline?.MilestoneDue is not null, activity?.Running == true);
            }, ct);
        }
        catch (AppException e) { return new(false, false, false, null, TaskRpcService.ToErrorResponse(e)); }
    }

    [NeoRpcMethod("attachment")]
    public async ValueTask<GitHubAttachmentResponse> AttachmentAsync(GitHubAttachmentRequest request, CancellationToken ct)
    {
        string? issueUrl = null;
        try
        {
            if (attachments is null) throw new AppException(AppErrorKind.UnsupportedCapability, "GitHub attachments are unavailable.");
            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url) || request.Url.Length > 512)
                throw AppException.Validation("url", "Invalid attachment URL.");
            var context = await store.QueryAsync(async (s, token) =>
            {
                var profile = await s.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
                var space = await s.Spaces.GetPersonalByOwnerAsync(profile.Id, token) ?? throw AppException.NotFound("Personal space is unavailable.");
                var task = await s.Tasks.GetIncludingDeletedAsync(request.TaskId, token);
                if (task is null || task.DeletedAtUtc is not null || task.SpaceId != space.Id || !task.Notes.Contains(request.Url, StringComparison.Ordinal))
                    throw AppException.NotFound("Attachment was not found in this task.");
                var link = await s.GitHub.FindLinkByTaskAsync(task.Id, token);
                if (link is null || link.State == 4) throw AppException.NotFound("Task is not connected to GitHub.");
                var connection = await s.GitHub.GetConnectionAsync(link.ConnectionId, token);
                if (connection is null || connection.DeletedAtUtc is not null || connection.ProfileId != profile.Id || connection.SpaceId != space.Id)
                    throw AppException.NotFound("GitHub connection is unavailable.");
                return (Account: new GitHubAccount(connection.AccountId, connection.AccountLogin), IssueUrl: GitHubIssueAddress.TryParse(link.HtmlUrl, out var address) ? address.WebUrl : null);
            }, ct);
            issueUrl = context.IssueUrl;
            var image = await attachments.ReadAttachmentAsync(context.Account, url, ct);
            return new(true, image.ContentType, Convert.ToBase64String(image.Data), issueUrl, null);
        }
        catch (AppException e) { return new(false, null, null, issueUrl, TaskRpcService.ToErrorResponse(e)); }
        catch (GitHubApiException e) { return new(false, null, null, issueUrl, Error(e)); }
    }

    [NeoRpcMethod("syncNow")]
    public async ValueTask<GitHubSyncResponse> SyncNowAsync(GitHubSyncRequest request, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (sync is null) throw new AppException(AppErrorKind.UnsupportedCapability, "GitHub sync is not configured in this host.");
            var connection = (await commands.StatusAsync(ct)).Connection ?? throw AppException.NotFound("Connect to GitHub first.");
            var activity = sync.RequestSync(string.IsNullOrWhiteSpace(request.RepositoryId) ? null : request.RepositoryId, connection.Id);
            return new(true, MapSync(activity), null);
        }
        catch (AppException e) { return new(false, null, TaskRpcService.ToErrorResponse(e)); }
    }

    private GitHubSyncDto? SyncStatus(GitHubConnectionRecord? connection)
    {
        if (sync is null || connection is null) return null;
        var activity = sync.Status;
        if (activity.ConnectionId == connection.Id && activity.StartedAtUtc is not null) return MapSync(activity);
        var error = connection.LastErrorKind is { } kind && Enum.IsDefined((GitHubErrorKind)kind) ? Error(new((GitHubErrorKind)kind)) : null;
        return new(false, Instant(connection.LastSyncStartedUtc), Instant(connection.LastSyncCompletedUtc), null, null, null, null, false, error);
    }
    private static GitHubSyncDto MapSync(GitHubSyncActivity state) => new(state.Running, Instant(state.StartedAtUtc), Instant(state.FinishedAtUtc),
        state.Result?.Created, state.Result?.Updated, state.Result?.CommentChanges, state.Result?.Conflicts, state.Result?.MorePages ?? false,
        state.AppError is { } app ? TaskRpcService.ToErrorResponse(app) : state.GitHubError is { } kind ? Error(new(kind)) : null);
    private static string? Instant(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    [NeoRpcMethod("connect")]
    public async ValueTask<GitHubOperationResponse> ConnectAsync(GitHubConnectRequest request, CancellationToken ct) => await Run(async () => { await commands.ConnectAsync(new(request.OperationId), ct); scheduler?.Wake(); });
    [NeoRpcMethod("listRepositories")]
    public async ValueTask<GitHubCandidatesResponse> ListRepositoriesAsync(GitHubCandidatesRequest request, CancellationToken ct)
    {
        try
        {
            var result = await commands.ListReachableRepositoriesAsync(request.Cursor, ct);
            return new(true, result.Repositories.Select(c => Candidate(c.Repository, c.AlreadyAdded)).ToArray(), result.NextCursor, null);
        }
        catch (AppException e) { return new(false, [], null, TaskRpcService.ToErrorResponse(e)); }
        catch (GitHubApiException e) { return new(false, [], null, Error(e)); }
    }
    [NeoRpcMethod("lookupRepository")]
    public async ValueTask<GitHubCandidatesResponse> LookupRepositoryAsync(GitHubLookupRequest request, CancellationToken ct)
    {
        try { var result = await commands.LookupRepositoryAsync(request.Owner, request.Name, ct); return new(true, [Candidate(result.Repository, result.AlreadyAdded)], null, null); }
        catch (AppException e) { return new(false, [], null, TaskRpcService.ToErrorResponse(e)); }
        catch (GitHubApiException e) { return new(false, [], null, Error(e)); }
    }
    [NeoRpcMethod("addRepository")]
    public async ValueTask<GitHubOperationResponse> AddRepositoryAsync(GitHubAddRequest r, CancellationToken ct) => await Run(async () => { await commands.AddRepositoryAsync(new(r.OperationId, r.ConnectionId, Positive(r.ExpectedVersion), r.Owner, r.Name, Positive(r.RepositoryId), r.TargetContainerId, r.TargetSectionId, Filter(r.Filter), r.MilestonesAsProjects), ct); scheduler?.Wake(); });
    [NeoRpcMethod("updateRepository")]
    public async ValueTask<GitHubOperationResponse> UpdateRepositoryAsync(GitHubUpdateRequest r, CancellationToken ct) => await Run(async () => { await commands.UpdateRepositoryAsync(new(r.OperationId, r.Id, Positive(r.ExpectedVersion), r.TargetContainerId, r.TargetSectionId, Filter(r.Filter), r.MilestonesAsProjects), ct); scheduler?.Wake(); });
    [NeoRpcMethod("pauseConnection")]
    public async ValueTask<GitHubOperationResponse> PauseConnectionAsync(GitHubChangeRequest r, CancellationToken ct) => await Run(async () => { await Quiesce(async () => { await commands.SetConnectionPausedAsync(Change(r), ct); }, ct); });
    [NeoRpcMethod("pauseRepository")]
    public async ValueTask<GitHubOperationResponse> PauseRepositoryAsync(GitHubChangeRequest r, CancellationToken ct) => await Run(async () => { await Quiesce(async () => { await commands.SetRepositoryPausedAsync(Change(r), ct); }, ct); });
    [NeoRpcMethod("updateSettings")]
    public async ValueTask<GitHubOperationResponse> UpdateSettingsAsync(GitHubSettingsRequest r, CancellationToken ct) => await Run(async () => { await commands.UpdateSettingsAsync(new(r.OperationId, r.Id, Positive(r.ExpectedVersion), r.PostDiaryByDefault, r.CreateLabelsForTags), ct); });
    [NeoRpcMethod("setSelfAssignment")]
    public async ValueTask<GitHubOperationResponse> SetSelfAssignmentAsync(GitHubSelfAssignmentRequest r, CancellationToken ct) => await Run(async () => { await commands.SetSelfAssignmentAsync(new(r.OperationId, r.TaskId, r.Assigned), ct); sync?.RequestTaskSync(r.TaskId); });
    [NeoRpcMethod("retryTask")]
    public async ValueTask<GitHubOperationResponse> RetryTaskAsync(GitHubTaskActionRequest r, CancellationToken ct) => await Run(async () => { await commands.RetryTaskAsync(new(r.OperationId, r.TaskId, Positive(r.ExpectedVersion)), ct); sync?.RequestTaskSync(r.TaskId, immediate: true); });
    [NeoRpcMethod("dismissConflict")]
    public async ValueTask<GitHubOperationResponse> DismissConflictAsync(GitHubTaskActionRequest r, CancellationToken ct) => await Run(async () => { await commands.DismissConflictAsync(new(r.OperationId, r.TaskId, Positive(r.ExpectedVersion)), ct); });
    [NeoRpcMethod("keepAsOrdinaryTask")]
    public async ValueTask<GitHubOperationResponse> KeepAsOrdinaryTaskAsync(GitHubTaskActionRequest r, CancellationToken ct) => await Run(async () => { await Quiesce(async () => { await commands.KeepAsOrdinaryTaskAsync(new(r.OperationId, r.TaskId, Positive(r.ExpectedVersion)), ct); }, ct); });
    [NeoRpcMethod("removeRepository")]
    public async ValueTask<GitHubOperationResponse> RemoveRepositoryAsync(GitHubChangeRequest r, CancellationToken ct) => await Run(async () => { await Quiesce(() => commands.RemoveRepositoryAsync(Change(r), ct), ct); });
    [NeoRpcMethod("disconnect")]
    public async ValueTask<GitHubOperationResponse> DisconnectAsync(GitHubChangeRequest r, CancellationToken ct) => await Run(async () => { await Quiesce(() => commands.DisconnectAsync(Change(r), ct), ct); });

    private async Task Quiesce(Func<Task> action, CancellationToken ct)
    {
        try { if (sync is null) await action(); else await sync.RunPausedAsync(action, ct); }
        finally { scheduler?.Wake(); }
    }

    private static GitHubFilterDto ReadFilter(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var milestone = root.TryGetProperty("milestone", out var m) ? m : default;
        return new(root.TryGetProperty("labels", out var labels) ? labels.EnumerateArray().Select(l => l.GetString()!).ToArray() : [],
            root.TryGetProperty("labelMode", out var mode) ? mode.GetString()! : "any",
            root.TryGetProperty("assignee", out var assignee) ? assignee.GetString()! : "any",
            milestone.ValueKind == JsonValueKind.Number ? Number(milestone.GetInt64()) : null,
            milestone.ValueKind == JsonValueKind.String && milestone.GetString() == "none",
            root.TryGetProperty("includeClosedDays", out var days) ? days.GetInt32() : 0);
    }
    private static GitHubCandidateDto Candidate(GitHubRepository r, bool added) => new(Number(r.Id), r.Owner.Login, r.Name, r.FullName, r.Private, r.HasIssues, added);
    private static ChangeGitHubCommand Change(GitHubChangeRequest r) => new(r.OperationId, r.Id, Positive(r.ExpectedVersion), r.Paused);
    private static GitHubFilter Filter(GitHubFilterDto f) => new(f.Labels, f.LabelMode, f.Assignee, f.Milestone is null ? null : Positive(f.Milestone), f.NoMilestone, f.IncludeClosedDays);
    private static long Positive(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result > 0 ? result : throw AppException.Validation("id", "Use a positive numeric ID or version.");
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static async ValueTask<GitHubOperationResponse> Run(Func<Task> action)
    {
        try { await action(); return new(true, null); }
        catch (AppException e) { return new(false, TaskRpcService.ToErrorResponse(e)); }
        catch (GitHubApiException e) { return new(false, Error(e)); }
    }
    private static TaskErrorResponse Error(GitHubApiException e) => new("github" + e.Kind, e.Kind switch
    {
        GitHubErrorKind.CliUnavailable => "Install GitHub CLI 2.103.3 or newer in Program Files, then sign in with gh auth login.",
        GitHubErrorKind.Unauthorized => "Sign in or refresh your login outside Chaos using GitHub CLI, then connect again.",
        GitHubErrorKind.AccountMismatch => "The GitHub CLI credential does not match the connected account. Sign in to that account again.",
        GitHubErrorKind.Forbidden => "GitHub denied access. Check repository permissions and organization authorization.",
        GitHubErrorKind.NotFound => "GitHub could not find that repository or your account cannot access it.",
        GitHubErrorKind.RateLimited => "GitHub's request limit was reached. Try again after the limit resets.",
        GitHubErrorKind.Network => "GitHub could not be reached. Check your connection and try again.",
        _ => "GitHub could not complete the request. Reload and try again.",
    }, [], Guid.NewGuid().ToString("N"));
}
