using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Threading.Channels;
using Chaos.Server.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Sync;

/// <summary>Server-sent sync and membership wake-ups. Protocol v1 still receives account sequences only.</summary>
public static class SyncEvents
{
    public static async Task StreamAsync(HttpContext http, ClaimsPrincipal user, ChaosDbContext db,
        SyncBroadcaster broadcaster, IOptions<SyncOptions> options, CancellationToken ct)
    {
        if (SyncEndpoints.Identify(http, user, out var caller) is { } refusal)
        {
            await refusal.ExecuteAsync(http);
            return;
        }

        var response = Start(http);
        var queue = Channel.CreateUnbounded<long>(new UnboundedChannelOptions { SingleReader = true });
        using var subscription = broadcaster.Subscribe(caller.AccountId, seq => queue.Writer.TryWrite(seq));
        var current = await db.SyncStreams.AsNoTracking().Where(s => s.Id == caller.AccountId)
            .Select(s => s.NextSeq - 1).FirstOrDefaultAsync(ct);
        long sent = -1;
        try
        {
            await WriteAsync(response, $"data: {{\"seq\":{Number(current)}}}\n\n", ct);
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
                    await WriteAsync(response, $"data: {{\"seq\":{Number(latest)}}}\n\n", ct);
                    sent = latest;
                }
            }
        }
        catch (OperationCanceledException error)
        {
            System.Diagnostics.Trace.TraceInformation("Sync event stream ended after cancellation: {0}", error.GetType().Name);
        }
        catch (IOException error)
        {
            System.Diagnostics.Trace.TraceInformation("Sync event stream connection closed: {0}", error.GetType().Name);
        }
    }

    public static async Task StreamV2Async(HttpContext http, ClaimsPrincipal user, ChaosDbContext db,
        SyncBroadcaster broadcaster, SpaceMembershipCache memberships, IOptions<SyncOptions> options, CancellationToken ct)
    {
        if (SyncEndpoints.IdentifyV2(http, user, out var caller) is { } refusal)
        {
            await refusal.ExecuteAsync(http);
            return;
        }

        var response = Start(http);
        var queue = Channel.CreateUnbounded<(Guid? Stream, long? Seq, long? Membership)>();
        using var streamSubscription = broadcaster.SubscribeStreams(caller.AccountId,
            (stream, seq) => queue.Writer.TryWrite((stream, seq, null)));
        using var membershipSubscription = broadcaster.SubscribeMembership(caller.AccountId,
            version => queue.Writer.TryWrite((null, null, version)));
        var membership = await memberships.GetAsync(db, caller.AccountId, ct);
        var ids = membership.Roles.Keys.Append(caller.AccountId).ToArray();
        var heads = await db.SyncStreams.AsNoTracking().Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, Seq = s.NextSeq - 1 }).ToListAsync(ct);
        try
        {
            foreach (var head in heads)
            {
                await WriteAsync(response, $"data: {{\"stream\":\"{head.Id:D}\",\"seq\":{Number(head.Seq)}}}\n\n", ct);
            }
            await WriteAsync(response, $"data: {{\"membership\":{Number(membership.Version)}}}\n\n", ct);
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(options.Value.EventKeepAlive);
                (Guid? Stream, long? Seq, long? Membership) next;
                try
                {
                    next = await queue.Reader.ReadAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await WriteAsync(response, ": keep-alive\n\n", ct);
                    continue;
                }

                var text = next.Membership is { } version
                    ? $"data: {{\"membership\":{Number(version)}}}\n\n"
                    : $"data: {{\"stream\":\"{next.Stream:D}\",\"seq\":{Number(next.Seq!.Value)}}}\n\n";
                await WriteAsync(response, text, ct);
            }
        }
        catch (OperationCanceledException error)
        {
            System.Diagnostics.Trace.TraceInformation("V2 sync event stream ended after cancellation: {0}", error.GetType().Name);
        }
        catch (IOException error)
        {
            System.Diagnostics.Trace.TraceInformation("V2 sync event stream connection closed: {0}", error.GetType().Name);
        }
    }

    private static HttpResponse Start(HttpContext http)
    {
        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        return response;
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken ct)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await response.Body.FlushAsync(ct);
    }
}
