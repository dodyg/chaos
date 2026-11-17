using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Updates;

/// <summary>
/// Reads the newest release of dodyg/chaos from the public GitHub API (no sign-in, no personal data sent).
/// Drafts and pre-releases are never returned by this endpoint.
/// </summary>
public sealed class GitHubReleaseFeed : IReleaseFeed, IDisposable
{
    public static readonly Uri LatestAddress = new("https://api.github.com/repos/dodyg/chaos/releases/latest");
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public GitHubReleaseFeed() : this(new HttpClient { Timeout = Timeout }, true)
    {
    }

    public GitHubReleaseFeed(HttpClient http, bool ownsClient = false)
    {
        _http = http;
        _ownsClient = ownsClient;
    }

    public async Task<ReleaseInfo?> LatestAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestAddress);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Chaos", "1"));
            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                throw new UpdateCheckException("GitHub is limiting requests right now. Try again in a little while.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateCheckException($"GitHub answered {(int)response.StatusCode}. Try again later.");
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (!ReleaseVersions.TryParse(tag, out var version))
            {
                throw new UpdateCheckException("The newest release has a name Chaos cannot read.");
            }

            var url = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;
            var published = root.TryGetProperty("published_at", out var publishedElement) ? publishedElement.GetString() : null;
            return new ReleaseInfo(tag!, version.ToString(3), url ?? "https://github.com/dodyg/chaos/releases", published);
        }
        catch (HttpRequestException error)
        {
            throw new UpdateCheckException("Could not reach GitHub. Check the internet connection.", error);
        }
        catch (TaskCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new UpdateCheckException("GitHub did not answer in time.", error);
        }
        catch (JsonException error)
        {
            throw new UpdateCheckException("GitHub sent an answer Chaos cannot read.", error);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
