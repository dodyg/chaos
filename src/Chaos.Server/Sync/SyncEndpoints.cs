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
        services.AddScoped<CalendarCacheService>();
        services.AddHostedService<SyncHorizonService>();
        services.AddHostedService<SyncNotificationListener>();
        services.AddOptions<SharingOptions>().Bind(configuration.GetSection(SharingOptions.Section))
            .Validate(o => o.MaxPeoplePerSpace > 0 && o.MaxAgentsPerSpace > 0 && o.MaxSpacesPerAccount > 0
                && o.MaxOpenInvitesPerSpace > 0 && o.MaxInviteEmailsPerAccountPerDay > 0
                && o.InviteAcceptsPerAccountPerHour > 0 && o.InviteAcceptsPerIpPerHour > 0,
                "Sharing limits must be greater than zero.")
            .ValidateOnStart();
        services.AddSingleton<SpaceMembershipCache>();
        services.AddScoped<ServerChangeWriter>();
        services.AddSingleton<InviteAcceptanceLimiter>();
        services.AddScoped<SpaceService>();
        services.AddHostedService<SpacePurgeService>();
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

    public static void MapV2SyncEndpoints(this RouteGroupBuilder v2)
    {
        v2.MapGet("/streams", Streams);
        var sync = v2.MapGroup("/sync");
        sync.MapPost("/push", PushV2);
        sync.MapGet("/pull", PullV2);
        sync.MapGet("/snapshot", SnapshotV2);
        sync.MapGet("/events", SyncEvents.StreamV2Async);
    }

    internal sealed record Caller(Guid AccountId, Guid DeviceId, int Schema);

    /// <summary>Checks the three protocol headers (§7.12) and the token; a failure is the answer to send.</summary>
    internal static IResult? Identify(HttpContext http, ClaimsPrincipal user, out Caller caller)
        => Identify(http, user, SyncProtocol.Version1, out caller);

    internal static IResult? IdentifyV2(HttpContext http, ClaimsPrincipal user, out Caller caller)
        => Identify(http, user, SyncProtocol.Version2, out caller);

    private static IResult? Identify(HttpContext http, ClaimsPrincipal user, int protocolVersion, out Caller caller)
    {
        caller = new Caller(Guid.Empty, Guid.Empty, 0);
        if (CallerIdentity.TryGet(user) is not (Guid account, Guid device))
        {
            return Results.Unauthorized();
        }

        if (!int.TryParse(http.Request.Headers[SyncProtocol.ProtocolHeader], out var protocol) || protocol != protocolVersion)
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

    private static Task<IResult> Streams(HttpContext http, ClaimsPrincipal user, ChaosDbContext db, SyncService sync,
        SpaceMembershipCache memberships, CancellationToken ct)
    {
        if (IdentifyV2(http, user, out var caller) is { } refusal)
        {
            return Task.FromResult(refusal);
        }

        return StreamsAsync();
        async Task<IResult> StreamsAsync()
        {
            var version = await db.Accounts.AsNoTracking().Where(a => a.Id == caller.AccountId)
                .Select(a => a.MembershipVersion).FirstAsync(ct);
            var rows = await sync.StreamsAsync(caller.AccountId, ct);
            memberships.Invalidate(caller.AccountId);
            return Results.Ok(new
            {
                membershipVersion = version,
                streams = rows.Select(row => new
                {
                    id = row.Id, kind = row.Kind, spaceId = row.SpaceId, role = row.Role,
                    head = row.Head, pruned = row.Pruned,
                }),
            });
        }
    }

    private static async Task<IResult> UploadCalendarCache(HttpContext http, ClaimsPrincipal user, CalendarCacheSnapshot cache,
        CalendarCacheService service, CancellationToken ct)
    {
        if (Identify(http, user, out var caller) is { } refusal) return refusal;
        return await service.StoreAsync(caller.AccountId, cache, ct) switch
        {
            CalendarCacheOutcome.Stored => Results.Ok(),
            CalendarCacheOutcome.Invalid => ApiErrors.Problem("invalid-calendar-cache", "The calendar cache is not valid.", StatusCodes.Status400BadRequest),
            CalendarCacheOutcome.AccountGone => Results.Unauthorized(),
            CalendarCacheOutcome.SourceGone => Results.NotFound(),
            _ => Results.StatusCode(StatusCodes.Status413PayloadTooLarge),
        };
    }

    private static async Task<IResult> Push(HttpContext http, ClaimsPrincipal user, SyncService sync, CancellationToken ct)
        => await PushVersionAsync(http, user, sync, SyncProtocol.Version1, ct);

    private static async Task<IResult> PushV2(HttpContext http, ClaimsPrincipal user, SyncService sync, CancellationToken ct)
        => await PushVersionAsync(http, user, sync, SyncProtocol.Version2, ct);

    private static async Task<IResult> PushVersionAsync(HttpContext http, ClaimsPrincipal user, SyncService sync, int protocolVersion,
        CancellationToken ct)
    {
        if ((protocolVersion == SyncProtocol.Version2
                ? IdentifyV2(http, user, out var caller)
                : Identify(http, user, out caller)) is { } refusal)
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
            outcome = await sync.PushAsync(caller.AccountId, caller.DeviceId, caller.Schema, protocolVersion, changes, ct);
        }
        catch (AccountUnavailableException)
        {
            return ApiErrors.Problem(DesktopClient.AccountDeletedCode, "This account was deleted.", StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(new
        {
            accepted = protocolVersion == SyncProtocol.Version1
                ? (object)outcome.Accepted
                : outcome.Accepted.Select(id => new { changeId = id, stream = outcome.Streams!.GetValueOrDefault(id) }).ToArray(),
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

    private static async Task<IResult> PullV2(HttpContext http, ClaimsPrincipal user, SyncService sync, Guid? stream, long? after,
        int? limit, CancellationToken ct)
    {
        if (IdentifyV2(http, user, out var caller) is { } refusal) return refusal;
        if (stream is null) return ApiErrors.Problem("invalid-stream", "The stream is required.", StatusCodes.Status400BadRequest);
        try
        {
            var page = await sync.PullAsync(caller.AccountId, stream.Value, caller.Schema, Math.Max(after ?? 0, 0),
                limit ?? SyncProtocol.MaxPullChanges, ct);
            return Results.Content(page.Json, "application/json", Encoding.UTF8);
        }
        catch (UnauthorizedAccessException)
        {
            return ApiErrors.Problem(SyncRejection.NotMember, "You are not a member of this space.", StatusCodes.Status403Forbidden);
        }
    }

    private static async Task<IResult> SnapshotV2(HttpContext http, ClaimsPrincipal user, SyncService sync, Guid? stream,
        string? kind, string? after, int? limit, CancellationToken ct)
    {
        if (IdentifyV2(http, user, out var caller) is { } refusal) return refusal;
        if (stream is null) return ApiErrors.Problem("invalid-stream", "The stream is required.", StatusCodes.Status400BadRequest);
        try
        {
            var page = kind is null ? null : await sync.SnapshotAsync(caller.AccountId, stream.Value, caller.Schema, kind, after,
                limit ?? SyncProtocol.MaxPullChanges, ct);
            return page is null
                ? ApiErrors.Problem("unknown-kind", "The snapshot needs a known kind.", StatusCodes.Status400BadRequest)
                : Results.Content(page.Json, "application/json", Encoding.UTF8);
        }
        catch (UnauthorizedAccessException)
        {
            return ApiErrors.Problem(SyncRejection.NotMember, "You are not a member of this space.", StatusCodes.Status403Forbidden);
        }
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
            log.LogDebug("Sync horizon service stopped.");
        }
    }
}
