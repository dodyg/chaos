using System.Security.Claims;
using System.Threading.RateLimiting;
using Chaos.Server.Auth;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Chaos.Server.Api;

public sealed record RegisterDeviceRequest(Guid DeviceId, string Name, string Platform, string ClientVersion, int ClientSchema);

public sealed record DeviceDto(Guid Id, string Name, string Platform, string ClientVersion, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool Current);

public sealed record AccountDto(Guid Id, string Email, string? DisplayName, long StorageBytes, long StorageQuotaBytes, IReadOnlyList<DeviceDto> Devices);

/// <summary>The versioned client API (PLAN2 §8.4 step 6, P12 item 4). Every query is filtered by the account in the token.</summary>
public static class ServiceEndpoints
{
    public const string RateLimitPolicy = "device";
    private const int MaxNameLength = 100;
    private const int MaxShortLength = 40;

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
        });
        return services;
    }

    public static void MapServiceEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/v1").RequireAuthorization().RequireRateLimiting(RateLimitPolicy).AddEndpointFilter<ActiveDeviceFilter>();
        v1.MapPost("/devices", RegisterDevice);
        v1.MapGet("/account", GetAccount);
        v1.MapDelete("/devices/{id:guid}", RevokeDevice);
        v1.MapSyncEndpoints();
    }

    private static (Guid AccountId, Guid DeviceId)? Caller(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(Claims.Subject)?.Value, out var account) && Guid.TryParse(user.FindFirst(DesktopClient.DeviceIdClaim)?.Value, out var device)
            ? (account, device)
            : null;

    private static async Task<IResult> RegisterDevice(ClaimsPrincipal user, RegisterDeviceRequest request, ChaosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (Caller(user) is not { } caller)
        {
            return Results.Unauthorized();
        }

        // A token is for one device; it cannot register another.
        if (request.DeviceId != caller.DeviceId)
        {
            return Results.Problem("The device ID does not match the one this sign-in was made for.", statusCode: StatusCodes.Status403Forbidden);
        }

        var name = request.Name?.Trim() ?? string.Empty;
        var platform = request.Platform?.Trim() ?? string.Empty;
        var version = request.ClientVersion?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength || platform.Length is 0 or > MaxShortLength || version.Length is 0 or > MaxShortLength || request.ClientSchema < 0)
        {
            return Results.Problem("The device name, platform and version are required and must be short.", statusCode: StatusCodes.Status400BadRequest);
        }

        var now = clock.GetUtcNow();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == request.DeviceId, ct);
        var created = device is null;
        if (device is null)
        {
            device = new Device { Id = request.DeviceId, AccountId = caller.AccountId, Name = name, Platform = platform, ClientVersion = version, ClientSchema = request.ClientSchema, CreatedAt = now, LastSeenAt = now };
            db.Devices.Add(device);
            db.AuditEvents.Add(new AuditEvent { AccountId = caller.AccountId, Kind = AuditKinds.DeviceAdded, At = now, DeviceId = device.Id });
        }
        else if (device.AccountId != caller.AccountId)
        {
            return Results.Problem("This device is registered to another account.", statusCode: StatusCodes.Status409Conflict);
        }
        else if (device.RevokedAt is not null)
        {
            return Results.Problem("This device was removed from the account.", statusCode: StatusCodes.Status403Forbidden);
        }
        else
        {
            device.Name = name;
            device.Platform = platform;
            device.ClientVersion = version;
            device.ClientSchema = request.ClientSchema;
            device.LastSeenAt = now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (created)
        {
            // Two first calls from one device raced; the other one registered it.
            return Results.Problem("The device is already being registered. Try again.", statusCode: StatusCodes.Status409Conflict);
        }

        var dto = new DeviceDto(device.Id, device.Name, device.Platform, device.ClientVersion, device.CreatedAt, device.LastSeenAt, true);
        return created ? Results.Created($"/v1/devices/{device.Id}", dto) : Results.Ok(dto);
    }

    private static async Task<IResult> GetAccount(ClaimsPrincipal user, ChaosDbContext db, IOptions<LimitsOptions> limits, CancellationToken ct)
    {
        if (Caller(user) is not { } caller)
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
        return Results.Ok(new AccountDto(account.Id, account.PrimaryEmail, account.DisplayName, account.StorageBytes, limits.Value.AccountQuotaBytes, devices));
    }

    private static async Task<IResult> RevokeDevice(ClaimsPrincipal user, Guid id, ChaosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (Caller(user) is not { } caller)
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
        if (!Guid.TryParse(user.FindFirst(Claims.Subject)?.Value, out var accountId) || !Guid.TryParse(user.FindFirst(DesktopClient.DeviceIdClaim)?.Value, out var deviceId))
        {
            return Results.Unauthorized();
        }

        var ct = context.HttpContext.RequestAborted;
        if (!await db.Accounts.AsNoTracking().AnyAsync(a => a.Id == accountId && a.DeletedAt == null, ct)
            || await AuthEndpoints.DeviceRefusedAsync(db, accountId, deviceId, ct))
        {
            return Results.Unauthorized();
        }

        var now = clock.GetUtcNow();
        await db.Devices.Where(d => d.Id == deviceId && d.AccountId == accountId && d.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenAt, now), ct);
        return await next(context);
    }
}
