using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Web;

/// <summary>
/// The PWA's wake-up stream. The app refreshes the open view on any event, so the stream sends a tick whenever a
/// readable stream advanced (the account's or a member space's, PLAN3 7.8) or the membership changed, and the number
/// only orders the ticks. The content of a change never travels here.
/// </summary>
internal static class WebEvents
{
    internal static IResult Stream(HttpContext http, Guid device, ChaosDbContext db, SyncBroadcaster broadcaster, IOptions<SyncOptions> options)
    {
        var caller = WebCaller.From(http.User);
        if (device != caller.DeviceId) return Results.Conflict();
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        return TypedResults.ServerSentEvents(Events(caller, db, broadcaster, options.Value.EventKeepAlive, http.RequestAborted));
    }

    private static async IAsyncEnumerable<SseItem<string>> Events(WebCaller caller, ChaosDbContext db, SyncBroadcaster broadcaster,
        TimeSpan keepAlive, [EnumeratorCancellation] CancellationToken ct)
    {
        var queue = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
        using var streams = broadcaster.SubscribeStreams(caller.AccountId, (_, seq) => queue.Writer.TryWrite(seq));
        using var membership = broadcaster.SubscribeMembership(caller.AccountId, version => queue.Writer.TryWrite(version));
        long tick = 0;
        yield return new SseItem<string>(tick.ToString(CultureInfo.InvariantCulture));
        while (!ct.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(keepAlive);
            bool woke;
            try
            {
                await queue.Reader.ReadAsync(idle.Token);
                while (queue.Reader.TryRead(out _)) { }
                woke = true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                woke = false; // Recheck the web device even when no changes arrive.
            }

            var active = await db.Devices.AsNoTracking().AnyAsync(d => d.Id == caller.DeviceId && d.AccountId == caller.AccountId &&
                d.Platform == WebSession.Platform && d.RevokedAt == null && db.Accounts.Any(a => a.Id == d.AccountId && a.DeletedAt == null), ct);
            if (!active) yield break;
            if (woke)
            {
                tick++;
                yield return new SseItem<string>(tick.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                yield return new SseItem<string>("", "heartbeat");
            }
        }
    }
}
