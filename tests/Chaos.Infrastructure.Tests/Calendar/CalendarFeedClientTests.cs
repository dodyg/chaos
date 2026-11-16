using System.Net;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Calendar;

namespace Chaos.Infrastructure.Tests.Calendar;

/// <summary>The HTTP rules of PLAN2 §10.2 over a fake handler: HTTPS only, redirects, conditional GET, size, and no address in errors.</summary>
public sealed class CalendarFeedClientTests
{
    private static readonly Uri Address = new("https://calendar.example.org/secret-token-123/basic.ics");

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static (CalendarFeedClient Client, Handler Handler) ClientOf(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new Handler(respond);
        return (new CalendarFeedClient(new HttpClient(handler)), handler);
    }

    private static HttpResponseMessage Ok(string body, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        if (etag is not null)
        {
            response.Headers.TryAddWithoutValidation("ETag", etag);
        }

        return response;
    }

    private static HttpResponseMessage Redirect(string to) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    [Test]
    public async Task Fetch_ReturnsTheTextAndValidators()
    {
        var (client, _) = ClientOf(_ => Ok("BEGIN:VCALENDAR", "\"v7\""));

        var result = await client.FetchAsync(Address, null, null, CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(FeedFetchStatus.Content);
        await Assert.That(result.Text).IsEqualTo("BEGIN:VCALENDAR");
        await Assert.That(result.ETag).IsEqualTo("\"v7\"");
    }

    [Test]
    public async Task Fetch_SendsConditionalHeaders_AndTreats304AsNotModified()
    {
        var (client, handler) = ClientOf(_ => new HttpResponseMessage(HttpStatusCode.NotModified));

        var result = await client.FetchAsync(Address, "\"v7\"", "Mon, 16 Nov 2026 08:00:00 GMT", CancellationToken.None);

        await Assert.That(result.Status).IsEqualTo(FeedFetchStatus.NotModified);
        await Assert.That(handler.Requests.Single().Headers.GetValues("If-None-Match").Single()).IsEqualTo("\"v7\"");
        await Assert.That(handler.Requests.Single().Headers.GetValues("If-Modified-Since").Single()).IsEqualTo("Mon, 16 Nov 2026 08:00:00 GMT");
    }

    [Test]
    public async Task Fetch_RefusesPlainHttp_WithoutAnyRequest()
    {
        var (client, handler) = ClientOf(_ => Ok("x"));

        await Assert.That(async () => await client.FetchAsync(new Uri("http://calendar.example.org/a.ics"), null, null, CancellationToken.None)).Throws<CalendarFeedException>();
        await Assert.That(handler.Requests).IsEmpty();
    }

    [Test]
    public async Task Fetch_FollowsHttpsRedirects_UpToThree()
    {
        var hops = 0;
        var (client, handler) = ClientOf(request => ++hops <= 3 ? Redirect($"https://calendar.example.org/hop{hops}.ics") : Ok("done"));

        var result = await client.FetchAsync(Address, null, null, CancellationToken.None);

        await Assert.That(result.Text).IsEqualTo("done");
        await Assert.That(handler.Requests.Count).IsEqualTo(4);
    }

    [Test]
    public async Task Fetch_StopsAfterThreeRedirects()
    {
        var (client, _) = ClientOf(_ => Redirect("https://calendar.example.org/loop.ics"));

        var error = await Assert.ThrowsAsync<CalendarFeedException>(async () => await client.FetchAsync(Address, null, null, CancellationToken.None));

        await Assert.That(error!.Message).Contains("redirects");
    }

    [Test]
    public async Task Fetch_RefusesARedirectToHttp()
    {
        var (client, handler) = ClientOf(_ => Redirect("http://calendar.example.org/plain.ics"));

        var error = await Assert.ThrowsAsync<CalendarFeedException>(async () => await client.FetchAsync(Address, null, null, CancellationToken.None));

        await Assert.That(error!.Message).Contains("https");
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Fetch_RefusesAResponseOverTenMebibytes_ByHeaderAndByStreaming()
    {
        var (declared, _) = ClientOf(_ =>
        {
            var response = Ok("small");
            response.Content = new ByteArrayContent(new byte[CalendarFeedClient.MaxBytes + 1]);
            return response;
        });
        await Assert.That(async () => await declared.FetchAsync(Address, null, null, CancellationToken.None)).Throws<CalendarFeedException>();

        // No length header: the cap applies while reading.
        var (streamed, _) = ClientOf(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new EndlessStream()) });
        await Assert.That(async () => await streamed.FetchAsync(Address, null, null, CancellationToken.None)).Throws<CalendarFeedException>();
    }

    [Test]
    public async Task Fetch_Errors_NeverContainTheAddress()
    {
        foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
        {
            var (client, _) = ClientOf(_ => new HttpResponseMessage(status));
            var error = await Assert.ThrowsAsync<CalendarFeedException>(async () => await client.FetchAsync(Address, null, null, CancellationToken.None));
            await Assert.That(error!.Message).DoesNotContain("secret-token-123");
            await Assert.That(error.Message).DoesNotContain("calendar.example.org");
        }

        var failing = new CalendarFeedClient(new HttpClient(new Handler(_ => throw new HttpRequestException("Name resolution failed for calendar.example.org/secret-token-123"))));
        var network = await Assert.ThrowsAsync<CalendarFeedException>(async () => await failing.FetchAsync(Address, null, null, CancellationToken.None));
        await Assert.That(network!.Message).DoesNotContain("secret-token-123");
    }

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Array.Fill(buffer, (byte)'a', offset, count); return count; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { buffer.Span.Fill((byte)'a'); return ValueTask.FromResult(buffer.Length); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
