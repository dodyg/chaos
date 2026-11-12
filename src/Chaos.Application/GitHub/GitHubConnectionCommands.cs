using System.Text.Json;
using System.Text.RegularExpressions;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;

namespace Chaos.Application.GitHub;

public sealed record GitHubFilter(IReadOnlyList<string> Labels, string LabelMode = "any", string Assignee = "any", long? Milestone = null, bool NoMilestone = false, int IncludeClosedDays = 0)
{
    public static GitHubFilter Empty { get; } = new([]);
    public string ToJson()
    {
        if (Labels is null || Labels.Count > 100 || Labels.Any(label => string.IsNullOrWhiteSpace(label) || label.Length > 50 || label.Any(char.IsControl))
            || LabelMode is not ("any" or "all") || Assignee is not ("any" or "me" or "none")
            || Milestone <= 0 || (NoMilestone && Milestone is not null) || IncludeClosedDays is < 0 or > 365)
            throw AppException.Validation("filter", "Choose valid labels, assignment, milestone and 0–365 closed days.");
        return JsonSerializer.Serialize(new { v = 1, labels = Labels.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            labelMode = LabelMode, assignee = Assignee, milestone = NoMilestone ? (object)"none" : Milestone, includeClosedDays = IncludeClosedDays });
    }
}
public sealed record GitHubStatus(GitHubConnectionRecord? Connection, IReadOnlyList<GitHubRepositoryRecord> Repositories);
public sealed record GitHubRepositoryCandidate(GitHubRepository Repository, bool AlreadyAdded);
public sealed record GitHubRepositoryCandidates(IReadOnlyList<GitHubRepositoryCandidate> Repositories, string? NextCursor);
public sealed record ConnectGitHubCommand(string OperationId);
public sealed record AddGitHubRepositoryCommand(string OperationId, string ConnectionId, long ExpectedVersion, string Owner, string Name, long RepositoryId, string TargetContainerId, string? TargetSectionId, GitHubFilter Filter, bool MilestonesAsProjects);
public sealed record UpdateGitHubRepositoryCommand(string OperationId, string RepositoryId, long ExpectedVersion, string TargetContainerId, string? TargetSectionId, GitHubFilter Filter, bool MilestonesAsProjects);
public sealed record ChangeGitHubCommand(string OperationId, string Id, long ExpectedVersion, bool Paused = false);
public sealed record GitHubSettingsCommand(string OperationId, string Id, long ExpectedVersion, bool PostDiaryByDefault, bool CreateLabelsForTags);

/// <summary>Local configuration with explicit remote setup reads. No network call holds a database transaction.</summary>
public sealed partial class GitHubConnectionCommands(IApplicationStore store, IGitHubApi api, IIdGenerator ids, TimeProvider clock)
{
    public Task<GitHubStatus> StatusAsync(CancellationToken ct = default) => store.QueryAsync(async (s, token) =>
    {
        var (_, space) = await Context(s, token);
        var connection = await s.GitHub.GetConnectionForSpaceAsync(space.Id, token);
        return new GitHubStatus(connection, connection is null ? [] : await s.GitHub.ListSubscriptionsAsync(connection.Id, token));
    }, ct);

    public async Task<GitHubConnectionRecord> ConnectAsync(ConnectGitHubCommand command, CancellationToken ct = default)
    {
        ValidateOperation(command.OperationId);
        var replay = await Replay<GitHubConnectionRecord>(command.OperationId, "github.connect", command, ct);
        if (replay is not null) return replay;
        var account = await api.GetSetupAccountAsync(ct);
        if (account.Id <= 0 || !LoginPattern().IsMatch(account.Login)) throw new GitHubApiException(GitHubErrorKind.Server);
        return await Mutate(command.OperationId, "github.connect", command, async (s, profile, space, now, token) =>
        {
            var existing = await s.GitHub.GetConnectionForSpaceAsync(space.Id, token);
            if (existing is not null)
            {
                if (existing.AccountId != account.Id) throw AppException.Validation("account", "This space is connected to @" + existing.AccountLogin + ". Disconnect first.");
                return await s.GitHub.UpdateConnectionAsync(existing with { AccountLogin = account.Login, State = 0, LastErrorKind = null, LastErrorMessage = null, UpdatedAtUtc = now }, existing.Version, token)
                    ?? throw Conflict();
            }
            var row = new GitHubConnectionRecord { Id = ids.NewId().ToString(), SpaceId = space.Id, ProfileId = profile.Id,
                AccountId = account.Id, AccountLogin = account.Login, WebBaseUrl = "https://github.com", ApiBaseUrl = "https://api.github.com", CreatedAtUtc = now, UpdatedAtUtc = now };
            await s.GitHub.AddConnectionAsync(row, token);
            return row;
        }, ct);
    }

    public async Task<GitHubRepositoryCandidates> ListReachableRepositoriesAsync(string? cursor, CancellationToken ct = default)
    {
        var status = await StatusAsync(ct);
        var connection = status.Connection ?? throw AppException.NotFound("Connect to GitHub first.");
        var page = await api.ListRepositoriesAsync(Account(connection), cursor, null, ct);
        return new(page.Items.Select(repo => new GitHubRepositoryCandidate(repo, status.Repositories.Any(r => r.RepositoryId == repo.Id))).ToArray(), page.NextCursor);
    }

    public async Task<GitHubRepositoryCandidate> LookupRepositoryAsync(string owner, string name, CancellationToken ct = default)
    {
        if (!SegmentPattern().IsMatch(owner) || !SegmentPattern().IsMatch(name) || owner is "." or ".." || name is "." or "..")
            throw AppException.Validation("repository", "Enter owner/repository.");
        var status = await StatusAsync(ct);
        var connection = status.Connection ?? throw AppException.NotFound("Connect to GitHub first.");
        var repo = await api.GetRepositoryAsync(Account(connection), owner, name, ct);
        return new(repo, status.Repositories.Any(r => r.RepositoryId == repo.Id));
    }

    public async Task<GitHubRepositoryRecord> AddRepositoryAsync(AddGitHubRepositoryCommand command, CancellationToken ct = default)
    {
        ValidateOperation(command.OperationId);
        var filter = command.Filter.ToJson();
        if (!SegmentPattern().IsMatch(command.Owner) || !SegmentPattern().IsMatch(command.Name) || command.Owner is "." or ".." || command.Name is "." or ".." || command.RepositoryId <= 0)
            throw AppException.Validation("repository", "Enter a valid owner, repository and numeric repository ID.");
        var replay = await Replay<GitHubRepositoryRecord>(command.OperationId, "github.addRepository", command, ct);
        if (replay is not null) return replay;
        var connection = await store.QueryAsync((s, token) => Connection(s, command.ConnectionId, token), ct);
        CheckVersion(connection.Version, command.ExpectedVersion);
        var remote = await api.GetRepositoryAsync(Account(connection), command.Owner, command.Name, ct);
        if (remote.Id != command.RepositoryId) throw AppException.Validation("repository", "The repository changed. Choose it again.");
        if (!remote.HasIssues) throw AppException.Validation("repository", "Issues are disabled for this repository.");
        return await Mutate(command.OperationId, "github.addRepository", command, async (s, _, space, now, token) =>
        {
            var current = await Connection(s, command.ConnectionId, token);
            CheckVersion(current.Version, command.ExpectedVersion);
            await Target(s, space.Id, command.TargetContainerId, command.TargetSectionId, token);
            if ((await s.GitHub.ListSubscriptionsAsync(current.Id, token)).Any(r => r.RepositoryId == remote.Id))
                throw AppException.Validation("repository", "This repository is already added.");
            var row = new GitHubRepositoryRecord { Id = ids.NewId().ToString(), ConnectionId = current.Id, RepositoryId = remote.Id,
                Owner = remote.Owner.Login, Name = remote.Name, FullName = remote.FullName, HtmlUrl = remote.HtmlUrl, IsPrivate = remote.Private,
                TargetContainerId = command.TargetContainerId, TargetSectionId = command.TargetSectionId, FilterJson = filter,
                MilestonesAsProjects = command.MilestonesAsProjects, CreatedAtUtc = now, UpdatedAtUtc = now };
            await s.GitHub.AddRepositoryAsync(row, token);
            return row;
        }, ct);
    }

    public Task<GitHubRepositoryRecord> UpdateRepositoryAsync(UpdateGitHubRepositoryCommand command, CancellationToken ct = default)
    {
        var filter = command.Filter.ToJson();
        return Mutate(command.OperationId, "github.updateRepository", command, async (s, _, space, now, token) =>
        {
            var row = await Repository(s, command.RepositoryId, token);
            CheckVersion(row.Version, command.ExpectedVersion);
            await Target(s, space.Id, command.TargetContainerId, command.TargetSectionId, token);
            var changed = filter != row.FilterJson || command.MilestonesAsProjects != row.MilestonesAsProjects;
            if (changed) await s.GitHub.ClearCheckpointsAsync(row.Id, token);
            return await s.GitHub.UpdateRepositoryAsync(row with { TargetContainerId = command.TargetContainerId, TargetSectionId = command.TargetSectionId,
                FilterJson = filter, MilestonesAsProjects = command.MilestonesAsProjects, IssuesCursorUtc = changed ? null : row.IssuesCursorUtc,
                IssuesEtag = changed ? null : row.IssuesEtag, InitialImportDoneUtc = changed ? null : row.InitialImportDoneUtc, UpdatedAtUtc = now }, row.Version, token) ?? throw Conflict();
        }, ct);
    }

    public Task<GitHubConnectionRecord> SetConnectionPausedAsync(ChangeGitHubCommand command, CancellationToken ct = default) =>
        Mutate(command.OperationId, "github.pauseConnection", command, async (s, _, _, now, token) =>
        {
            var row = await Connection(s, command.Id, token); CheckVersion(row.Version, command.ExpectedVersion);
            return await s.GitHub.UpdateConnectionAsync(row with { State = command.Paused ? 1 : 0, UpdatedAtUtc = now }, row.Version, token) ?? throw Conflict();
        }, ct);

    public Task<GitHubRepositoryRecord> SetRepositoryPausedAsync(ChangeGitHubCommand command, CancellationToken ct = default) =>
        Mutate(command.OperationId, "github.pauseRepository", command, async (s, _, _, now, token) =>
        {
            var row = await Repository(s, command.Id, token); CheckVersion(row.Version, command.ExpectedVersion);
            return await s.GitHub.UpdateRepositoryAsync(row with { State = command.Paused ? 1 : 0, UpdatedAtUtc = now }, row.Version, token) ?? throw Conflict();
        }, ct);

    public Task<GitHubConnectionRecord> UpdateSettingsAsync(GitHubSettingsCommand command, CancellationToken ct = default) =>
        Mutate(command.OperationId, "github.settings", command, async (s, _, _, now, token) =>
        {
            var row = await Connection(s, command.Id, token); CheckVersion(row.Version, command.ExpectedVersion);
            return await s.GitHub.UpdateConnectionAsync(row with { PostDiaryByDefault = command.PostDiaryByDefault, CreateLabelsForTags = command.CreateLabelsForTags, UpdatedAtUtc = now }, row.Version, token) ?? throw Conflict();
        }, ct);

    public Task<GitHubRepositoryRecord> RemoveRepositoryAsync(ChangeGitHubCommand command, CancellationToken ct = default) =>
        Mutate(command.OperationId, "github.removeRepository", command, async (s, profile, _, now, token) =>
        {
            var row = await Repository(s, command.Id, token); CheckVersion(row.Version, command.ExpectedVersion);
            return await Remove(s, profile, row, now, token);
        }, ct);

    public Task<GitHubConnectionRecord> DisconnectAsync(ChangeGitHubCommand command, CancellationToken ct = default) =>
        Mutate(command.OperationId, "github.disconnect", command, async (s, profile, _, now, token) =>
        {
            var row = await Connection(s, command.Id, token); CheckVersion(row.Version, command.ExpectedVersion);
            foreach (var repo in await s.GitHub.ListSubscriptionsAsync(row.Id, token)) await Remove(s, profile, repo, now, token);
            return await s.GitHub.UpdateConnectionAsync(row with { State = 1, DeletedAtUtc = now, UpdatedAtUtc = now, RateLimitResetUtc = null, LastErrorKind = null, LastErrorMessage = null }, row.Version, token) ?? throw Conflict();
        }, ct);

    private async Task<GitHubRepositoryRecord> Remove(IDataSession s, ProfileRecord profile, GitHubRepositoryRecord row, DateTimeOffset now, CancellationToken ct)
    {
        string? after = null;
        while (true)
        {
            var links = await s.GitHub.ListRepositoryLinksAsync(row.Id, after, 200, ct);
            foreach (var link in links)
            {
                if (link.State == 4) continue;
                if (link.TaskId is not null && await s.Tasks.GetIncludingDeletedAsync(link.TaskId, ct) is not null)
                    await s.Diary.AddAsync(new DiaryEntryRecord { Id = ids.NewId().ToString(), TaskId = link.TaskId, AuthorProfileId = profile.Id,
                        EntryAtUtc = now, RawMarkdown = "Came from " + link.HtmlUrl, Revision = 1, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now }, ct);
                await s.GitHub.UpdateIssueLinkAsync(link with { State = link.TaskId is null ? 3 : 4, LocalDirty = false, LocalDirtySinceUtc = null,
                    LocalAssigneeIntent = null, NextAttemptUtc = null, IssueEtag = null, LastErrorKind = null, LastErrorMessage = null, UpdatedAtUtc = now }, link.Version, ct);
                await s.GitHub.ClearIssueMappingsAsync(link.Id, ct);
            }
            if (links.Count < 200) break;
            after = links[^1].Id;
        }
        await s.GitHub.ClearCheckpointsAsync(row.Id, ct);
        return await s.GitHub.UpdateRepositoryAsync(row with { State = 1, DeletedAtUtc = now, UpdatedAtUtc = now,
            IssuesCursorUtc = null, CommentsCursorUtc = null, IssuesEtag = null, CommentsEtag = null, LabelsEtag = null, MilestonesEtag = null,
            InitialImportDoneUtc = null, LastErrorKind = null, LastErrorMessage = null }, row.Version, ct) ?? throw Conflict();
    }

    private async Task<T> Mutate<T>(string operation, string kind, object payload, Func<IDataSession, ProfileRecord, SpaceRecord, DateTimeOffset, CancellationToken, Task<T>> run, CancellationToken ct)
    {
        ValidateOperation(operation);
        return await store.ExecuteAsync(async (s, token) =>
        {
            var (profile, space) = await Context(s, token);
            var now = clock.GetUtcNow();
            var result = await TaskMutationSupport.ReplayOrRunAsync(s, profile.Id, operation, kind, payload, now, t => run(s, profile, space, now, t), token);
            return result.Result;
        }, ct);
    }

    // Setup reads must also replay without contacting GitHub after a committed operation.
    private Task<T?> Replay<T>(string operation, string kind, object payload, CancellationToken ct) where T : class => store.QueryAsync(async (s, token) =>
    {
        var (profile, _) = await Context(s, token);
        if (await s.Receipts.FindAsync(profile.Id, operation, token) is null) return null;
        var result = await TaskMutationSupport.ReplayOrRunAsync<T>(s, profile.Id, operation, kind, payload, clock.GetUtcNow(), _ => throw new InvalidOperationException("Receipt disappeared in read snapshot."), token);
        return result.Result;
    }, ct);

    internal static async Task<(ProfileRecord Profile, SpaceRecord Space)> Context(IDataSession s, CancellationToken ct)
    {
        var profile = await s.Profiles.GetPrimaryAsync(ct) ?? throw AppException.NotFound("Local profile is unavailable.");
        var space = await s.Spaces.GetPersonalByOwnerAsync(profile.Id, ct) ?? throw AppException.NotFound("Personal space is unavailable.");
        return (profile, space);
    }
    private static async Task<GitHubConnectionRecord> Connection(IDataSession s, string id, CancellationToken ct)
    {
        var (profile, space) = await Context(s, ct);
        var row = await s.GitHub.GetConnectionAsync(id, ct);
        return row is not null && row.DeletedAtUtc is null && row.ProfileId == profile.Id && row.SpaceId == space.Id ? row : throw AppException.NotFound("GitHub connection was not found.");
    }
    private static async Task<GitHubRepositoryRecord> Repository(IDataSession s, string id, CancellationToken ct)
    {
        var row = await s.GitHub.GetRepositoryAsync(id, ct);
        if (row is null || row.DeletedAtUtc is not null) throw AppException.NotFound("Repository subscription was not found.");
        await Connection(s, row.ConnectionId, ct);
        return row;
    }
    private static async Task Target(IDataSession s, string space, string containerId, string? sectionId, CancellationToken ct)
    {
        var container = await s.Containers.GetAsync(containerId, ct);
        if (container is null || container.SpaceId != space || container.ArchivedAtUtc is not null) throw AppException.Validation("targetContainerId", "Choose an active container in this space.");
        if (sectionId is not null && (await s.Sections.GetAsync(sectionId, ct))?.ContainerId != containerId) throw AppException.Validation("targetSectionId", "Choose a section in this container.");
    }
    private static GitHubAccount Account(GitHubConnectionRecord row) => new(row.AccountId, row.AccountLogin);
    private static void ValidateOperation(string value) { if (!Guid.TryParse(value, out _)) throw AppException.Validation("operationId", "Use a valid operation ID."); }
    private static void CheckVersion(long actual, long expected) { if (expected <= 0 || actual != expected) throw Conflict(); }
    private static AppException Conflict() => AppException.VersionConflict("GitHub settings changed. Reload and try again.");
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", RegexOptions.CultureInvariant)] private static partial Regex LoginPattern();
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant)] private static partial Regex SegmentPattern();
}
