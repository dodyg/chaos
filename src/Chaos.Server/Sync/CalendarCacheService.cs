using System.Text;
using System.Text.Json;
using Chaos.Server.Data;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Sync;

public enum CalendarCacheOutcome { Stored, Invalid, AccountGone, SourceGone, TooLarge }

/// <summary>Validates and stores a device's fetched calendar events for one of the account's enabled calendar sources (for My Day on other devices).</summary>
public sealed class CalendarCacheService(ChaosDbContext db, TimeProvider clock)
{
    public async Task<CalendarCacheOutcome> StoreAsync(Guid accountId, CalendarCacheSnapshot cache, CancellationToken ct)
    {
        if (!Guid.TryParse(cache.SourceId, out _) || cache.Events is null || cache.Events.Count > 10000
            || cache.FetchedAt > clock.GetUtcNow().AddMinutes(10)
            || cache.Events.Any(e => e is null || string.IsNullOrEmpty(e.ExternalId) || e.ExternalId.Length > 2048
                || e.Title is null || e.Title.Length > 4096 || e.Location?.Length > 4096 || e.VideoUrl?.Length > 4096 || e.WebUrl?.Length > 4096
                || e.EndUtc < e.StartUtc || (e.AllDay && (e.StartDate is null || e.EndDate is null || e.EndDate <= e.StartDate))))
            return CalendarCacheOutcome.Invalid;

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<CalendarCacheOutcome>(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var account = await db.Accounts.FromSqlInterpolated($"""SELECT * FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""").SingleOrDefaultAsync(ct);
            if (account is null || account.DeletedAt is not null) return CalendarCacheOutcome.AccountGone;
            var source = await db.SyncEntities.AsNoTracking().SingleOrDefaultAsync(e => e.AccountId == accountId
                && e.Kind == "calendar_source" && e.EntityKey == cache.SourceId && e.PurgedAt == null, ct);
            if (source is null) return CalendarCacheOutcome.SourceGone;
            var groups = SyncJson.ReadGroups(source.Groups);
            if (groups.GetValueOrDefault("deleted")?.Values.Values.GetValueOrDefault("DeletedAtUtc") is not null
                || groups.GetValueOrDefault("enabled")?.Values.Values.GetValueOrDefault("Enabled") is not long enabled || enabled != 1)
                return CalendarCacheOutcome.SourceGone;
            var json = JsonSerializer.Serialize(cache);
            if (Encoding.UTF8.GetByteCount(json) > SyncProtocol.MaxPushBytes) return CalendarCacheOutcome.TooLarge;
            var now = clock.GetUtcNow();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "SyncEntities" ("AccountId", "Kind", "EntityKey", "Groups", "LastSeq", "UpdatedAt")
                VALUES ({accountId}, 'calendar_cache', {cache.SourceId}, CAST({json} AS jsonb), 0, {now})
                ON CONFLICT ("AccountId", "Kind", "EntityKey") DO UPDATE
                SET "Groups" = EXCLUDED."Groups", "UpdatedAt" = EXCLUDED."UpdatedAt"
                WHERE ("SyncEntities"."Groups" ->> 'FetchedAt')::timestamptz <= (EXCLUDED."Groups" ->> 'FetchedAt')::timestamptz
                """, ct);
            await transaction.CommitAsync(ct);
            return CalendarCacheOutcome.Stored;
        });
    }
}
