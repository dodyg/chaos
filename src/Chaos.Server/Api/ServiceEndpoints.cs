using System.Security.Claims;
using System.Threading.RateLimiting;
using Chaos.Server.Auth;
using Chaos.Server.Blobs;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Chaos.Server.Api;

public sealed record RegisterDeviceRequest(Guid DeviceId, string Name, string Platform, string ClientVersion, int ClientSchema);

public sealed record DeviceDto(Guid Id, string Name, string Platform, string ClientVersion, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool Current);

/// <summary>The account's canonical profile, Personal space and Inbox IDs (PLAN2 §7.13); absent until the first device binds them.</summary>
public sealed record CanonicalIds(Guid ProfileId, Guid PersonalSpaceId, Guid InboxId);

/// <summary>What the account holds right now, for the join dialog's "on the account" side (counts of live entities, content-free).</summary>
public sealed record AccountCounts(long Tasks, long Lists, long Tags, long Pages);

public sealed record AccountDto(Guid Id, string Email, string? DisplayName, long StorageBytes, long StorageQuotaBytes, IReadOnlyList<DeviceDto> Devices,
    CanonicalIds? Canonical = null, AccountCounts? Counts = null);

public sealed record BindAccountRequest(Guid ProfileId, Guid PersonalSpaceId, Guid InboxId);

/// <summary>Whether this call bound the IDs (the first device) and the IDs the account now has.</summary>
public sealed record BindAccountResponse(bool Bound, CanonicalIds Canonical);

/// <summary>What the person typed to confirm deleting the account (PLAN2 §8.6); the desktop asks for it and the service checks it too.</summary>
public sealed record DeleteAccountRequest(string Confirmation);

/// <summary>The versioned client API (PLAN2 §8.4 step 6, P12 item 4). Every query is filtered by the account in the token.</summary>
public static class ServiceEndpoints
{
    public const string RateLimitPolicy = "device";

    public static IServiceCollection AddServiceRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(RateLimitPolicy, http =>
            {
                var limit = http.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.RequestsPerMinutePerDevice;
                var key = http.User.FindFirst(DesktopClient.DeviceIdClaim)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
            // The service MCP endpoint (PLAN3 12.5) applies the same per-minute limit to each agent.
            limiter.AddPolicy(McpEndpoint.RateLimitPolicy, http =>
            {
                var limit = http.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.RequestsPerMinutePerDevice;
                var key = http.User.FindFirst(McpOAuth.AgentIdClaim)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter("agent:" + key, _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
        });
        return services;
    }

    public static void MapServiceEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1").RequireAuthorization().RequireRateLimiting(RateLimitPolicy).AddEndpointFilter<ActiveDeviceFilter>();
        v1.MapPost("/devices", RegisterDevice);
        v1.MapGet("/account", GetAccount);
        v1.MapPost("/account/bind", BindAccount);
        v1.MapDelete("/account", DeleteAccount);
        v1.MapDelete("/devices/{id:guid}", RevokeDevice);
        v1.MapSyncEndpoints();
        v1.MapBlobEndpoints();
        var v2 = app.MapGroup("/v2").RequireAuthorization().RequireRateLimiting(RateLimitPolicy)
            .AddEndpointFilter<ActiveDeviceFilter>().AddEndpointFilter<SpaceEndpoints.V2ProtocolFilter>();
        v2.MapV2SyncEndpoints();
        v2.MapSpaceEndpoints();
        v2.MapAgentEndpoints();
    }

    private static async Task<IResult> RegisterDevice(ClaimsPrincipal user, RegisterDeviceRequest request, DeviceRegistration devices, CancellationToken ct)
    {
        if (CallerIdentity.TryGet(user) is not { } caller)
        {
            return Results.Unauthorized();
        }

        var result = await devices.RegisterAsync(caller.AccountId, caller.DeviceId, request, ct);
        return result.Outcome switch
        {
            DeviceRegistrationOutcome.Registered => Results.Created($"/v1/devices/{result.Device!.Id}", result.Device),
            DeviceRegistrationOutcome.Updated => Results.Ok(result.Device),
            DeviceRegistrationOutcome.WrongDevice => ApiErrors.Problem("device-mismatch", "The device ID does not match the one this sign-in was made for.", StatusCodes.Status403Forbidden),
            DeviceRegistrationOutcome.Invalid => ApiErrors.Problem("invalid-device", "The device name, platform and version are required and must be short.", StatusCodes.Status400BadRequest),
            DeviceRegistrationOutcome.OtherAccount => ApiErrors.Problem("device-registered-elsewhere", "This device is registered to another account.", StatusCodes.Status409Conflict),
            DeviceRegistrationOutcome.Removed => ApiErrors.Problem("device-removed", "This device was removed from the account.", StatusCodes.Status403Forbidden),
            _ => ApiErrors.Problem("device-busy", "The device is already being registered. Try again.", StatusCodes.Status409Conflict),
        };
    }

    private static async Task<IResult> GetAccount(ClaimsPrincipal user, ChaosDbContext db, IOptions<LimitsOptions> limits, CancellationToken ct)
    {
        if (CallerIdentity.TryGet(user) is not { } caller)
        {
            return Results.Unauthorized();
        }

        var account = await db.Accounts.AsNoTracking().Where(a => a.Id == caller.AccountId && a.DeletedAt == null)
            .Select(a => new { a.Id, a.PrimaryEmail, a.DisplayName, a.StorageBytes })
            .FirstOrDefaultAsync(ct);
        if (account is null)
        {
            return Results.Unauthorized();
        }

        var devices = await db.Devices.AsNoTracking().Where(d => d.AccountId == caller.AccountId && d.RevokedAt == null)
            .OrderBy(d => d.CreatedAt)
            .Select(d => new DeviceDto(d.Id, d.Name, d.Platform, d.ClientVersion, d.CreatedAt, d.LastSeenAt, d.Id == caller.DeviceId))
            .ToListAsync(ct);
        var ids = await db.Accounts.AsNoTracking().Where(a => a.Id == caller.AccountId).Select(a => new { a.ProfileId, a.PersonalSpaceId, a.InboxId }).FirstAsync(ct);
        CanonicalIds? canonical = ids is { ProfileId: { } p, PersonalSpaceId: { } sp, InboxId: { } ib } ? new CanonicalIds(p, sp, ib) : null;
        return Results.Ok(new AccountDto(account.Id, account.PrimaryEmail, account.DisplayName, account.StorageBytes, limits.Value.AccountQuotaBytes, devices, canonical, await CountAsync(db, caller.AccountId, ct)));
    }

    private static readonly string[] CountedKinds = ["task", "container", "tag", "page"];

    /// <summary>Live (not purged, not in the trash) entities of the kinds the person recognises. Lists exclude each Inbox.</summary>
    private static async Task<AccountCounts> CountAsync(ChaosDbContext db, Guid accountId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<KindCount>($"""
            SELECT e."Kind" AS "Kind", count(*) AS "Count"
            FROM "SyncEntities" e
            WHERE e."StreamId" = {accountId} AND e."PurgedAt" IS NULL
              AND e."Kind" = ANY ({CountedKinds})
              AND e."Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
              AND NOT (e."Kind" = 'container' AND (e."Groups" -> 'create' -> 'values' ->> 'IsInbox') = '1')
            GROUP BY e."Kind"
            """).ToListAsync(ct);
        long Of(string kind) => rows.FirstOrDefault(r => r.Kind == kind)?.Count ?? 0;
        return new AccountCounts(Of("task"), Of("container"), Of("tag"), Of("page"));
    }

    private sealed record KindCount(string Kind, long Count);

    /// <summary>
    /// The first device binds its profile, Personal space and Inbox IDs as the account's canonical ones (PLAN2 §7.13 case 1).
    /// Safe to repeat and to race: only the first call binds; every call answers with what the account has.
    /// </summary>
    private static async Task<IResult> BindAccount(ClaimsPrincipal user, BindAccountRequest request, ChaosDbContext db, CancellationToken ct)
    {
        if (CallerIdentity.TryGet(user) is not { } caller)
        {
            return Results.Unauthorized();
        }

        if (request.ProfileId == Guid.Empty || request.PersonalSpaceId == Guid.Empty || request.InboxId == Guid.Empty
            || new[] { request.ProfileId, request.PersonalSpaceId, request.InboxId }.Distinct().Count() != 3)
        {
            return ApiErrors.Problem("invalid-ids", "The profile, Personal space and Inbox IDs are required and must differ.", StatusCodes.Status400BadRequest);
        }

        // One conditional UPDATE decides who binds, so two devices racing cannot both win.
        bool bound;
        try
        {
            bound = await db.Accounts.Where(a => a.Id == caller.AccountId && a.ProfileId == null
                    && !db.Accounts.Any(other => other.Id != caller.AccountId && other.ProfileId == request.ProfileId))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ProfileId, request.ProfileId).SetProperty(a => a.PersonalSpaceId, request.PersonalSpaceId).SetProperty(a => a.InboxId, request.InboxId), ct) == 1;
        }
        catch (DbUpdateException error) when (error.InnerException is Npgsql.PostgresException
            { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            return ApiErrors.Problem("profile-in-use", "Those account IDs are already bound to another account.",
                StatusCodes.Status409Conflict);
        }
        var ids = await db.Accounts.AsNoTracking().Where(a => a.Id == caller.AccountId).Select(a => new { a.ProfileId, a.PersonalSpaceId, a.InboxId }).FirstAsync(ct);
        if (ids.ProfileId is null)
        {
            return ApiErrors.Problem("profile-in-use", "Those account IDs are already bound to another account.", StatusCodes.Status409Conflict);
        }
        return Results.Ok(new BindAccountResponse(bound, new CanonicalIds(ids.ProfileId!.Value, ids.PersonalSpaceId!.Value, ids.InboxId!.Value)));
    }

    public const string DeleteConfirmation = "DELETE";
    public static readonly TimeSpan FreshSignInWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Deletes the account and everything the service holds for it (PLAN2 §8.6). It needs a sign-in made in the last ten minutes
    /// and the typed phrase. Other devices learn the next time they call (401 <c>account-deleted</c>) and keep their local data.
    /// </summary>
    private static async Task<IResult> DeleteAccount(ClaimsPrincipal user, [Microsoft.AspNetCore.Mvc.FromBody] DeleteAccountRequest request, AccountDeletion deletion, TimeProvider clock, CancellationToken ct)
    {
        if (CallerIdentity.TryGet(user) is not { } caller)
        {
            return Results.Unauthorized();
        }

        var now = clock.GetUtcNow();
        if (!long.TryParse(user.FindFirst(DesktopClient.AuthTimeClaim)?.Value, out var signedAtSeconds)
            || now - DateTimeOffset.FromUnixTimeSeconds(signedAtSeconds) > FreshSignInWindow)
        {
            return ApiErrors.Problem("fresh-sign-in-required", "Sign in again to confirm deleting your account.", StatusCodes.Status403Forbidden);
        }

        if (!string.Equals(request.Confirmation?.Trim(), DeleteConfirmation, StringComparison.Ordinal))
        {
            return ApiErrors.Problem("confirmation-required", $"Type {DeleteConfirmation} to confirm.", StatusCodes.Status400BadRequest);
        }

        var result = await deletion.DeleteAsync(caller.AccountId, caller.DeviceId, ct);
        if (!result.Deleted && result.TransferFirst.Count > 0)
        {
            return ApiErrors.Problem("transfer-ownership-first",
                "Transfer ownership of these spaces first: " + string.Join(", ", result.TransferFirst),
                StatusCodes.Status409Conflict);
        }
        return result.Deleted ? Results.NoContent() : Results.Unauthorized();
    }

    private static async Task<IResult> RevokeDevice(ClaimsPrincipal user, Guid id, ChaosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (CallerIdentity.TryGet(user) is not { } caller)
        {
            return Results.Unauthorized();
        }

        var now = clock.GetUtcNow();
        var revoked = await db.Devices.Where(d => d.Id == id && d.AccountId == caller.AccountId && d.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.RevokedAt, now), ct);
        if (revoked == 1)
        {
            db.AuditEvents.Add(new AuditEvent { AccountId = caller.AccountId, Kind = AuditKinds.DeviceRevoked, At = now, DeviceId = id });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }

        // Another account's device looks the same as one that never existed.
        return await db.Devices.AsNoTracking().AnyAsync(d => d.Id == id && d.AccountId == caller.AccountId, ct)
            ? Results.NoContent()
            : Results.NotFound();
    }
}

/// <summary>
/// Refuses a token whose account was deleted or whose device was revoked, then notes the device's last use.
/// A device that has not registered yet passes (it registers with its first call).
/// </summary>
public sealed class ActiveDeviceFilter(ChaosDbContext db, TimeProvider clock) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var user = context.HttpContext.User;
        if (CallerIdentity.TryGet(user) is not (Guid accountId, Guid deviceId))
        {
            return Results.Unauthorized();
        }

        var ct = context.HttpContext.RequestAborted;
        if (!await db.Accounts.AsNoTracking().AnyAsync(a => a.Id == accountId && a.DeletedAt == null, ct))
        {
            return Refuse(DesktopClient.AccountDeletedCode, "This account was deleted.");
        }

        if (await AuthEndpoints.DeviceRefusedAsync(db, accountId, deviceId, ct))
        {
            return Refuse(DesktopClient.DeviceRevokedCode, "This device was removed from the account.");
        }

        var now = clock.GetUtcNow();
        await db.Devices.Where(d => d.Id == deviceId && d.AccountId == accountId && d.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, now), ct);
        return await next(context);
    }

    /// <summary>A 401 that says why, so the desktop can tell "sign in again" from "this device is out of the account".</summary>
    private static IResult Refuse(string code, string detail) =>
        ApiErrors.Problem(code, detail, StatusCodes.Status401Unauthorized);
}
