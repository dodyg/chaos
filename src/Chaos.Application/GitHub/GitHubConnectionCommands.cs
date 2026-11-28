using System.Text.Json;
using System.Text.RegularExpressions;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;

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
/// <summary>The connection of one space as this device sees it. <see cref="IsRunner"/> says the engine runs here; <see cref="IsOwner"/> says this person manages it.</summary>
public sealed record GitHubStatus(GitHubConnectionRecord? Connection, IReadOnlyList<GitHubRepositoryRecord> Repositories, bool IsRunner = false, bool IsOwner = false, string? SpaceId = null, string? SpaceName = null);
public sealed record GitHubSchedule(GitHubStatus Status, DateTimeOffset? ConnectionRetryUtc, IReadOnlyList<GitHubIssueLinkRecord> LinkRetries, int IntervalMinutes = 5);
public sealed record GitHubRepositoryCandidate(GitHubRepository Repository, bool AlreadyAdded);
public sealed record GitHubRepositoryCandidates(IReadOnlyList<GitHubRepositoryCandidate> Repositories, string? NextCursor);
public sealed record ConnectGitHubCommand(string OperationId, string? SpaceId = null);
public sealed record AddGitHubRepositoryCommand(string OperationId, string ConnectionId, long ExpectedVersion, string Owner, string Name, long RepositoryId, string TargetContainerId, string? TargetSectionId, GitHubFilter Filter, bool MilestonesAsProjects, CapacityCategory TaskCategory = CapacityCategory.Personal);
public sealed record UpdateGitHubRepositoryCommand(string OperationId, string RepositoryId, long ExpectedVersion, string TargetContainerId, string? TargetSectionId, GitHubFilter Filter, bool MilestonesAsProjects, CapacityCategory TaskCategory = CapacityCategory.Personal);
public sealed record ChangeGitHubCommand(string OperationId, string Id, long ExpectedVersion, bool Paused = false);
public sealed record GitHubSettingsCommand(string OperationId, string Id, long ExpectedVersion, bool PostDiaryByDefault, bool CreateLabelsForTags);
public sealed record GitHubSelfAssignmentCommand(string OperationId, string TaskId, bool Assigned);
public sealed record GitHubTaskActionCommand(string OperationId, string TaskId, long ExpectedVersion);

/// <summary>
/// Local configuration with explicit remote setup reads. No network call holds a database transaction.
/// A null space id means the Personal space; in a shared space only an owner manages the connection (PLAN3 §10.1).
/// </summary>
public sealed partial class GitHubConnectionCommands(IApplicationStore store, IGitHubApi api, IIdGenerator ids, TimeProvider clock, IDeviceInfo? device = null)
{
    private string DeviceName => device?.Name ?? "This device";

    public Task<bool> RetryTaskAsync(GitHubTaskActionCommand command, CancellationToken ct = default) =>
        TaskAction(command, "retry", ct);

    public Task<bool> DismissConflictAsync(GitHubTaskActionCommand command, CancellationToken ct = default) =>
        TaskAction(command, "dismiss-conflict", ct);

    public Task<bool> KeepAsOrdinaryTaskAsync(GitHubTaskActionCommand command, CancellationToken ct = default) =>
        TaskAction(command, "detach", ct);

    private Task<bool> TaskAction(GitHubTaskActionCommand command, string action, CancellationToken ct) =>
        MutateIn(command.OperationId, "github." + action, command, async (s, token) =>
        {
            var task = await s.Tasks.GetAsync(command.TaskId, token) ?? throw AppException.NotFound("The linked GitHub task was not found.");
            return await GitHubSpaces.ResolveAsync(s, task.SpaceId, token);
        }, async (s, profile, space, now, token) =>
        {
            var task = await s.Tasks.GetAsync(command.TaskId, token);
            var link = await s.GitHub.FindLinkByTaskAsync(command.TaskId, token);
            if (task is null || task.SpaceId != space.Id || link is null || link.State == 4)
                throw AppException.NotFound("The linked GitHub task was not found.");
            await Connection(s, link.ConnectionId, token);
            CheckVersion(link.Version, command.ExpectedVersion);
            if (action == "detach") await GitHubSpaces.RequireOwnerAsync(s, space, profile, token);
            var next = action switch
            {
                "retry" => link with { LastErrorKind = null, LastErrorMessage = null, ErrorAttempts = 0, NextAttemptUtc = null, IssueEtag = null, LocalDirty = true, LocalDirtySinceUtc = link.LocalDirtySinceUtc ?? now },
                "dismiss-conflict" => link with { ConflictNoticeUtc = null },
                _ => link with { State = 4, LocalDirty = false, LocalDirtySinceUtc = null, LocalAssigneeIntent = null, ConflictNoticeUtc = null, LastErrorKind = null, LastErrorMessage = null, ErrorAttempts = 0, NextAttemptUtc = null, IssueEtag = null },
            };
            if (action == "detach")
            {
                await s.Diary.AddAsync(new DiaryEntryRecord { Id = ids.NewId().ToString(), TaskId = task.Id, AuthorProfileId = profile.Id,
                    EntryAtUtc = now, RawMarkdown = "Came from " + link.HtmlUrl, Revision = 1, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now }, token);
                await s.GitHub.DeleteIssueWriteAsync(link.Id, token);
                await s.GitHub.ClearIssueMappingsAsync(link.Id, token);
            }
            return await s.GitHub.UpdateIssueLinkAsync(next with { UpdatedAtUtc = now }, link.Version, token) is not null;
        }, ct);

    /// <summary>
    /// "Assign to me" / "Unassign me". In the Personal space it sets the link's own intent; in a shared space the
    /// person is an ordinary assignee (PLAN3 10.3), whose GitHub login, if set, the runner then puts on the issue.
    /// </summary>
    public async Task<bool> SetSelfAssignmentAsync(GitHubSelfAssignmentCommand command, CancellationToken ct = default)
    {
        var shared = await store.QueryAsync(async (s, token) =>
        {
            var task = await s.Tasks.GetAsync(command.TaskId, token);
            var link = task is null ? null : await s.GitHub.FindLinkByTaskAsync(task.Id, token);
            if (task is null || link is not { State: 0 }) throw AppException.Validation("taskId", "Choose a task linked to an active GitHub issue.");
            var space = await s.Spaces.GetAsync(task.SpaceId, token);
            return space is { Kind: SpaceKind.Shared } ? task.Version : (long?)null;
        }, ct);
        if (shared is { } version)
        {
            var profile = await store.QueryAsync((s, token) => s.Profiles.GetPrimaryAsync(token), ct) ?? throw AppException.NotFound("Local profile is unavailable.");
            await new TaskCommands(store, ids, clock).ChangeAssigneeAsync(new ChangeAssigneeCommand(command.OperationId, command.TaskId, version, profile.Id, command.Assigned), ct);
            return true;
        }
        return await Mutate(command.OperationId, "github.self-assignment", command, async (s, profile, space, now, token) =>
        {
            var task = await s.Tasks.GetAsync(command.TaskId, token);
            var link = await s.GitHub.FindLinkByTaskAsync(command.TaskId, token);
            if (task is null || task.SpaceId != space.Id || link is not { State: 0 }) throw AppException.Validation("taskId", "Choose a task linked to an active GitHub issue.");
            var connection = await s.GitHub.GetConnectionAsync(link.ConnectionId, token);
            var repo = await s.GitHub.GetRepositoryAsync(link.RepositorySubscriptionId, token);
            if (connection is null || connection.DeletedAtUtc is not null || !await GitHubSpaces.CanRunAsync(s, connection, profile, token) || repo is null || repo.DeletedAtUtc is not null)
                throw AppException.NotFound("The GitHub connection is unavailable.");
            _ = await s.GitHub.UpdateIssueLinkAsync(link with { LocalAssigneeIntent = command.Assigned ? 1 : 0, LocalDirty = true,
                LocalDirtySinceUtc = link.LocalDirtySinceUtc ?? now, UpdatedAtUtc = now }, link.Version, token) ?? throw Conflict();
            return true;
        }, ct);
    }

    public Task<GitHubStatus> StatusAsync(string? spaceId = null, CancellationToken ct = default) => store.QueryAsync(async (s, token) =>
    {
        var (profile, space) = await GitHubSpaces.ResolveAsync(s, spaceId, token);
        var connection = await s.GitHub.GetConnectionForSpaceAsync(space.Id, token);
        return new GitHubStatus(connection, connection is null ? [] : await s.GitHub.ListSubscriptionsAsync(connection.Id, token),
            connection is not null && connection.RunnerDeviceId == await s.GitHub.GetDeviceIdAsync(token),
            await GitHubSpaces.IsOwnerAsync(s, space, profile, token), space.Id, space.Name);
    }, ct);

    /// <summary>Every connection whose space this person owns and whose runner is this device; the scheduler serves each in turn.</summary>
    public Task<IReadOnlyList<GitHubSchedule>> ScheduleAsync(CancellationToken ct = default) => store.QueryAsync<IReadOnlyList<GitHubSchedule>>(async (s, token) =>
    {
        var profile = await s.Profiles.GetPrimaryAsync(token);
        if (profile is null) return [];
        var deviceId = await s.GitHub.GetDeviceIdAsync(token);
        var result = new List<GitHubSchedule>();
        var interval = await s.GitHub.GetSyncIntervalMinutesAsync(token);
        foreach (var connection in await s.GitHub.ListConnectionsAsync(token))
        {
            if (connection.RunnerDeviceId != deviceId || !await GitHubSpaces.CanRunAsync(s, connection, profile, token)) continue;
            var space = await GitHubSpaces.SpaceOfAsync(s, connection, token);
            result.Add(new(new(connection, await s.GitHub.ListSubscriptionsAsync(connection.Id, token), true, true, space.Id, space.Name),
                await s.GitHub.GetConnectionRetryAsync(connection.Id, token), await s.GitHub.ListScheduledRetriesAsync(connection.Id, 200, token), interval));
        }
        return result;
    }, ct);

    public Task<int> GetSyncIntervalMinutesAsync(CancellationToken ct = default) => store.QueryAsync((s, token) => s.GitHub.GetSyncIntervalMinutesAsync(token), ct);

    /// <summary>Sets how often this device runs a full GitHub sync pass, in minutes (1 to 1440).</summary>
    public async Task SetSyncIntervalMinutesAsync(int minutes, CancellationToken ct = default)
    {
        if (minutes is < 1 or > 1440) throw AppException.Validation("minutes", "Choose between 1 minute and 24 hours.");
        await store.ExecuteAsync(async (s, token) => { await s.GitHub.SetSyncIntervalMinutesAsync(minutes, token); return true; }, ct);
    }

    public async Task<GitHubConnectionRecord> ConnectAsync(ConnectGitHubCommand command, CancellationToken ct = default)
    {
        ValidateOperation(command.OperationId);
        var replay = await Replay<GitHubConnectionRecord>(command.OperationId, "github.connect", command, ct);
        if (replay is not null) return replay;
        await store.ExecuteAsync(async (s, token) =>
        {
            var (profile, space) = await GitHubSpaces.ResolveAsync(s, command.SpaceId, token);
            await GitHubSpaces.RequireOwnerAsync(s, space, profile, token);
            return true;
        }, ct);
        var account = await api.GetSetupAccountAsync(ct);
        if (account.Id <= 0 || !LoginPattern().IsMatch(account.Login)) throw new GitHubApiException(GitHubErrorKind.Server);
        return await MutateIn(command.OperationId, "github.connect", command, (s, token) => GitHubSpaces.ResolveAsync(s, command.SpaceId, token), async (s, profile, space, now, token) =>
        {
            await GitHubSpaces.RequireOwnerAsync(s, space, profile, token);
            var existing = await s.GitHub.GetConnectionForSpaceAsync(space.Id, token);
            if (existing is not null)
            {
                if (existing.AccountId != account.Id) throw AppException.Validation("account", "This space is connected to @" + existing.AccountLogin + ". Disconnect first.");
                return await s.GitHub.UpdateConnectionAsync(existing with { AccountLogin = account.Login, State = 0, LastErrorKind = null, LastErrorMessage = null, UpdatedAtUtc = now }, existing.Version, token)
                    ?? throw Conflict();
            }
            var row = new GitHubConnectionRecord { Id = ids.NewId().ToString(), SpaceId = space.Id, ProfileId = profile.Id,
                AccountId = account.Id, AccountLogin = account.Login, RunnerDeviceId = await s.GitHub.GetDeviceIdAsync(token), RunnerDeviceName = DeviceName, WebBaseUrl = "https://github.com", ApiBaseUrl = "https://api.github.com", CreatedAtUtc = now, UpdatedAtUtc = now };
            await s.GitHub.AddConnectionAsync(row, token);
            return row;
        }, ct);
    }

    /// <summary>Verifies the local CLI account before assigning this device to run GitHub sync. Only an owner's device may be the runner.</summary>
    public async Task<GitHubConnectionRecord> RunOnThisDeviceAsync(ChangeGitHubCommand command, CancellationToken ct = default)
    {
        ValidateOperation(command.OperationId);
        var replay = await Replay<GitHubConnectionRecord>(command.OperationId, "github.runOnThisDevice", command, ct);
        if (replay is not null) return replay;
        await store.ExecuteAsync(async (s, token) =>
        {
            var row = await Connection(s, command.Id, token);
            await GitHubSpaces.RequireOwnerAsync(s, await GitHubSpaces.SpaceOfAsync(s, row, token), await s.Profiles.GetPrimaryAsync(token)
                ?? throw AppException.NotFound("Local profile is unavailable."), token);
            CheckVersion(row.Version, command.ExpectedVersion);
            return true;
        }, ct);
        var account = await api.GetSetupAccountAsync(ct);
        return await MutateIn(command.OperationId, "github.runOnThisDevice", command, RowSpace(command.Id), async (s, profile, space, now, token) =>
        {
            await GitHubSpaces.RequireOwnerAsync(s, space, profile, token);
            var row = await Connection(s, command.Id, token);
            CheckVersion(row.Version, command.ExpectedVersion);
            if (account.Id != row.AccountId) throw AppException.Validation("account", "Sign in to the same GitHub account as @" + row.AccountLogin + " on this device.");
            var deviceId = await s.GitHub.GetDeviceIdAsync(token);
            if (row.RunnerDeviceId == deviceId) return row;
            await s.GitHub.ResetRunnerStateAsync(row.Id, token);
            return await s.GitHub.UpdateConnectionAsync(row with { RunnerDeviceId = deviceId, RunnerDeviceName = DeviceName, RateLimitResetUtc = null, LastErrorKind = null, LastErrorMessage = null, UpdatedAtUtc = now }, row.Version, token) ?? throw Conflict();
        }, ct);
    }

    public async Task<GitHubRepositoryCandidates> ListReachableRepositoriesAsync(string? cursor, string? spaceId = null, CancellationToken ct = default)
    {
        var status = await StatusAsync(spaceId, ct);
        var connection = status.Connection ?? throw AppException.NotFound("Connect to GitHub first.");
        RequireOwner(status);
        var page = await api.ListRepositoriesAsync(Account(connection), cursor, null, ct);
        return new(page.Items.Select(repo => new GitHubRepositoryCandidate(repo, status.Repositories.Any(r => r.RepositoryId == repo.Id))).ToArray(), page.NextCursor);
    }

    public async Task<GitHubRepositoryCandidate> LookupRepositoryAsync(string owner, string name, string? spaceId = null, CancellationToken ct = default)
    {
        if (!SegmentPattern().IsMatch(owner) || !SegmentPattern().IsMatch(name) || owner is "." or ".." || name is "." or "..")
            throw AppException.Validation("repository", "Enter owner/repository.");
        var status = await StatusAsync(spaceId, ct);
        var connection = status.Connection ?? throw AppException.NotFound("Connect to GitHub first.");
        RequireOwner(status);
        var repo = await api.GetRepositoryAsync(Account(connection), owner, name, ct);
        return new(repo, status.Repositories.Any(r => r.RepositoryId == repo.Id));
    }

    /// <summary>Non-owners are refused before any GitHub CLI call: the connection's account is an owner's (PLAN3 10.1).</summary>
    private static void RequireOwner(GitHubStatus status)
    {
        if (!status.IsOwner) throw AppException.Validation("spaceId", GitHubSpaces.OwnerOnly(status.SpaceName ?? "this space"));
    }

    public async Task<GitHubRepositoryRecord> AddRepositoryAsync(AddGitHubRepositoryCommand command, CancellationToken ct = default)
    {
        ValidateOperation(command.OperationId);
        var filter = command.Filter.ToJson();
        var category = TaskFieldValidation.Category(command.TaskCategory, "taskCategory");
        if (!SegmentPattern().IsMatch(command.Owner) || !SegmentPattern().IsMatch(command.Name) || command.Owner is "." or ".." || command.Name is "." or ".." || command.RepositoryId <= 0)
            throw AppException.Validation("repository", "Enter a valid owner, repository and numeric repository ID.");
        var replay = await Replay<GitHubRepositoryRecord>(command.OperationId, "github.addRepository", command, ct);
        if (replay is not null) return replay;
        var connection = await store.ExecuteAsync(async (s, token) =>
        {
            var row = await Connection(s, command.ConnectionId, token);
            await GitHubSpaces.RequireOwnerAsync(s, await GitHubSpaces.SpaceOfAsync(s, row, token), await s.Profiles.GetPrimaryAsync(token)
                ?? throw AppException.NotFound("Local profile is unavailable."), token);
            return row;
        }, ct);
        CheckVersion(connection.Version, command.ExpectedVersion);
        var remote = await api.GetRepositoryAsync(Account(connection), command.Owner, command.Name, ct);
        if (remote.Id != command.RepositoryId) throw AppException.Validation("repository", "The repository changed. Choose it again.");
        if (!remote.HasIssues) throw AppException.Validation("repository", "Issues are disabled for this repository.");
        return await MutateIn(command.OperationId, "github.addRepository", command, RowSpace(command.ConnectionId), async (s, _, space, now, token) =>
        {
            var current = await Connection(s, command.ConnectionId, token);
            CheckVersion(current.Version, command.ExpectedVersion);
            await Target(s, space.Id, command.TargetContainerId, command.TargetSectionId, token);
            if ((await s.GitHub.ListSubscriptionsAsync(current.Id, token)).Any(r => r.RepositoryId == remote.Id))
                throw AppException.Validation("repository", "This repository is already added.");
            var row = new GitHubRepositoryRecord { Id = ids.NewId().ToString(), ConnectionId = current.Id, RepositoryId = remote.Id,
                Owner = remote.Owner.Login, Name = remote.Name, FullName = remote.FullName, HtmlUrl = remote.HtmlUrl, IsPrivate = remote.Private,
                TargetContainerId = command.TargetContainerId, TargetSectionId = command.TargetSectionId, FilterJson = filter,
                MilestonesAsProjects = command.MilestonesAsProjects, TaskCategory = category, CreatedAtUtc = now, UpdatedAtUtc = now };
            await s.GitHub.AddRepositoryAsync(row, token);
            return row;
        }, ct);
    }

    public Task<GitHubRepositoryRecord> UpdateRepositoryAsync(UpdateGitHubRepositoryCommand command, CancellationToken ct = default)
    {
        var filter = command.Filter.ToJson();
        var category = TaskFieldValidation.Category(command.TaskCategory, "taskCategory");
        return MutateIn(command.OperationId, "github.updateRepository", command, RowSpace(command.RepositoryId), async (s, profile, space, now, token) =>
        {
            await GitHubSpaces.RequireOwnerAsync(s, space, profile, token);
            var row = await Repository(s, command.RepositoryId, token);
            CheckVersion(row.Version, command.ExpectedVersion);
            await Target(s, space.Id, command.TargetContainerId, command.TargetSectionId, token);
            var changed = filter != row.FilterJson || command.MilestonesAsProjects != row.MilestonesAsProjects;
            if (changed) await s.GitHub.ClearCheckpointsAsync(row.Id, token);
            return await s.GitHub.UpdateRepositoryAsync(row with { TargetContainerId = command.TargetContainerId, TargetSectionId = command.TargetSectionId,
                FilterJson = filter, MilestonesAsProjects = command.MilestonesAsProjects, TaskCategory = category, IssuesCursorUtc = changed ? null : row.IssuesCursorUtc,
                IssuesEtag = changed ? null : row.IssuesEtag, InitialImportDoneUtc = changed ? null : row.InitialImportDoneUtc, UpdatedAtUtc = now }, row.Version, token) ?? throw Conflict();
        }, ct);
    }

    public Task<GitHubConnectionRecord> SetConnectionPausedAsync(ChangeGitHubCommand command, CancellationToken ct = default) =>
        MutateIn(command.OperationId, "github.pauseConnection", command, RowSpace(command.Id), async (s, profile, space, now, token) =>
        {
            await GitHubSpaces.RequireOwnerAsync(s, space, profile, token);
            var row = await Connection(s, command.Id, token); CheckVersion(row.Version, command.ExpectedVersion);
            return await s.GitHub.UpdateConnectionAsync(row with { State = command.Paused ? 1 : 0, UpdatedAtUtc = now }, row.Version, token) ?? throw Conflict();
        }, ct);

    public Task<GitHubRepositoryRecord> SetRepositoryPausedAsync(ChangeGitHubCommand command, CancellationToken ct = default) =>
        MutateIn(command.OperationId, "github.pauseRepository", command, RowSpace(command.Id), async (s, profile, space, now, token) =>
        {
            await GitHubSpaces.RequireOwnerAsync(s, space, profile, token);
            var row = await Repository(s, command.Id, token); CheckVersion(row.Version, command.ExpectedVersion);
            return await s.GitHub.UpdateRepositoryAsync(row with { State = command.Paused ? 1 : 0, UpdatedAtUtc = now }, row.Version, token) ?? throw Conflict();
        }, ct);

    public Task<GitHubConnectionRecord> UpdateSettingsAsync(GitHubSettingsCommand command, CancellationToken ct = default) =>
        MutateIn(command.OperationId, "github.settings", command, RowSpace(command.Id), async (s, profile, space, now, token) =>
        {
            await GitHubSpaces.RequireOwnerAsync(s, space, profile, token);
            var row = await Connection(s, command.Id, token); CheckVersion(row.Version, command.ExpectedVersion);
            return await s.GitHub.UpdateConnectionAsync(row with { PostDiaryByDefault = command.PostDiaryByDefault, CreateLabelsForTags = command.CreateLabelsForTags, UpdatedAtUtc = now }, row.Version, token) ?? throw Conflict();
        }, ct);

    public Task<GitHubRepositoryRecord> RemoveRepositoryAsync(ChangeGitHubCommand command, CancellationToken ct = default) =>
        MutateIn(command.OperationId, "github.removeRepository", command, RowSpace(command.Id), async (s, profile, space, now, token) =>
        {
            await GitHubSpaces.RequireOwnerAsync(s, space, profile, token);
            var row = await Repository(s, command.Id, token); CheckVersion(row.Version, command.ExpectedVersion);
            return await Remove(s, profile, row, now, token);
        }, ct);

    public Task<GitHubConnectionRecord> DisconnectAsync(ChangeGitHubCommand command, CancellationToken ct = default) =>
        MutateIn(command.OperationId, "github.disconnect", command, RowSpace(command.Id), async (s, profile, space, now, token) =>
        {
            await GitHubSpaces.RequireOwnerAsync(s, space, profile, token);
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
                await s.GitHub.DeleteIssueWriteAsync(link.Id, ct);
            }
            if (links.Count < 200) break;
            after = links[^1].Id;
        }
        await s.GitHub.ClearCheckpointsAsync(row.Id, ct);
        return await s.GitHub.UpdateRepositoryAsync(row with { State = 1, DeletedAtUtc = now, UpdatedAtUtc = now,
            IssuesCursorUtc = null, CommentsCursorUtc = null, IssuesEtag = null, CommentsEtag = null, LabelsEtag = null, MilestonesEtag = null,
            InitialImportDoneUtc = null, LastErrorKind = null, LastErrorMessage = null }, row.Version, ct) ?? throw Conflict();
    }

    private Task<T> Mutate<T>(string operation, string kind, object payload, Func<IDataSession, ProfileRecord, SpaceRecord, DateTimeOffset, CancellationToken, Task<T>> run, CancellationToken ct) =>
        MutateIn(operation, kind, payload, (s, token) => GitHubSpaces.ResolveAsync(s, null, token), run, ct);

    /// <summary>The space of a connection or repository row, read in the same transaction as the change.</summary>
    private Func<IDataSession, CancellationToken, Task<(ProfileRecord Profile, SpaceRecord Space)>> RowSpace(string id) =>
        async (s, token) =>
        {
            var profile = await s.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
            var connection = await s.GitHub.GetConnectionAsync(id, token);
            if (connection is null && await s.GitHub.GetRepositoryAsync(id, token) is { } repository) connection = await s.GitHub.GetConnectionAsync(repository.ConnectionId, token);
            return (profile, await GitHubSpaces.SpaceOfAsync(s, connection ?? throw AppException.NotFound("GitHub connection was not found."), token));
        };

    private async Task<T> MutateIn<T>(string operation, string kind, object payload, Func<IDataSession, CancellationToken, Task<(ProfileRecord Profile, SpaceRecord Space)>> context,
        Func<IDataSession, ProfileRecord, SpaceRecord, DateTimeOffset, CancellationToken, Task<T>> run, CancellationToken ct)
    {
        ValidateOperation(operation);
        return await store.ExecuteAsync(async (s, token) =>
        {
            var (profile, space) = await context(s, token);
            var now = clock.GetUtcNow();
            var result = await TaskMutationSupport.ReplayOrRunAsync(s, profile.Id, operation, kind, payload, now, t => run(s, profile, space, now, t), token);
            return result.Result;
        }, ct);
    }

    // Setup reads must also replay without contacting GitHub after a committed operation.
    private Task<T?> Replay<T>(string operation, string kind, object payload, CancellationToken ct) where T : class => store.QueryAsync(async (s, token) =>
    {
        var profile = await s.Profiles.GetPrimaryAsync(token) ?? throw AppException.NotFound("Local profile is unavailable.");
        if (await s.Receipts.FindAsync(profile.Id, operation, token) is null) return null;
        var result = await TaskMutationSupport.ReplayOrRunAsync<T>(s, profile.Id, operation, kind, payload, clock.GetUtcNow(), _ => throw new InvalidOperationException("Receipt disappeared in read snapshot."), token);
        return result.Result;
    }, ct);

    /// <summary>A live connection by id; who may use it is decided per command (owner to manage, owner's device to run).</summary>
    private static async Task<GitHubConnectionRecord> Connection(IDataSession s, string id, CancellationToken ct)
    {
        var row = await s.GitHub.GetConnectionAsync(id, ct);
        return row is not null && row.DeletedAtUtc is null ? row : throw AppException.NotFound("GitHub connection was not found.");
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
