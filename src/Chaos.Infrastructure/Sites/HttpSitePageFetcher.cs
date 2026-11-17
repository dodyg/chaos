using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Sites;

/// <summary>Downloads a web page: http(s) only, a 5 MiB cap, a 30 s timeout, at most 5 redirects.</summary>
public sealed class HttpSitePageFetcher : ISitePageFetcher, IDisposable
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public HttpSitePageFetcher()
        : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, AutomaticDecompression = DecompressionMethods.All }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan }, true)
    {
    }

    public HttpSitePageFetcher(HttpClient http, bool ownsClient = false)
    {
        _http = http;
        _ownsClient = ownsClient;
    }

    public async Task<FetchedPage> FetchAsync(Uri address, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", 0.5));
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Chaos/1.0)");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new SiteReadException(response.StatusCode switch
                {
                    HttpStatusCode.NotFound or HttpStatusCode.Gone => "The page was not found.",
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The site refused access to the page.",
                    _ => $"The site answered {(int)response.StatusCode}.",
                });
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "text/html";
            if (mediaType is not ("text/html" or "application/xhtml+xml" or "text/plain"))
            {
                throw new SiteReadException("That address is not a web page Chaos can read.");
            }

            if (response.Content.Headers.ContentLength is { } length && length > MaxBytes)
            {
                throw new SiteReadException("The page is larger than 5 MB, which is more than Chaos will download.");
            }

            var html = await ReadCappedAsync(response.Content, timeout.Token);
            var final = response.RequestMessage?.RequestUri ?? address;
            if (mediaType == "text/plain")
            {
                return new(final, "", html.Trim());
            }

            var (title, text) = HtmlText.Extract(html);
            if (text.Length < 80)
            {
                throw new SiteReadException("The page has almost no readable text. It may need JavaScript to load.");
            }

            return new(final, title, text);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SiteReadException("The site did not answer within 30 seconds.");
        }
        catch (HttpRequestException)
        {
            throw new SiteReadException("Chaos could not reach that address. Check the connection and the address.");
        }
    }

    private static async Task<string> ReadCappedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new SiteReadException("The page is larger than 5 MB, which is more than Chaos will download.");
            }

            buffer.Write(chunk, 0, read);
        }

        var encoding = Encoding.UTF8;
        if (content.Headers.ContentType?.CharSet is { } charset)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // Unknown charset name: UTF-8 is the best guess.
            }
        }

        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
