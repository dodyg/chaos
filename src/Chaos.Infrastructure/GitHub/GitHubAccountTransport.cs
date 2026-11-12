using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.GitHub;

/// <summary>Native-only credential source; credentials never enter application/RPC contracts.</summary>
public interface IGitHubCredentialSource
{
    Task<string> ReadAsync(string login, CancellationToken ct);
}

/// <summary>Verifies each credential snapshot before sending issue data using that exact snapshot.</summary>
public sealed class GitHubAccountTransport : IDisposable
{
    private readonly HttpClient _client;
    private readonly IGitHubCredentialSource _credentials;
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private long _requestCount;
    private long _notModifiedCount;
    public long RequestCount => Interlocked.Read(ref _requestCount);
    public long NotModifiedCount => Interlocked.Read(ref _notModifiedCount);

    public GitHubAccountTransport(IGitHubCredentialSource credentials)
        : this(credentials, new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { }

    /// <summary>Injected handlers are for deterministic tests and must not follow redirects.</summary>
    public GitHubAccountTransport(IGitHubCredentialSource credentials, HttpMessageHandler handler)
    {
        _credentials = credentials;
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Explicit setup only: establishes the numeric identity for the selected CLI login.</summary>
    public async Task<GitHubAccount> GetAccountAsync(string login, CancellationToken ct)
    {
        try
        {
            var credential = await ReadCredentialAsync(login, ct);
            var identity = await ReadIdentityAsync(credential, ct);
            if (identity is null || identity.Id <= 0 || !string.Equals(identity.Login, login, StringComparison.OrdinalIgnoreCase))
                throw new GitHubApiException(GitHubErrorKind.AccountMismatch);
            return new(identity.Id, identity.Login!);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new GitHubApiException(GitHubErrorKind.Network); }
        catch (HttpRequestException) { throw new GitHubApiException(GitHubErrorKind.Network); }
        catch (IOException) { throw new GitHubApiException(GitHubErrorKind.Network); }
        catch (JsonException) { throw new GitHubApiException(GitHubErrorKind.Server); }
        catch (FormatException) { throw new GitHubApiException(GitHubErrorKind.Unauthorized); }
    }

    public async Task<HttpResponseMessage> SendAsync(GitHubAccount account, HttpMethod method, Uri endpoint,
        string? jsonBody, string? etag, CancellationToken ct)
    {
        Validate(endpoint);
        if (jsonBody is not null && System.Text.Encoding.UTF8.GetByteCount(jsonBody) > 1024 * 1024)
            throw new GitHubApiException(GitHubErrorKind.Validation);
        if (account.Id <= 0 || string.IsNullOrWhiteSpace(account.Login)) throw new GitHubApiException(GitHubErrorKind.AccountMismatch);
        try
        {
            // Capture once. An external CLI account switch cannot alter this request's identity.
            var credential = await ReadCredentialAsync(account.Login, ct);
            var identity = await ReadIdentityAsync(credential, ct);
            if (identity?.Id != account.Id) throw new GitHubApiException(GitHubErrorKind.AccountMismatch);

            using var request = Create(method, endpoint, credential);
            if (jsonBody is not null) request.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
            if (etag is not null) request.Headers.IfNoneMatch.ParseAdd(etag);
            var response = await SendBoundedAsync(request, ct);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotModified)
            {
                try { RequireSuccess(response, endpoint); }
                finally { response.Dispose(); }
            }
            return response;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new GitHubApiException(GitHubErrorKind.Network); }
        catch (HttpRequestException) { throw new GitHubApiException(GitHubErrorKind.Network); }
        catch (IOException) { throw new GitHubApiException(GitHubErrorKind.Network); }
        catch (JsonException) { throw new GitHubApiException(GitHubErrorKind.Server); }
        catch (FormatException) { throw new GitHubApiException(GitHubErrorKind.Validation); }
    }

    private async Task<string> ReadCredentialAsync(string login, CancellationToken ct)
    {
        var credential = await _credentials.ReadAsync(login, ct);
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 16384 || credential.Any(character => character <= 32 || character >= 127))
            throw new GitHubApiException(GitHubErrorKind.Unauthorized);
        return credential;
    }

    private async Task<Identity?> ReadIdentityAsync(string credential, CancellationToken ct)
    {
        using var check = Create(HttpMethod.Get, new Uri("https://api.github.com/user"), credential);
        using var response = await SendBoundedAsync(check, ct);
        RequireSuccess(response, check.RequestUri);
        if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"))
            throw new GitHubApiException(GitHubErrorKind.RateLimited, RetryInstant(response));
        return await JsonSerializer.DeserializeAsync(await response.Content.ReadAsStreamAsync(ct), GitHubIdentityJson.Default.Identity, ct);
    }

    private static void Validate(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "https" || endpoint.Host != "api.github.com" ||
            !endpoint.IsDefaultPort || endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
            throw new GitHubApiException(GitHubErrorKind.Validation);
    }

    private static HttpRequestMessage Create(HttpMethod method, Uri endpoint, string credential)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.UserAgent.ParseAdd("Chaos-GitHub/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        return request;
    }

    private async Task<HttpResponseMessage> SendBoundedAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        Interlocked.Increment(ref _requestCount);
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotModified) Interlocked.Increment(ref _notModifiedCount);
        try
        {
            if (response.Content.Headers.ContentLength > MaxResponseBytes) throw new GitHubApiException(GitHubErrorKind.Server);
            await response.Content.LoadIntoBufferAsync(MaxResponseBytes, timeout.Token);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static void RequireSuccess(HttpResponseMessage response, Uri? source = null)
    {
        if (response.IsSuccessStatusCode) return;
        var status = (int)response.StatusCode;
        var limited = status is 403 or 429 && (response.Headers.Contains("Retry-After") ||
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"));
        string? movedTo = null;
        if (status is >= 300 and < 400 && response.Headers.Location is { } location)
        {
            if (!Uri.TryCreate(source ?? new Uri("https://api.github.com"), location, out var target)) throw new GitHubApiException(GitHubErrorKind.Validation);
            Validate(target);
            movedTo = target.AbsoluteUri;
        }
        throw new GitHubApiException(limited || status == 429 ? GitHubErrorKind.RateLimited : status switch
        {
            401 => GitHubErrorKind.Unauthorized,
            403 => GitHubErrorKind.Forbidden,
            404 => GitHubErrorKind.NotFound,
            410 => GitHubErrorKind.Gone,
            >= 300 and < 400 => GitHubErrorKind.Moved,
            422 => GitHubErrorKind.Validation,
            _ => GitHubErrorKind.Server,
        }, RetryInstant(response), movedTo);
    }

    private static DateTimeOffset? RetryInstant(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Date is { } date) return date;
        if (response.Headers.RetryAfter?.Delta is { } delay && delay >= TimeSpan.Zero && delay < TimeSpan.FromDays(365)) return DateTimeOffset.UtcNow.Add(delay);
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var seconds) && seconds is >= 0 and <= 253402300799)
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return null;
    }

    public void Dispose() => _client.Dispose();
}

public sealed record Identity([property: JsonPropertyName("id")] long Id, [property: JsonPropertyName("login")] string? Login = null);

[JsonSerializable(typeof(Identity))]
internal partial class GitHubIdentityJson : JsonSerializerContext;
