using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Web;

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
        using var subscription = broadcaster.Subscribe(caller.AccountId, seq => queue.Writer.TryWrite(seq));
        var current = await db.Accounts.AsNoTracking().Where(a => a.Id == caller.AccountId).Select(a => a.NextSeq - 1).FirstOrDefaultAsync(ct);
        yield return new SseItem<string>(current.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var sent = current;
        while (!ct.IsCancellationRequested)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(keepAlive);
            long? latest = null;
            try
            {
                latest = await queue.Reader.ReadAsync(idle.Token);
                while (queue.Reader.TryRead(out var more)) latest = Math.Max(latest.Value, more);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                latest = null; // Recheck the web device even when no changes arrive.
            }

            var active = await db.Devices.AsNoTracking().AnyAsync(d => d.Id == caller.DeviceId && d.AccountId == caller.AccountId &&
                d.Platform == WebSession.Platform && d.RevokedAt == null && db.Accounts.Any(a => a.Id == d.AccountId && a.DeletedAt == null), ct);
            if (!active) yield break;
            if (latest > sent)
            {
                sent = latest.Value;
                yield return new SseItem<string>(sent.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (latest is null)
            {
                yield return new SseItem<string>("", "heartbeat");
            }
        }
    }
}
