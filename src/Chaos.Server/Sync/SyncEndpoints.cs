using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Chaos.Server.Api;
using Chaos.Server.Auth;
using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;
using Chaos.Sync;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Chaos.Server.Sync;

/// <summary>The sync endpoints of wire protocol version 1 (PLAN2 §7.12). The account and device come from the token, never from the request.</summary>
public static class SyncEndpoints
{
    public static IServiceCollection AddSyncService(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SyncOptions>().Bind(configuration.GetSection(SyncOptions.Section))
            .Validate(o => o.ChangeLogHorizon > TimeSpan.Zero && o.HorizonJobInterval > TimeSpan.Zero && o.EventKeepAlive > TimeSpan.Zero,
                "Sync:ChangeLogHorizon, Sync:HorizonJobInterval and Sync:EventKeepAlive must be greater than zero.")
            .ValidateOnStart();
        services.AddSingleton<SyncBroadcaster>();
        services.TryAddSingleton<SyncKindCatalog>();
        services.AddScoped<SyncService>();
        services.AddHostedService<SyncHorizonService>();
        services.AddHostedService<SyncNotificationListener>();
        return services;
    }

    public static void MapSyncEndpoints(this RouteGroupBuilder v1)
    {
        var sync = v1.MapGroup("/sync");
        sync.MapPost("/push", Push);
        sync.MapPost("/calendar-cache", UploadCalendarCache);
        sync.MapGet("/pull", Pull);
        sync.MapGet("/snapshot", Snapshot);
        sync.MapGet("/events", SyncEvents.StreamAsync);
    }

    internal sealed record Caller(Guid AccountId, Guid DeviceId, int Schema);

    /// <summary>Checks the three protocol headers (§7.12) and the token; a failure is the answer to send.</summary>
    internal static IResult? Identify(HttpContext http, ClaimsPrincipal user, out Caller caller)
    {
        caller = new Caller(Guid.Empty, Guid.Empty, 0);
        if (CallerIdentity.TryGet(user) is not (Guid account, Guid device))
        {
            return Results.Unauthorized();
        }

        if (!int.TryParse(http.Request.Headers[SyncProtocol.ProtocolHeader], out var protocol) || protocol != SyncProtocol.Version)
        {
            return ApiErrors.Problem("protocol-unsupported", "This version of Chaos speaks a sync protocol the service does not.", StatusCodes.Status400BadRequest);
        }

        if (!int.TryParse(http.Request.Headers[SyncProtocol.SchemaHeader], out var schema) || schema < 0)
        {
            return ApiErrors.Problem("invalid-header", $"The {SyncProtocol.SchemaHeader} header is missing or not a number.", StatusCodes.Status400BadRequest);
        }

        if (!Guid.TryParse(http.Request.Headers[SyncProtocol.DeviceHeader], out var headerDevice) || headerDevice != device)
        {
            return ApiErrors.Problem("device-mismatch", $"The {SyncProtocol.DeviceHeader} header does not match the device this sign-in was made for.", StatusCodes.Status403Forbidden);
        }

        caller = new Caller(account, device, schema);
        return null;
    }

    private static async Task<IResult> UploadCalendarCache(HttpContext http, ClaimsPrincipal user, CalendarCacheSnapshot cache,
        ChaosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (Identify(http, user, out var caller) is { } refusal) return refusal;
        if (!Guid.TryParse(cache.SourceId, out _) || cache.Events is null || cache.Events.Count > 10000
            || cache.FetchedAt > clock.GetUtcNow().AddMinutes(10)
            || cache.Events.Any(e => e is null || string.IsNullOrEmpty(e.ExternalId) || e.ExternalId.Length > 2048
                || e.Title is null || e.Title.Length > 4096 || e.Location?.Length > 4096 || e.VideoUrl?.Length > 4096 || e.WebUrl?.Length > 4096
                || e.EndUtc < e.StartUtc || (e.AllDay && (e.StartDate is null || e.EndDate is null || e.EndDate <= e.StartDate))))
            return ApiErrors.Problem("invalid-calendar-cache", "The calendar cache is not valid.", StatusCodes.Status400BadRequest);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync<IResult>(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var account = await db.Accounts.FromSqlInterpolated($"""SELECT * FROM "Accounts" WHERE "Id" = {caller.AccountId} FOR UPDATE""").SingleOrDefaultAsync(ct);
            if (account is null || account.DeletedAt is not null) return Results.Unauthorized();
            var source = await db.SyncEntities.AsNoTracking().SingleOrDefaultAsync(e => e.AccountId == caller.AccountId
                && e.Kind == "calendar_source" && e.EntityKey == cache.SourceId && e.PurgedAt == null, ct);
            if (source is null) return Results.NotFound();
            var groups = SyncJson.ReadGroups(source.Groups);
            if (groups.GetValueOrDefault("deleted")?.Values.Values.GetValueOrDefault("DeletedAtUtc") is not null
                || groups.GetValueOrDefault("enabled")?.Values.Values.GetValueOrDefault("Enabled") is not long enabled || enabled != 1)
                return Results.NotFound();
            var json = JsonSerializer.Serialize(cache);
            if (Encoding.UTF8.GetByteCount(json) > SyncProtocol.MaxPushBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            var now = clock.GetUtcNow();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "SyncEntities" ("AccountId", "Kind", "EntityKey", "Groups", "LastSeq", "UpdatedAt")
                VALUES ({caller.AccountId}, 'calendar_cache', {cache.SourceId}, CAST({json} AS jsonb), 0, {now})
                ON CONFLICT ("AccountId", "Kind", "EntityKey") DO UPDATE
                SET "Groups" = EXCLUDED."Groups", "UpdatedAt" = EXCLUDED."UpdatedAt"
                WHERE ("SyncEntities"."Groups" ->> 'FetchedAt')::timestamptz <= (EXCLUDED."Groups" ->> 'FetchedAt')::timestamptz
                """, ct);
            await transaction.CommitAsync(ct);
            return Results.Ok();
        });
    }

    private static async Task<IResult> Push(HttpContext http, ClaimsPrincipal user, SyncService sync, CancellationToken ct)
    {
        if (Identify(http, user, out var caller) is { } refusal)
        {
            return refusal;
        }

        // The body is read against the limit rather than trusting Content-Length (§8.5: 4 MiB).
        using var body = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await http.Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            if (body.Length + read > SyncProtocol.MaxPushBytes)
            {
                return ApiErrors.Problem("too-large", "A push may be at most 4 MiB.", StatusCodes.Status413PayloadTooLarge);
            }

            body.Write(buffer, 0, read);
        }

        IReadOnlyList<IncomingChange> changes;
        try
        {
            changes = SyncService.ParsePush(body.GetBuffer().AsMemory(0, (int)body.Length));
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return ApiErrors.Problem("invalid", "The push is not a list of changes.", StatusCodes.Status400BadRequest);
        }

        if (changes.Count > SyncProtocol.MaxPushChanges)
        {
            return ApiErrors.Problem("too-many", $"A push may carry at most {SyncProtocol.MaxPushChanges} changes.", StatusCodes.Status413PayloadTooLarge);
        }

        PushOutcome outcome;
        try
        {
            outcome = await sync.PushAsync(caller.AccountId, caller.DeviceId, caller.Schema, changes, ct);
        }
        catch (AccountUnavailableException)
        {
            return ApiErrors.Problem(DesktopClient.AccountDeletedCode, "This account was deleted.", StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new
        {
            accepted = outcome.Accepted,
            rejected = outcome.Rejected.Select(r => new { changeId = r.ChangeId, reason = r.Reason }),
            serverHlc = outcome.ServerHlc,
        });
    }

    private static async Task<IResult> Pull(HttpContext http, ClaimsPrincipal user, SyncService sync, long? after, int? limit, CancellationToken ct)
    {
        if (Identify(http, user, out var caller) is { } refusal)
        {
            return refusal;
        }

        var page = await sync.PullAsync(caller.AccountId, caller.Schema, Math.Max(after ?? 0, 0), limit ?? SyncProtocol.MaxPullChanges, ct);
        return Results.Content(page.Json, "application/json", Encoding.UTF8);
    }

    private static async Task<IResult> Snapshot(HttpContext http, ClaimsPrincipal user, SyncService sync, string? kind, string? after, int? limit, CancellationToken ct)
    {
        if (Identify(http, user, out var caller) is { } refusal)
        {
            return refusal;
        }

        var page = kind is null ? null : await sync.SnapshotAsync(caller.AccountId, caller.Schema, kind, after, limit ?? SyncProtocol.MaxPullChanges, ct);
        return page is null
            ? ApiErrors.Problem("unknown-kind", "The snapshot needs a known kind.", StatusCodes.Status400BadRequest)
            : Results.Content(page.Json, "application/json", Encoding.UTF8);
    }
}

/// <summary>Prunes the change log past the horizon on a timer (PLAN2 §7.12: 90 days).</summary>
public sealed class SyncHorizonService(IServiceScopeFactory scopes, IOptions<SyncOptions> options, TimeProvider clock, ILogger<SyncHorizonService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.HorizonJobInterval, clock);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var pruned = await scope.ServiceProvider.GetRequiredService<SyncService>().PruneAsync(stoppingToken);
                    log.LogInformation("Change log pruned for {Accounts} accounts", pruned);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    log.LogWarning(error, "Pruning the change log failed; it will run again");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
