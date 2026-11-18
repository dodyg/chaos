using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Threading.Channels;
using Chaos.Server.Data;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Chaos.Server.Sync;

/// <summary>
/// <c>GET /v1/sync/events</c> (PLAN2 §7.12, P15 item 1): a Server-Sent Events stream that says <c>{"seq":N}</c> whenever the
/// account's sequence advances, plus a keep-alive comment when idle. The client reacts by pulling. A push on this instance
/// reaches the stream through <see cref="SyncBroadcaster"/>; a push on another instance reaches it through PostgreSQL
/// <c>LISTEN</c> on the account's channel (§8.3 step 7). The first event carries the current sequence, so a client that
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

        // Subscribe before reading the current sequence, so a push in between is heard rather than lost.
        await using var listener = await ListenAsync(db, caller.AccountId, queue.Writer, ct);
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
        catch (OperationCanceledException)
        {
            // The client went away.
        }
        catch (IOException)
        {
            // The connection broke while writing.
        }
    }

    private static async Task WriteAsync(HttpResponse response, string text, CancellationToken ct)
    {
        await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await response.Body.FlushAsync(ct);
    }

    /// <summary>
    /// Listens on the account's PostgreSQL channel for pushes made by other instances. Without a connection string
    /// there is nothing to listen on and the stream relies on this process's broadcaster alone.
    /// </summary>
    private static async Task<IAsyncDisposable> ListenAsync(ChaosDbContext db, Guid accountId, ChannelWriter<long> into, CancellationToken ct)
    {
        if (db.Database.GetConnectionString() is not { Length: > 0 } connectionString)
        {
            return new Nothing();
        }

        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            connection.Notification += (_, notice) =>
            {
                if (long.TryParse(notice.Payload, NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
                {
                    into.TryWrite(seq);
                }
            };
            await using var listen = connection.CreateCommand();
            listen.CommandText = $"LISTEN \"{SyncBroadcaster.Channel(accountId)}\"";
            await listen.ExecuteNonQueryAsync(ct);

            // Notifications are delivered while the connection is waiting for one, so a background task keeps it waiting.
            var pump = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await connection.WaitAsync(CancellationToken.None);
                    }
                }
                catch (Exception)
                {
                    // Closed on the way out, or the database went away: the client reconnects and pulls.
                }
            }, CancellationToken.None);
            return new Listener(connection, pump);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Nothing : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Listener(NpgsqlConnection connection, Task pump) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await connection.DisposeAsync();
            await pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}
