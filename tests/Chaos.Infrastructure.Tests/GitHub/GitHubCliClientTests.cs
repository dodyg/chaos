using System.Net;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.GitHub;

namespace Chaos.Infrastructure.Tests.GitHub;

public sealed class GitHubCliClientTests
{
    private sealed class Runner : IGitHubCliRunner
    {
        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct) =>
            Task.FromResult(arguments[0] == "--version" ? "gh version 2.103.3" : arguments[0] == "config" ? "person" : "fixture-a");
    }

    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri.AbsolutePath == "/user") return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":1,\"login\":\"person\"}") };
            return respond(request, request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));
        }
    }

    [Test]
    public async Task Comments_ParseIssueIdentityAndPaginationTailLinks()
    {
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"id\":3000000000,\"body\":\"text\",\"issue_url\":\"https://api.github.com/repos/person/repo/issues/4\"}]") };
            response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/page2>; rel=\"next\", <https://api.github.com/page0>; rel=\"prev\", <https://api.github.com/page9>; rel=\"last\"");
            return response;
        });
        using var client = new GitHubCliClient(new Runner(), handler, TimeProvider.System);
        var page = await client.ListCommentsAsync(new(1, "person"), "person", "repo", null, 4, null, null, CancellationToken.None);
        await Assert.That(page.PreviousCursor).IsEqualTo("https://api.github.com/page0");
        await Assert.That(page.LastCursor).IsEqualTo("https://api.github.com/page9");
        await Assert.That(page.Items.Single().IssueUrl).IsEqualTo("https://api.github.com/repos/person/repo/issues/4");
    }

    [Test]
    [Arguments("prev")]
    [Arguments("last")]
    public async Task Comments_RejectUnsafeTailDestination(string relation)
    {
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            response.Headers.TryAddWithoutValidation("Link", "<https://example.com/private>; rel=\"" + relation + "\"");
            return response;
        });
        using var client = new GitHubCliClient(new Runner(), handler, TimeProvider.System);
        await Assert.That(async () => await client.ListCommentsAsync(new(1, "person"), "person", "repo", null, 4, null, null, CancellationToken.None)).Throws<GitHubApiException>();
    }

    [Test]
    public async Task GetRepositoryAsync_Moved_FollowsOneApiReadRedirect()
    {
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/repos/person/old")
            {
                var moved = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                moved.Headers.Location = new Uri("new", UriKind.Relative);
                return moved;
            }
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":2,\"name\":\"new\",\"full_name\":\"person/new\"}") };
        });
        using var client = new GitHubCliClient(new Runner(), handler, TimeProvider.System);
        var repository = await client.GetRepositoryAsync(new(1, "person"), "person", "old", CancellationToken.None);
        await Assert.That(repository.FullName).IsEqualTo("person/new");
        await Assert.That(handler.Paths.Count).IsEqualTo(4);
        await Assert.That(handler.Paths[^1]).IsEqualTo("/repos/person/new");
    }

    [Test]
    public async Task RemoveLabelAsync_DotSegment_UsesIdsInsteadOfNormalizingUrlPath()
    {
        var handler = new Handler((request, body) =>
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":3,\"node_id\":\"issue-node\",\"labels\":[{\"id\":4,\"node_id\":\"label-node\",\"name\":\"..\",\"color\":\"ffffff\"}]}") };
            using var json = JsonDocument.Parse(body!);
            if (json.RootElement.GetProperty("variables").GetProperty("labelIds")[0].GetString() != "label-node") throw new InvalidOperationException("Wrong label identity");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":{\"removeLabelsFromLabelable\":{\"labelable\":{\"id\":\"issue-node\"}}}}") };
        });
        using var client = new GitHubCliClient(new Runner(), handler, TimeProvider.System);
        await client.RemoveLabelAsync(new(1, "person"), "person", "repo", 1, "..", CancellationToken.None);
        await Assert.That(handler.Paths.SequenceEqual(new[] { "/user", "/repos/person/repo/issues/1", "/user", "/graphql" })).IsTrue();
    }

    [Test]
    public async Task GetClosingPullRequestsAsync_GraphQlBatch_MapsRepositoryAndReviewState()
    {
        var handler = new Handler((_, body) =>
        {
            using var request = JsonDocument.Parse(body!);
            if (request.RootElement.GetProperty("variables").GetProperty("ids")[0].GetString() != "issue-node") throw new InvalidOperationException("Missing GraphQL variables");
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                {"data":{"nodes":[{"id":"issue-node","closedByPullRequestsReferences":{"nodes":[{"id":"pr-node","fullDatabaseId":"3000000000","number":3,"title":"Fix","url":"https://github.com/person/repo/pull/3","state":"OPEN","isDraft":false,"merged":false,"reviewDecision":"APPROVED","repository":{"nameWithOwner":"person/repo"}}]}}]}}
                """) };
        });
        using var client = new GitHubCliClient(new Runner(), handler, TimeProvider.System);
        var result = await client.GetClosingPullRequestsAsync(new(1, "person"), ["issue-node"], CancellationToken.None);
        var pr = result["issue-node"].Single();
        await Assert.That(pr.Id).IsEqualTo(3000000000L);
        await Assert.That(pr.RepositoryFullName).IsEqualTo("person/repo");
        await Assert.That(pr.ReviewDecision).IsEqualTo("APPROVED");
        await Assert.That(async () => await client.GetClosingPullRequestsAsync(new(1, "person"), Enumerable.Repeat("node", 51).ToArray(), CancellationToken.None)).Throws<GitHubApiException>();
        await Assert.That(handler.Paths.Count).IsEqualTo(2);
    }

    [Test]
    public async Task GetClosingPullRequestsAsync_PartialErrors_FailsWithoutExposingMessage()
    {
        var handler = new Handler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent("{\"errors\":[{\"type\":\"FORBIDDEN\",\"message\":\"private issue text\"}],\"data\":{\"nodes\":[]}}") });
        using var client = new GitHubCliClient(new Runner(), handler, TimeProvider.System);
        await Assert.That(async () => await client.GetClosingPullRequestsAsync(new(1, "person"), ["issue-node"], CancellationToken.None))
            .Throws<GitHubApiException>().And.HasMessageContaining("Forbidden");
    }

    [Test]
    public async Task ListRepositoriesAsync_ConditionalPagination_PreservesServerCursorAndEtag()
    {
        var handler = new Handler((request, _) =>
        {
            if (request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal))
            {
                if (request.Headers.IfNoneMatch.Single().Tag != "\"fixture-etag\"") throw new InvalidOperationException("Conditional request lost ETag");
                return new(HttpStatusCode.NotModified);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"id\":2,\"name\":\"repo\",\"full_name\":\"person/repo\",\"owner\":{\"id\":1,\"login\":\"person\"},\"private\":true}]") };
            response.Headers.ETag = new("\"fixture-etag\"");
            response.Headers.Add("Link", "<https://api.github.com/user/repos?page=2>; rel=\"next\"");
            return response;
        });
        using var client = new GitHubCliClient(new Runner(), handler, TimeProvider.System);
        var account = await client.GetSetupAccountAsync(CancellationToken.None);
        var first = await client.ListRepositoriesAsync(account, null, null, CancellationToken.None);
        await Assert.That(first.Items.Single().Private).IsTrue();
        await Assert.That(first.NextCursor).IsEqualTo("https://api.github.com/user/repos?page=2");
        var second = await client.ListRepositoriesAsync(account, first.NextCursor, first.Etag, CancellationToken.None);
        await Assert.That(second.NotModified).IsTrue();
        await Assert.That(second.Items.Count).IsEqualTo(0);
        await Assert.That(second.Etag).IsEqualTo("\"fixture-etag\"");
        await Assert.That(client.RequestCount).IsEqualTo(5L);
        await Assert.That(client.NotModifiedCount).IsEqualTo(1L);
    }

    [Test]
    public async Task UpdateIssueAsync_OnlyBodyChanged_SendsOnlyBodyAndPinnedHeaders()
    {
        var handler = new Handler((request, body) =>
        {
            using var json = JsonDocument.Parse(body!);
            if (request.Method != HttpMethod.Patch || json.RootElement.EnumerateObject().Count() != 1 || json.RootElement.GetProperty("body").GetString() != "new body" ||
                request.Headers.GetValues("X-GitHub-Api-Version").Single() != "2026-03-10") throw new InvalidOperationException("Unexpected update shape");
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":3,\"number\":1,\"body\":\"new body\"}") };
        });
        using var client = new GitHubCliClient(new Runner(), handler, TimeProvider.System);
        var issue = await client.UpdateIssueAsync(new(1, "person"), "person", "repo", 1, new(Body: "new body"), CancellationToken.None);
        await Assert.That(issue.Body).IsEqualTo("new body");
        await Assert.That(handler.Paths.SequenceEqual(new[] { "/user", "/repos/person/repo/issues/1" })).IsTrue();
    }

    [Test]
    public async Task ListLabelsAsync_UntrustedNextHost_IsRejected()
    {
        var handler = new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
            response.Headers.Add("Link", "<https://example.invalid/labels>; rel=\"next\"");
            return response;
        });
        using var client = new GitHubCliClient(new Runner(), handler, TimeProvider.System);
        await Assert.That(async () => await client.ListLabelsAsync(new(1, "person"), "person", "repo", null, null, CancellationToken.None)).Throws<GitHubApiException>();
        await Assert.That(handler.Paths.Count).IsEqualTo(2);
    }

    [Test]
    public async Task SetSelfAssignmentAsync_Assign_OnlySendsConnectedLogin()
    {
        string? posted = null;
        var handler = new Handler((_, body) => { posted = body; return new(HttpStatusCode.Created) { Content = new StringContent("{}") }; });
        using var client = new GitHubCliClient(new Runner(), handler, TimeProvider.System);
        await client.SetSelfAssignmentAsync(new(1, "person"), "person", "repo", 1, true, CancellationToken.None);
        using var json = JsonDocument.Parse(posted!);
        await Assert.That(json.RootElement.GetProperty("assignees").EnumerateArray().Single().GetString()).IsEqualTo("person");
    }
}
