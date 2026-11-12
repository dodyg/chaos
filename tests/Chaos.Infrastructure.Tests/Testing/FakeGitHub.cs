using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Tests.Testing;

/// <summary>Simulated remote GitHub, used with real SQLite. It never reads credentials or makes network calls.</summary>
public sealed class FakeGitHub : IGitHubApi
{
    public GitHubAccount Account { get; set; } = new(1, "person");
    public GitHubErrorKind? NextFailure { get; set; }
    public int PageSize { get; set; } = 2;
    public DateTimeOffset Now { get; set; } = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
    public int Requests { get; private set; }
    public int RemoteWrites { get; private set; }
    public Func<Task>? AfterIssueWrite { get; set; }
    public Func<GitHubIssue, Task>? AfterIssueRead { get; set; }
    public Func<Task>? AfterSetWrite { get; set; }
    public Func<Task>? AfterCommentPost { get; set; }
    public Func<Task>? AfterCommentEdit { get; set; }
    public Func<Task>? AfterCommentDelete { get; set; }
    public Func<Task>? AfterCommentRead { get; set; }
    public Dictionary<long, GitHubErrorKind> IssueUpdateFailures { get; } = [];
    public long RequestCount => Requests;
    public long NotModifiedCount { get; private set; }
    private long _revision;
    private long _nextId = 1000000;
    private readonly Dictionary<string, GitHubRepository> _repositories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Repository, long Number), GitHubIssue> _issues = [];
    private readonly Dictionary<(string Repository, long Number), List<GitHubComment>> _comments = [];
    private readonly Dictionary<string, List<GitHubLabel>> _labels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<GitHubMilestone>> _milestones = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, IReadOnlyList<GitHubPullRequestRef>> PullRequests { get; } = new(StringComparer.Ordinal);

    private DateTimeOffset Tick() { _revision++; return Now = Now.AddSeconds(1); }
    private static string Key(string owner, string name) => (owner + "/" + name).ToLowerInvariant();
    public void PutRepository(GitHubRepository repository) { _repositories[repository.FullName.ToLowerInvariant()] = repository; Tick(); }
    public void PutIssue(string repository, GitHubIssue issue) { _issues[(repository.ToLowerInvariant(), issue.Number)] = issue; Tick(); }
    public void PutComments(string repository, long number, IEnumerable<GitHubComment> comments) { _comments[(repository.ToLowerInvariant(), number)] = comments.ToList(); Tick(); }
    public void PutLabels(string repository, IEnumerable<GitHubLabel> labels) { _labels[repository.ToLowerInvariant()] = labels.ToList(); Tick(); }
    public void PutMilestones(string repository, IEnumerable<GitHubMilestone> milestones) { _milestones[repository.ToLowerInvariant()] = milestones.ToList(); Tick(); }

    private void Check(GitHubAccount? account = null)
    {
        Requests++;
        if (NextFailure is { } failure) { NextFailure = null; throw new GitHubApiException(failure); }
        if (account is not null && account.Id != Account.Id) throw new GitHubApiException(GitHubErrorKind.AccountMismatch);
    }

    private GitHubPage<T> Page<T>(IEnumerable<T> source, string? cursor, string? etag)
    {
        var all = source.ToArray();
        var offset = cursor is null ? 0 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture);
        var current = "\"fake-" + _revision + "-" + offset + "-" + Chaos.Application.Abstractions.StoredJsonFormats.Hash(all) + "\"";
        if (etag == current) { NotModifiedCount++; return new([], null, current, true, 5000, Now.AddHours(1)); }
        var items = all.Skip(offset).Take(PageSize).ToArray();
        var next = offset + items.Length < all.Length ? (offset + items.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        return new(items, next, current, false, 5000, Now.AddHours(1), offset > 0 ? Math.Max(0, offset - PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
            offset + items.Length < all.Length ? ((all.Length - 1) / PageSize * PageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
    }

    public Task<GitHubAccount> GetSetupAccountAsync(CancellationToken ct) { Check(); return Task.FromResult(Account); }
    public Task<GitHubPage<GitHubRepository>> ListRepositoriesAsync(GitHubAccount account, string? cursor, string? etag, CancellationToken ct)
    { Check(account); return Task.FromResult(Page(_repositories.Values.OrderBy(repository => repository.Id), cursor, etag)); }
    public Task<GitHubRepository> GetRepositoryAsync(GitHubAccount account, string owner, string name, CancellationToken ct)
    { Check(account); return Task.FromResult(RedirectedRepositories.GetValueOrDefault(Key(owner, name)) ?? _repositories.GetValueOrDefault(Key(owner, name)) ?? throw new GitHubApiException(GitHubErrorKind.NotFound)); }
    public List<string> IssueListStates { get; } = [];
    public Task<GitHubPage<GitHubIssue>> ListIssuesAsync(GitHubAccount account, string owner, string name, DateTimeOffset? since, string? cursor, string? etag, CancellationToken ct, string? state = null)
    { Check(account); IssueListStates.Add(state ?? "all"); return Task.FromResult(Page(_issues.Where(pair => pair.Key.Repository == Key(owner, name)).Select(pair => Issue(owner, name, pair.Key.Number)).Where(issue => (state is null or "all" || issue.State == state) && (since is null || issue.UpdatedAt >= since)).OrderBy(issue => issue.UpdatedAt).ThenBy(issue => issue.Id), cursor, etag)); }
    private GitHubIssue Issue(string owner, string name, long number)
    {
        var issue = _issues.GetValueOrDefault((Key(owner, name), number)) ?? throw new GitHubApiException(GitHubErrorKind.NotFound);
        if (_labels.TryGetValue(Key(owner, name), out var labels)) issue = issue with { Labels = issue.Labels.Where(l => labels.Any(c => c.Id == l.Id)).Select(l => labels.Single(c => c.Id == l.Id)).ToArray() };
        if (issue.Milestone is { } milestone && _milestones.TryGetValue(Key(owner, name), out var milestones)) issue = issue with { Milestone = milestones.FirstOrDefault(m => m.Id == milestone.Id) };
        return issue;
    }
    public async Task<GitHubIssue> GetIssueAsync(GitHubAccount account, string owner, string name, long number, CancellationToken ct)
    {
        Check(account);
        var issue = Issue(owner, name, number);
        if (AfterIssueRead is { } hook) await hook(issue);
        return issue;
    }
    public Dictionary<string, GitHubRepository> RedirectedRepositories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<(string Repository, long Number), int> IssueReads { get; } = [];
    public Dictionary<(string Repository, long Number), Queue<GitHubErrorKind>> IssueFailureSequences { get; } = [];
    public Dictionary<(string Repository, long Number), GitHubIssue> RedirectedIssues { get; } = [];
    public Dictionary<(string Repository, long Number), GitHubErrorKind> IssueFailures { get; } = [];
    public void RemoveIssue(string repository, long number) { _issues.Remove((repository.ToLowerInvariant(), number)); Tick(); }
    public Task<GitHubIssueRead> ReadIssueAsync(GitHubAccount account, string owner, string name, long number, string? etag, CancellationToken ct)
    {
        Check(account);
        var key = (Key(owner, name), number);
        IssueReads[key] = IssueReads.GetValueOrDefault(key) + 1;
        if (IssueFailureSequences.TryGetValue(key, out var sequence) && sequence.TryDequeue(out var temporary)) throw new GitHubApiException(temporary);
        if (IssueFailures.TryGetValue(key, out var failure)) throw new GitHubApiException(failure);
        var issue = RedirectedIssues.GetValueOrDefault(key) ?? Issue(owner, name, number);
        var current = "\"issue-" + Chaos.Application.Abstractions.StoredJsonFormats.Hash(issue) + "\"";
        if (current == etag) { NotModifiedCount++; return Task.FromResult(new GitHubIssueRead(null, current, true)); }
        return Task.FromResult(new GitHubIssueRead(issue, current, false));
    }

    public async Task<GitHubIssue> UpdateIssueAsync(GitHubAccount account, string owner, string name, long number, GitHubIssueUpdate update, CancellationToken ct)
    {
        Check(account);
        if (IssueUpdateFailures.TryGetValue(number, out var failure)) throw new GitHubApiException(failure);
        RemoteWrites++;
        var issue = Issue(owner, name, number);
        var changed = issue with { Title = update.Title ?? issue.Title, Body = update.Body ?? issue.Body,
            State = update.State ?? issue.State, StateReason = update.StateReason ?? issue.StateReason, UpdatedAt = Tick() };
        _issues[(Key(owner, name), number)] = changed;
        if (AfterIssueWrite is { } hook) await hook();
        return changed;
    }
    public Task<GitHubPage<GitHubLabel>> ListLabelsAsync(GitHubAccount account, string owner, string name, string? cursor, string? etag, CancellationToken ct)
    { Check(account); return Task.FromResult(Page(_labels.GetValueOrDefault(Key(owner, name)) ?? [], cursor, etag)); }
    public Task<GitHubPage<GitHubMilestone>> ListMilestonesAsync(GitHubAccount account, string owner, string name, string? cursor, string? etag, CancellationToken ct)
    { Check(account); return Task.FromResult(Page(_milestones.GetValueOrDefault(Key(owner, name)) ?? [], cursor, etag)); }
    public async Task<GitHubPage<GitHubComment>> ListCommentsAsync(GitHubAccount account, string owner, string name, DateTimeOffset? since, long? issueNumber, string? cursor, string? etag, CancellationToken ct)
    {
        Check(account);
        var page = Page(_comments.Where(pair => pair.Key.Repository == Key(owner, name) && (issueNumber is null || pair.Key.Number == issueNumber)).SelectMany(pair => pair.Value.Select(c => c with { IssueUrl = "https://api.github.com/repos/" + Key(owner, name) + "/issues/" + pair.Key.Number })).Where(comment => since is null || comment.UpdatedAt >= since).OrderBy(comment => issueNumber is null ? comment.UpdatedAt : comment.CreatedAt).ThenBy(comment => comment.Id), cursor, etag);
        if (AfterCommentRead is { } hook) await hook();
        return page;
    }
    public async Task<GitHubComment> PostCommentAsync(GitHubAccount account, string owner, string name, long number, string body, CancellationToken ct)
    {
        Check(account); RemoteWrites++;
        var issue = Issue(owner, name, number);
        var time = Tick();
        var comment = new GitHubComment(++_nextId, body, new(account.Id, account.Login), time, time, issue.HtmlUrl + "#issuecomment-" + _nextId);
        var key = (Key(owner, name), number);
        if (!_comments.TryGetValue(key, out var comments)) _comments[key] = comments = [];
        comments.Add(comment);
        _issues[key] = issue with { Comments = comments.Count, UpdatedAt = time };
        if (AfterCommentPost is { } hook) await hook();
        return comment;
    }
    private (List<GitHubComment> Comments, int Index, (string Repository, long Number) IssueKey) Comment(string owner, string name, long id)
    {
        foreach (var pair in _comments.Where(pair => pair.Key.Repository == Key(owner, name)))
        {
            var index = pair.Value.FindIndex(comment => comment.Id == id);
            if (index >= 0) return (pair.Value, index, pair.Key);
        }
        throw new GitHubApiException(GitHubErrorKind.NotFound);
    }
    public async Task<GitHubComment> EditCommentAsync(GitHubAccount account, string owner, string name, long commentId, string body, CancellationToken ct)
    {
        Check(account); RemoteWrites++; var (comments, index, key) = Comment(owner, name, commentId);
        if (comments[index].User.Id != account.Id) throw new GitHubApiException(GitHubErrorKind.Forbidden);
        var changed = comments[index] with { Body = body, UpdatedAt = Tick() };
        comments[index] = changed;
        _issues[key] = _issues[key] with { UpdatedAt = changed.UpdatedAt };
        if (AfterCommentEdit is { } hook) await hook();
        return changed;
    }
    public async Task DeleteCommentAsync(GitHubAccount account, string owner, string name, long commentId, CancellationToken ct)
    {
        Check(account); RemoteWrites++; var (comments, index, key) = Comment(owner, name, commentId);
        if (comments[index].User.Id != account.Id) throw new GitHubApiException(GitHubErrorKind.Forbidden);
        comments.RemoveAt(index); _issues[key] = _issues[key] with { Comments = comments.Count, UpdatedAt = Tick() };
        if (AfterCommentDelete is { } hook) await hook();
    }
    public async Task AddLabelAsync(GitHubAccount account, string owner, string name, long number, string label, CancellationToken ct)
    {
        Check(account); RemoteWrites++; var issue = Issue(owner, name, number);
        var existing = (_labels.GetValueOrDefault(Key(owner, name)) ?? []).FirstOrDefault(item => item.Name.Equals(label, StringComparison.OrdinalIgnoreCase)) ?? throw new GitHubApiException(GitHubErrorKind.Validation);
        PutIssue(Key(owner, name), issue with { Labels = issue.Labels.Append(existing).DistinctBy(item => item.Id).ToArray(), UpdatedAt = Tick() });
        if (AfterSetWrite is { } hook) await hook();
    }
    public Task RemoveLabelAsync(GitHubAccount account, string owner, string name, long number, string label, CancellationToken ct)
    { Check(account); RemoteWrites++; var issue = Issue(owner, name, number); PutIssue(Key(owner, name), issue with { Labels = issue.Labels.Where(item => !item.Name.Equals(label, StringComparison.OrdinalIgnoreCase)).ToArray(), UpdatedAt = Tick() }); return Task.CompletedTask; }
    public Task<GitHubLabel> CreateLabelAsync(GitHubAccount account, string owner, string name, string label, CancellationToken ct)
    {
        Check(account); RemoteWrites++; var key = Key(owner, name);
        if (!_labels.TryGetValue(key, out var labels)) _labels[key] = labels = [];
        if (labels.Any(item => item.Name.Equals(label, StringComparison.OrdinalIgnoreCase))) throw new GitHubApiException(GitHubErrorKind.Validation);
        var created = new GitHubLabel(++_nextId, label, "cfd3d7"); labels.Add(created); Tick(); return Task.FromResult(created);
    }
    public async Task SetSelfAssignmentAsync(GitHubAccount account, string owner, string name, long number, bool assign, CancellationToken ct)
    {
        Check(account); RemoteWrites++; var issue = Issue(owner, name, number);
        var others = issue.Assignees.Where(user => user.Id != account.Id);
        PutIssue(Key(owner, name), issue with { Assignees = (assign ? others.Append(new(account.Id, account.Login)) : others).ToArray(), UpdatedAt = Tick() });
        if (AfterSetWrite is { } hook) await hook();
    }
    public Task<IReadOnlyDictionary<string, IReadOnlyList<GitHubPullRequestRef>>> GetClosingPullRequestsAsync(GitHubAccount account, IReadOnlyList<string> issueNodeIds, CancellationToken ct)
    {
        Check(account);
        if (issueNodeIds.Count > 50) throw new GitHubApiException(GitHubErrorKind.Validation);
        return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<GitHubPullRequestRef>>>(issueNodeIds.Distinct().ToDictionary(id => id, id => PullRequests.GetValueOrDefault(id) ?? []));
    }
}
