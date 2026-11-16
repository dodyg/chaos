using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Calendar;

/// <summary>
/// Downloads an ICS feed (PLAN2 §10.2): HTTPS only, a conditional GET with ETag and If-Modified-Since,
/// a 10 MiB response cap, a 30 s timeout and at most 3 redirects, every hop on HTTPS. The address is a
/// credential, so no message or exception text from here ever contains it.
/// </summary>
public sealed class CalendarFeedClient : ICalendarFeedClient, IDisposable
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxRedirects = 3;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <summary>The production client: automatic redirects are off so every hop is checked here.</summary>
    public CalendarFeedClient()
        : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan }, true)
    {
    }

    /// <summary>Tests pass a client over a fake handler; it must not follow redirects itself.</summary>
    public CalendarFeedClient(HttpClient http, bool ownsClient = false)
    {
        _http = http;
        _ownsClient = ownsClient;
    }

    public async Task<FeedFetchResult> FetchAsync(Uri address, string? etag, string? lastModified, CancellationToken ct)
    {
        if (address.Scheme != Uri.UriSchemeHttps)
        {
            throw new CalendarFeedException("Calendar feeds must use an https:// address.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        var current = address;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/calendar"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", 0.5));
                if (!string.IsNullOrEmpty(etag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", etag);
                }

                if (!string.IsNullOrEmpty(lastModified))
                {
                    request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
                }

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return new FeedFetchResult(FeedFetchStatus.NotModified, null, etag, lastModified);
                }

                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop >= MaxRedirects)
                    {
                        throw new CalendarFeedException("The calendar address redirects too many times.");
                    }

                    var target = response.Headers.Location is { } location ? (location.IsAbsoluteUri ? location : new Uri(current, location)) : null;
                    if (target is null || target.Scheme != Uri.UriSchemeHttps)
                    {
                        throw new CalendarFeedException("The calendar address redirected somewhere that is not https.");
                    }

                    current = target;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new CalendarFeedException(response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The calendar refused access. The secret address may have been reset.",
                        HttpStatusCode.NotFound or HttpStatusCode.Gone => "The calendar address was not found. It may have been reset or removed.",
                        _ => $"The calendar server answered {(int)response.StatusCode}.",
                    });
                }

                if (response.Content.Headers.ContentLength is { } length && length > MaxBytes)
                {
                    throw new CalendarFeedException("The calendar is larger than 10 MB, which is more than Chaos will download.");
                }

                var text = await ReadCappedAsync(response.Content, timeout.Token);
                return new FeedFetchResult(
                    FeedFetchStatus.Content,
                    text,
                    response.Headers.ETag?.ToString(),
                    response.Content.Headers.LastModified?.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CalendarFeedException("The calendar did not answer within 30 seconds.");
        }
        catch (HttpRequestException)
        {
            // The inner message can include the address; say only what a person can act on.
            throw new CalendarFeedException("Chaos could not reach the calendar. Check the connection and try again.");
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
                throw new CalendarFeedException("The calendar is larger than 10 MB, which is more than Chaos will download.");
            }

            buffer.Write(chunk, 0, read);
        }

        return new UTF8Encoding(false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
