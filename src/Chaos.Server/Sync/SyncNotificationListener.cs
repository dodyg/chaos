using System.Globalization;
using System.Threading.Channels;
using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Chaos.Server.Sync;

/// <summary>
/// The one PostgreSQL <c>LISTEN</c> connection of this process (PLAN2 §8.3 step 7). Every instance's push sends
/// <c>NOTIFY chaos_seq, '&lt;account&gt;:&lt;seq&gt;'</c>; this service hears them all and hands each to <see cref="SyncBroadcaster"/>,
/// which fans it out to that account's open event streams. Streams therefore cost no database connection each. When the connection
/// drops it logs, reconnects with backoff, and re-announces the current sequence of every subscribed account, so a push missed
/// while it was down still reaches the streams.
/// </summary>
public sealed class SyncNotificationListener(IServiceScopeFactory scopes, SyncBroadcaster broadcaster, SpaceMembershipCache memberships,
    TimeProvider clock, ILogger<SyncNotificationListener> log) : BackgroundService
{
    /// <summary>The single notification channel; the payload names the account.</summary>
    public const string Channel = "chaos_seq";
    public const string MembershipChannel = "chaos_membership";

    public static string Payload(Guid accountId, long seq) => $"{accountId:N}:{seq.ToString(CultureInfo.InvariantCulture)}";

    internal static bool TryParse(string payload, out Guid streamId, out long seq)
    {
        streamId = Guid.Empty;
        seq = 0;
        var split = payload.IndexOf(':', StringComparison.Ordinal);
        return split > 0
            && Guid.TryParseExact(payload.AsSpan(0, split), "N", out streamId)
            && long.TryParse(payload.AsSpan(split + 1), NumberStyles.None, CultureInfo.InvariantCulture, out seq);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                string? connectionString;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    connectionString = scope.ServiceProvider.GetRequiredService<ChaosDbContext>().Database.GetConnectionString();
                }

                if (connectionString is not { Length: > 0 })
                {
                    return; // Nothing to listen on: streams rely on this process's broadcaster alone.
                }

                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(stoppingToken);
                var notifications = System.Threading.Channels.Channel.CreateUnbounded<(string Channel, string Payload)>();
                connection.Notification += (_, notice) =>
                    notifications.Writer.TryWrite((notice.Channel, notice.Payload));
                await using (var listen = connection.CreateCommand())
                {
                    listen.CommandText = $"LISTEN {Channel}; LISTEN {MembershipChannel};";
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                await CatchUpAsync(stoppingToken);
                failures = 0;
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken);
                    while (notifications.Reader.TryRead(out var notice))
                    {
                        await PublishNotificationAsync(notice.Channel, notice.Payload, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                failures++;
                log.LogWarning(error, "The sync notification connection failed; reconnecting (attempt {Attempt})", failures);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5)))), clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Pushes made while the connection was down were not heard: tell each subscribed account where its sequence stands.</summary>
    private async Task CatchUpAsync(CancellationToken ct)
    {
        var subscribed = broadcaster.SubscribedAccounts();
        if (subscribed.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChaosDbContext>();
        var current = await db.SyncStreams.AsNoTracking().Where(a => subscribed.Contains(a.Id)).Select(a => new { a.Id, Seq = a.NextSeq - 1 }).ToListAsync(ct);
        foreach (var account in current)
        {
            broadcaster.PublishStream(account.Id, account.Id, account.Seq);
        }
        foreach (var accountId in subscribed)
        {
            var roles = await memberships.GetAsync(db, accountId, ct);
            var ids = roles.Roles.Keys.ToArray();
            foreach (var stream in await db.SyncStreams.AsNoTracking().Where(s => ids.Contains(s.Id) && s.DeletedAt == null)
                .Select(s => new { s.Id, Seq = s.NextSeq - 1 }).ToListAsync(ct))
            {
                broadcaster.PublishStream(accountId, stream.Id, stream.Seq);
            }
            broadcaster.PublishMembership(accountId, roles.Version);
        }
    }

    private async Task PublishNotificationAsync(string channel, string payload, CancellationToken ct)
    {
        if (!TryParse(payload, out var subject, out var number))
        {
            return;
        }

        if (channel == MembershipChannel)
        {
            memberships.Invalidate(subject);
            broadcaster.PublishMembership(subject, number);
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChaosDbContext>();
        var memberAccounts = await db.SpaceMembers.AsNoTracking()
            .Where(m => m.SpaceId == subject && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null && m.AccountId != null)
            .Select(m => m.AccountId!.Value)
            .ToListAsync(ct);
        if (memberAccounts.Count == 0)
        {
            memberAccounts.Add(subject);
        }
        foreach (var accountId in memberAccounts)
        {
            broadcaster.PublishStream(accountId, subject, number);
        }
    }
}
