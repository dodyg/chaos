using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Threading.Channels;
using Chaos.Server.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Sync;

/// <summary>
/// <c>GET /v1/sync/events</c> (PLAN2 §7.12, P15 item 1): a Server-Sent Events stream that says <c>{"seq":N}</c> whenever the
/// account's sequence advances, plus a keep-alive comment when idle. The client reacts by pulling. A push on this instance
/// reaches the stream through <see cref="SyncBroadcaster"/>; a push on another instance reaches it through the process's
/// single PostgreSQL <c>LISTEN</c> connection (<see cref="SyncNotificationListener"/>, §8.3 step 7). The first event carries the current sequence, so a client that
/// connects late still pulls what it missed.
/// </summary>
public static class SyncEvents
{
    public static async Task StreamAsync(HttpContext http, ClaimsPrincipal user, ChaosDbContext db, SyncBroadcaster broadcaster, IOptions<SyncOptions> options, CancellationToken ct)
    {
        if (SyncEndpoints.Identify(http, user, out var caller) is { } refusal)
        {
            await refusal.ExecuteAsync(http);
            return;
        }

        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no"; // A proxy in front must not hold the events back.
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var queue = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
        using var subscription = broadcaster.Subscribe(caller.AccountId, seq => queue.Writer.TryWrite(seq));

        // Subscribed before reading the current sequence, so a push in between is heard rather than lost.
        var current = await db.Accounts.AsNoTracking().Where(a => a.Id == caller.AccountId).Select(a => a.NextSeq - 1).FirstOrDefaultAsync(ct);
        long sent = -1;
        try
        {
            await WriteAsync(response, $"data: {{\"seq\":{current.ToString(CultureInfo.InvariantCulture)}}}\n\n", ct);
            sent = current;
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(options.Value.EventKeepAlive);
                long latest;
                try
                {
                    latest = await queue.Reader.ReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await WriteAsync(response, ": keep-alive\n\n", ct);
                    continue;
                }

                while (queue.Reader.TryRead(out var more))
                {
                    latest = Math.Max(latest, more);
                }

                if (latest > sent)
                {
                    await WriteAsync(response, $"data: {{\"seq\":{latest.ToString(CultureInfo.InvariantCulture)}}}\n\n", ct);
                    sent = latest;
                }
            }
        }
        catch (OperationCanceledException error)
        {
            // The client went away.
            System.Diagnostics.Trace.TraceInformation("Sync event stream ended after cancellation: {0}", error.GetType().Name);
        }
        catch (IOException error)
        {
            // The connection broke while writing.
            System.Diagnostics.Trace.TraceInformation("Sync event stream connection closed: {0}", error.GetType().Name);
        }
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken ct)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await response.Body.FlushAsync(ct);
    }
}
