using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.GitHub;

/// <summary>CLI authentication with direct, account-verified REST requests. No automatic mutation retries.</summary>
public sealed class GitHubCliClient : IGitHubApi, IDisposable
{
    private readonly GitHubCliCredentialSource _credentials;
    private readonly GitHubAccountTransport _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _clock;
    private DateTimeOffset _nextWrite;
    private readonly GitHubRateLimiter _rateLimiter;
    public long RequestCount => _transport.RequestCount;
    public long NotModifiedCount => _transport.NotModifiedCount;

    public GitHubCliClient() : this(new GitHubCliRunner(), null, TimeProvider.System) { }

    public GitHubCliClient(IGitHubCliRunner runner, HttpMessageHandler? handler, TimeProvider clock)
    {
        _credentials = new(runner);
        _transport = handler is null ? new(_credentials) : new(_credentials, handler);
        _clock = clock;
        _rateLimiter = new(clock);
    }

    public Task<(string ContentType, byte[] Data)> ReadAttachmentAsync(GitHubAccount account, Uri endpoint, CancellationToken ct) => _transport.ReadAttachmentAsync(account, endpoint, ct);

    public async Task<GitHubAccount> GetSetupAccountAsync(CancellationToken ct) =>
        await _transport.GetAccountAsync(await _credentials.GetActiveLoginAsync(ct), ct);

    public Task<GitHubPage<GitHubRepository>> ListRepositoriesAsync(GitHubAccount account, string? cursor, string? etag, CancellationToken ct) =>
        Page(account, cursor ?? "/user/repos?per_page=100&sort=full_name", etag, GitHubRestJson.Default.GitHubRepositoryArray, ct);

    public Task<GitHubRepository> GetRepositoryAsync(GitHubAccount account, string owner, string name, CancellationToken ct) =>
        Read(account, HttpMethod.Get, Repo(owner, name), null, GitHubRestJson.Default.GitHubRepository, ct);

    public Task<GitHubPage<GitHubIssue>> ListIssuesAsync(GitHubAccount account, string owner, string name, DateTimeOffset? since, string? cursor, string? etag, CancellationToken ct, string? state = null) =>
        Page(account, cursor ?? Repo(owner, name) + "/issues?state=" + IssueState(state) + "&sort=updated&direction=asc&per_page=100" + Since(since), etag, GitHubRestJson.Default.GitHubIssueArray, ct);

    public Task<GitHubIssue> GetIssueAsync(GitHubAccount account, string owner, string name, long number, CancellationToken ct) =>
        Read(account, HttpMethod.Get, Issue(owner, name, number), null, GitHubRestJson.Default.GitHubIssue, ct);

    public async Task<GitHubIssueRead> ReadIssueAsync(GitHubAccount account, string owner, string name, long number, string? etag, CancellationToken ct)
    {
        using var response = await ReadResponse(account, HttpMethod.Get, Issue(owner, name, number), null, ct, etag);
        if (response.StatusCode == HttpStatusCode.NotModified) return new(null, response.Headers.ETag?.ToString() ?? etag, true);
        try
        {
            var issue = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(ct), GitHubRestJson.Default.GitHubIssue, ct) ?? throw new GitHubApiException(GitHubErrorKind.Server);
            return new(issue, response.Headers.ETag?.ToString(), false);
        }
        catch (JsonException) { throw new GitHubApiException(GitHubErrorKind.Server); }
    }

    public Task<GitHubIssue> UpdateIssueAsync(GitHubAccount account, string owner, string name, long number, GitHubIssueUpdate update, CancellationToken ct) =>
        Read(account, HttpMethod.Patch, Issue(owner, name, number), JsonSerializer.Serialize(update, GitHubRestJson.Default.GitHubIssueUpdate), GitHubRestJson.Default.GitHubIssue, ct);

    public Task<GitHubPage<GitHubLabel>> ListLabelsAsync(GitHubAccount account, string owner, string name, string? cursor, string? etag, CancellationToken ct) =>
        Page(account, cursor ?? Repo(owner, name) + "/labels?per_page=100", etag, GitHubRestJson.Default.GitHubLabelArray, ct);

    public Task<GitHubPage<GitHubMilestone>> ListMilestonesAsync(GitHubAccount account, string owner, string name, string? cursor, string? etag, CancellationToken ct) =>
        Page(account, cursor ?? Repo(owner, name) + "/milestones?state=all&per_page=100", etag, GitHubRestJson.Default.GitHubMilestoneArray, ct);

    public Task<GitHubPage<GitHubComment>> ListCommentsAsync(GitHubAccount account, string owner, string name, DateTimeOffset? since, long? issueNumber, string? cursor, string? etag, CancellationToken ct) =>
        Page(account, cursor ?? (issueNumber is { } number ? Issue(owner, name, number) + "/comments?per_page=100" :
            Repo(owner, name) + "/issues/comments?sort=updated&direction=asc&per_page=100") + Since(since), etag, GitHubRestJson.Default.GitHubCommentArray, ct);

    public Task<GitHubComment> PostCommentAsync(GitHubAccount account, string owner, string name, long number, string body, CancellationToken ct) =>
        Read(account, HttpMethod.Post, Issue(owner, name, number) + "/comments", JsonSerializer.Serialize(new CommentBody(body), GitHubRestJson.Default.CommentBody), GitHubRestJson.Default.GitHubComment, ct);

    public Task<GitHubComment> EditCommentAsync(GitHubAccount account, string owner, string name, long commentId, string body, CancellationToken ct) =>
        Read(account, HttpMethod.Patch, Repo(owner, name) + "/issues/comments/" + Number(commentId), JsonSerializer.Serialize(new CommentBody(body), GitHubRestJson.Default.CommentBody), GitHubRestJson.Default.GitHubComment, ct);

    public Task DeleteCommentAsync(GitHubAccount account, string owner, string name, long commentId, CancellationToken ct) =>
        Write(account, HttpMethod.Delete, Repo(owner, name) + "/issues/comments/" + Number(commentId), null, ct);

    public Task AddLabelAsync(GitHubAccount account, string owner, string name, long number, string label, CancellationToken ct) =>
        Write(account, HttpMethod.Post, Issue(owner, name, number) + "/labels", JsonSerializer.Serialize(new LabelsBody([label]), GitHubRestJson.Default.LabelsBody), ct);

    public async Task RemoveLabelAsync(GitHubAccount account, string owner, string name, long number, string label, CancellationToken ct)
    {
        if (label is not "." and not "..")
        {
            await Write(account, HttpMethod.Delete, Issue(owner, name, number) + "/labels/" + Segment(label, allowSlash: true), null, ct);
            return;
        }
        // Dot-segment names cannot safely be put in a REST URL path. Use stable GraphQL IDs.
        var issue = await GetIssueAsync(account, owner, name, number, ct);
        var found = issue.Labels.FirstOrDefault(item => item.Name.Equals(label, StringComparison.OrdinalIgnoreCase));
        if (found is null) return;
        if (string.IsNullOrWhiteSpace(issue.NodeId) || string.IsNullOrWhiteSpace(found.NodeId)) throw new GitHubApiException(GitHubErrorKind.Validation);
        const string query = "mutation($labelableId: ID!, $labelIds: [ID!]!) { removeLabelsFromLabelable(input: {labelableId: $labelableId, labelIds: $labelIds}) { labelable { id } } }";
        var body = JsonSerializer.Serialize(new GraphQlLabelRemovalRequest(query, new(issue.NodeId, [found.NodeId])), GitHubGraphQlJson.Default.GraphQlLabelRemovalRequest);
        var result = await GraphQl(account, body, ct, nonessentialRead: false);
        if (result.Data?.RemoveLabelsFromLabelable?.Labelable.Id != issue.NodeId) throw new GitHubApiException(GitHubErrorKind.Server);
    }

    public Task<GitHubLabel> CreateLabelAsync(GitHubAccount account, string owner, string name, string label, CancellationToken ct) =>
        Read(account, HttpMethod.Post, Repo(owner, name) + "/labels", JsonSerializer.Serialize(new LabelBody(label, "cfd3d7"), GitHubRestJson.Default.LabelBody), GitHubRestJson.Default.GitHubLabel, ct);

    public Task SetSelfAssignmentAsync(GitHubAccount account, string owner, string name, long number, bool assign, CancellationToken ct) =>
        Write(account, assign ? HttpMethod.Post : HttpMethod.Delete, Issue(owner, name, number) + "/assignees",
            JsonSerializer.Serialize(new AssigneesBody([account.Login]), GitHubRestJson.Default.AssigneesBody), ct);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<GitHubPullRequestRef>>> GetClosingPullRequestsAsync(GitHubAccount account, IReadOnlyList<string> issueNodeIds, CancellationToken ct)
    {
        if (issueNodeIds.Count == 0) return new Dictionary<string, IReadOnlyList<GitHubPullRequestRef>>();
        if (issueNodeIds.Count > 50 || issueNodeIds.Any(string.IsNullOrWhiteSpace)) throw new GitHubApiException(GitHubErrorKind.Validation);
        const string query = "query($ids: [ID!]!) { nodes(ids: $ids) { ... on Issue { id closedByPullRequestsReferences(first: 10, includeClosedPrs: true) { nodes { id fullDatabaseId number title url state isDraft merged reviewDecision repository { nameWithOwner } } } } } }";
        var body = JsonSerializer.Serialize(new GraphQlRequest(query, new(issueNodeIds)), GitHubGraphQlJson.Default.GraphQlRequest);
        var result = await GraphQl(account, body, ct, nonessentialRead: true);
        if (result.Data?.Nodes is null) throw new GitHubApiException(GitHubErrorKind.Server);
        var mapped = new Dictionary<string, IReadOnlyList<GitHubPullRequestRef>>(StringComparer.Ordinal);
        foreach (var issue in result.Data.Nodes)
        {
            if (issue?.Id is null || !issueNodeIds.Contains(issue.Id) || issue.ClosedByPullRequestsReferences?.Nodes is null) continue;
            mapped[issue.Id] = issue.ClosedByPullRequestsReferences.Nodes.Select(pr => new GitHubPullRequestRef(
                pr.FullDatabaseId is > 0 ? pr.FullDatabaseId.Value : throw new GitHubApiException(GitHubErrorKind.Server),
                pr.Id, pr.Repository.NameWithOwner, pr.Number, pr.Title, pr.Url, pr.State, pr.IsDraft, pr.Merged, pr.ReviewDecision)).ToArray();
        }
        return mapped;
    }

    private async Task<GraphQlResponse> GraphQl(GitHubAccount account, string body, CancellationToken ct, bool nonessentialRead)
    {
        using var response = await Request(account, HttpMethod.Post, "/graphql", body, null, ct, nonessentialRead);
        GraphQlResponse? result;
        try { result = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(ct), GitHubGraphQlJson.Default.GraphQlResponse, ct); }
        catch (JsonException) { throw new GitHubApiException(GitHubErrorKind.Server); }
        if (result?.Errors is { Length: > 0 } errors)
        {
            var kind = errors[0].Type switch { "FORBIDDEN" => GitHubErrorKind.Forbidden, "NOT_FOUND" => GitHubErrorKind.NotFound, "RATE_LIMITED" => GitHubErrorKind.RateLimited, "UNAUTHORIZED" => GitHubErrorKind.Unauthorized, _ => GitHubErrorKind.Server };
            if (kind == GitHubErrorKind.RateLimited)
            {
                await _gate.WaitAsync(ct);
                try { _rateLimiter.Block(null); } finally { _gate.Release(); }
            }
            throw new GitHubApiException(kind);
        }
        return result?.Data is not null ? result : throw new GitHubApiException(GitHubErrorKind.Server);
    }

    private static string Repo(string owner, string name) => "/repos/" + Segment(owner) + "/" + Segment(name);
    private static string Issue(string owner, string name, long number) => Repo(owner, name) + "/issues/" + Number(number);
    private static string Number(long number) => number > 0 ? number.ToString(CultureInfo.InvariantCulture) : throw new GitHubApiException(GitHubErrorKind.Validation);
    private static string IssueState(string? state) => state is null or "all" ? "all" : state is "open" or "closed" ? state : throw new GitHubApiException(GitHubErrorKind.Validation);
    private static string Since(DateTimeOffset? since) => since is { } value ? "&since=" + Uri.EscapeDataString(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) : "";

    private static string Segment(string text, bool allowSlash = false)
    {
        if (string.IsNullOrEmpty(text) || text is "." or ".." || text.Any(char.IsControl) || (!allowSlash && text.IndexOfAny(['/', '\\']) >= 0))
            throw new GitHubApiException(GitHubErrorKind.Validation);
        return Uri.EscapeDataString(text);
    }

    private async Task<HttpResponseMessage> Request(GitHubAccount account, HttpMethod method, string path, string? body, string? etag, CancellationToken ct, bool nonessentialRead = false)
    {
        if (!Uri.TryCreate(path.StartsWith('/') ? "https://api.github.com" + path : path, UriKind.Absolute, out var endpoint))
            throw new GitHubApiException(GitHubErrorKind.Validation);
        await _gate.WaitAsync(ct);
        try
        {
            _rateLimiter.Check(nonessentialRead);
            var write = method != HttpMethod.Get && method != HttpMethod.Head;
            var wait = _nextWrite - _clock.GetUtcNow();
            if (write && wait > TimeSpan.Zero) await Task.Delay(wait, _clock, ct);
            try
            {
                var response = await _transport.SendAsync(account, method, endpoint, body, etag, ct);
                _rateLimiter.Observe(response);
                return response;
            }
            catch (GitHubApiException error) when (error.Kind == GitHubErrorKind.RateLimited)
            {
                _rateLimiter.Block(error.RetryAtUtc);
                throw;
            }
            finally { if (write) _nextWrite = _clock.GetUtcNow().AddSeconds(1); }
        }
        finally { _gate.Release(); }
    }

    private async Task<T> Read<T>(GitHubAccount account, HttpMethod method, string path, string? body, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await ReadResponse(account, method, path, body, ct);
        try { return await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(ct), type, ct) ?? throw new GitHubApiException(GitHubErrorKind.Server); }
        catch (JsonException) { throw new GitHubApiException(GitHubErrorKind.Server); }
    }

    private async Task<HttpResponseMessage> ReadResponse(GitHubAccount account, HttpMethod method, string path, string? body, CancellationToken ct, string? etag = null)
    {
        try { return await Request(account, method, path, body, etag, ct); }
        catch (GitHubApiException error) when (method == HttpMethod.Get && error.Kind == GitHubErrorKind.Moved && error.MovedToUrl is not null)
        {
            // Follow one verified read redirect only. A write is never forwarded automatically.
            return await Request(account, HttpMethod.Get, error.MovedToUrl, null, null, ct);
        }
    }

    private async Task Write(GitHubAccount account, HttpMethod method, string path, string? body, CancellationToken ct)
    {
        using var response = await Request(account, method, path, body, null, ct);
    }

    private async Task<GitHubPage<T>> Page<T>(GitHubAccount account, string path, string? etag, JsonTypeInfo<T[]> type, CancellationToken ct)
    {
        using var response = await Request(account, HttpMethod.Get, path, null, etag, ct, nonessentialRead: true);
        var pagination = new Dictionary<string, string>(StringComparer.Ordinal);
        if (response.Headers.TryGetValues("Link", out var links))
        {
            MatchCollection matches;
            try { matches = Regex.Matches(string.Join(",", links), "<([^>]+)>;\\s*rel=\"(next|prev|last)\"", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1)); }
            catch (RegexMatchTimeoutException) { throw new GitHubApiException(GitHubErrorKind.Validation); }
            foreach (Match match in matches)
            {
                if (!Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "api.github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                    throw new GitHubApiException(GitHubErrorKind.Validation);
                pagination[match.Groups[2].Value] = uri.AbsoluteUri;
            }
        }
        var next = pagination.GetValueOrDefault("next");
        var previous = pagination.GetValueOrDefault("prev");
        var last = pagination.GetValueOrDefault("last");
        long? remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && long.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var count) ? count : null;
        DateTimeOffset? reset = null;
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out values) && long.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 253402300799)
            reset = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (response.StatusCode == HttpStatusCode.NotModified) return new([], next, response.Headers.ETag?.ToString() ?? etag, true, remaining, reset, previous, last);
        try
        {
            var items = await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(ct), type, ct) ?? throw new GitHubApiException(GitHubErrorKind.Server);
            return new(items, next, response.Headers.ETag?.ToString(), false, remaining, reset, previous, last);
        }
        catch (JsonException) { throw new GitHubApiException(GitHubErrorKind.Server); }
    }

    public void Dispose() { _transport.Dispose(); _gate.Dispose(); }
}

internal sealed record CommentBody(string Body);
internal sealed record LabelsBody(IReadOnlyList<string> Labels);
internal sealed record AssigneesBody(IReadOnlyList<string> Assignees);
internal sealed record LabelBody(string Name, string Color);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GitHubRepository))]
[JsonSerializable(typeof(GitHubRepository[]))]
[JsonSerializable(typeof(GitHubIssue))]
[JsonSerializable(typeof(GitHubIssue[]))]
[JsonSerializable(typeof(GitHubIssueUpdate))]
[JsonSerializable(typeof(GitHubLabel))]
[JsonSerializable(typeof(GitHubLabel[]))]
[JsonSerializable(typeof(GitHubMilestone[]))]
[JsonSerializable(typeof(GitHubComment))]
[JsonSerializable(typeof(GitHubComment[]))]
[JsonSerializable(typeof(CommentBody))]
[JsonSerializable(typeof(LabelsBody))]
[JsonSerializable(typeof(AssigneesBody))]
[JsonSerializable(typeof(LabelBody))]
internal partial class GitHubRestJson : JsonSerializerContext;

internal sealed record GraphQlRequest(string Query, GraphQlVariables Variables);
internal sealed record GraphQlVariables(IReadOnlyList<string> Ids);
internal sealed record GraphQlLabelRemovalRequest(string Query, GraphQlLabelRemovalVariables Variables);
internal sealed record GraphQlLabelRemovalVariables(string LabelableId, IReadOnlyList<string> LabelIds);
internal sealed record GraphQlResponse(GraphQlData? Data, GraphQlError[]? Errors);
internal sealed record GraphQlError(string? Type);
internal sealed record GraphQlData(GraphQlIssue?[]? Nodes, GraphQlLabelRemoval? RemoveLabelsFromLabelable);
internal sealed record GraphQlLabelRemoval(GraphQlLabelable Labelable);
internal sealed record GraphQlLabelable(string Id);
internal sealed record GraphQlIssue(string? Id, GraphQlPullRequests? ClosedByPullRequestsReferences);
internal sealed record GraphQlPullRequests(GraphQlPullRequest[] Nodes);
internal sealed record GraphQlPullRequest(string Id, [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long? FullDatabaseId, long Number, string Title, string Url, string State, bool IsDraft, bool Merged, string? ReviewDecision, GraphQlRepository Repository);
internal sealed record GraphQlRepository(string NameWithOwner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GraphQlRequest))]
[JsonSerializable(typeof(GraphQlResponse))]
[JsonSerializable(typeof(GraphQlLabelRemovalRequest))]
internal partial class GitHubGraphQlJson : JsonSerializerContext;
