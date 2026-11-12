using System.Text.Json;
using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.GitHub;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record GitHubStatusRequest();
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
public sealed record GitHubStatusResponse(bool Ok, GitHubConnectionDto? Connection, IReadOnlyList<GitHubRepositoryDto> Repositories, IReadOnlyList<GitHubTargetDto> Targets, TaskErrorResponse? Error);
public sealed record GitHubOperationResponse(bool Ok, TaskErrorResponse? Error);
public sealed record GitHubCandidatesResponse(bool Ok, IReadOnlyList<GitHubCandidateDto> Repositories, string? NextCursor, TaskErrorResponse? Error);

[NeoRpcService("github", Version = 1)]
public sealed class GitHubRpcService(GitHubConnectionCommands commands, IApplicationStore store)
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
                status.Repositories.Select(r => new GitHubRepositoryDto(r.Id, Number(r.Version), Number(r.RepositoryId), r.FullName, r.State switch { 0 => "Active", 1 => "Paused", _ => "Unavailable" }, r.TargetContainerId, r.TargetSectionId, ReadFilter(r.FilterJson), r.MilestonesAsProjects)).ToArray(), targets, null);
        }
        catch (AppException e) { return new(false, null, [], [], TaskRpcService.ToErrorResponse(e)); }
    }

    [NeoRpcMethod("connect")]
    public async ValueTask<GitHubOperationResponse> ConnectAsync(GitHubConnectRequest request, CancellationToken ct) => await Run(async () => { await commands.ConnectAsync(new(request.OperationId), ct); });
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
    public async ValueTask<GitHubOperationResponse> AddRepositoryAsync(GitHubAddRequest r, CancellationToken ct) => await Run(async () => { await commands.AddRepositoryAsync(new(r.OperationId, r.ConnectionId, Positive(r.ExpectedVersion), r.Owner, r.Name, Positive(r.RepositoryId), r.TargetContainerId, r.TargetSectionId, Filter(r.Filter), r.MilestonesAsProjects), ct); });
    [NeoRpcMethod("updateRepository")]
    public async ValueTask<GitHubOperationResponse> UpdateRepositoryAsync(GitHubUpdateRequest r, CancellationToken ct) => await Run(async () => { await commands.UpdateRepositoryAsync(new(r.OperationId, r.Id, Positive(r.ExpectedVersion), r.TargetContainerId, r.TargetSectionId, Filter(r.Filter), r.MilestonesAsProjects), ct); });
    [NeoRpcMethod("pauseConnection")]
    public async ValueTask<GitHubOperationResponse> PauseConnectionAsync(GitHubChangeRequest r, CancellationToken ct) => await Run(async () => { await commands.SetConnectionPausedAsync(Change(r), ct); });
    [NeoRpcMethod("pauseRepository")]
    public async ValueTask<GitHubOperationResponse> PauseRepositoryAsync(GitHubChangeRequest r, CancellationToken ct) => await Run(async () => { await commands.SetRepositoryPausedAsync(Change(r), ct); });
    [NeoRpcMethod("updateSettings")]
    public async ValueTask<GitHubOperationResponse> UpdateSettingsAsync(GitHubSettingsRequest r, CancellationToken ct) => await Run(async () => { await commands.UpdateSettingsAsync(new(r.OperationId, r.Id, Positive(r.ExpectedVersion), r.PostDiaryByDefault, r.CreateLabelsForTags), ct); });
    [NeoRpcMethod("removeRepository")]
    public async ValueTask<GitHubOperationResponse> RemoveRepositoryAsync(GitHubChangeRequest r, CancellationToken ct) => await Run(async () => { await commands.RemoveRepositoryAsync(Change(r), ct); });
    [NeoRpcMethod("disconnect")]
    public async ValueTask<GitHubOperationResponse> DisconnectAsync(GitHubChangeRequest r, CancellationToken ct) => await Run(async () => { await commands.DisconnectAsync(Change(r), ct); });

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
