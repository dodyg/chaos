using System.Security.Claims;
using Chaos.Server.Api;
using Chaos.Server.Auth;
using Chaos.Server.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Chaos.Server.Web;

internal readonly record struct WebCaller(Guid AccountId, Guid DeviceId)
{
    public static WebCaller From(ClaimsPrincipal principal) => new(
        Guid.Parse(principal.FindFirstValue(Claims.Subject)!),
        Guid.Parse(principal.FindFirstValue(DesktopClient.DeviceIdClaim)!));
}

internal static class WebSessionSetup
{
    public static IServiceCollection AddWebSessions(this IServiceCollection services, IHostEnvironment environment)
    {
        services.AddScoped<WebSession>();
        services.AddAuthentication().AddCookie(WebSession.Scheme, options =>
        {
            options.Cookie.Name = "chaos.web";
            options.Cookie.Path = WebSession.ApiPath;
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.EventsType = typeof(WebSession);
        });
        services.AddAuthorizationBuilder().AddPolicy(WebSession.Scheme, policy =>
            policy.AddAuthenticationSchemes(WebSession.Scheme).RequireAuthenticatedUser());
        return services;
    }

    public static RouteGroupBuilder MapWebSessionEndpoints(this WebApplication app)
    {
        var web = app.MapGroup(WebSession.ApiPath).RequireAuthorization(WebSession.Scheme).RequireRateLimiting(ServiceEndpoints.RateLimitPolicy);
        web.MapGet("/session", WebSession.GetSession);
        web.MapPost("/signout", WebSession.SignOut).AddEndpointFilter<WebAntiforgery>();
        return web;
    }
}

internal sealed class WebSession(ChaosDbContext db, TimeProvider clock) : CookieAuthenticationEvents
{
    public const string AppPath = "/app";
    public const string ApiPath = "/v1/web";
    public const string Scheme = "chaos-web";
    public const string Platform = "web";

    public async Task SignInAsync(HttpContext http, Guid accountId)
    {
        var now = clock.GetUtcNow();
        var deviceId = Guid.NewGuid();
        db.Devices.Add(new Device
        {
            Id = deviceId, AccountId = accountId, Name = "Mobile web", Platform = Platform, ClientVersion = "1",
            ClientSchema = 1, CreatedAt = now, LastSeenAt = now,
        });
        db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.DeviceAdded, At = now });
        await db.SaveChangesAsync(http.RequestAborted);

        var identity = new ClaimsIdentity([
            new Claim(Claims.Subject, accountId.ToString("D")),
            new Claim(DesktopClient.DeviceIdClaim, deviceId.ToString("D")),
        ], Scheme);
        await http.SignInAsync(Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var caller = WebCaller.From(context.Principal!);
        var ct = context.HttpContext.RequestAborted;
        var active = db.Devices.Where(d => d.Id == caller.DeviceId && d.AccountId == caller.AccountId &&
            d.Platform == Platform && d.RevokedAt == null && db.Accounts.Any(a => a.Id == d.AccountId && a.DeletedAt == null));
        if (!await active.AnyAsync(ct))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Scheme);
            return;
        }

        var now = clock.GetUtcNow();
        var cutoff = now.AddMinutes(-5);
        await active.Where(d => d.LastSeenAt < cutoff).ExecuteUpdateAsync(update => update.SetProperty(d => d.LastSeenAt, now), ct);
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    internal static async Task<IResult> GetSession(HttpContext http, IAntiforgery antiforgery)
    {
        var session = await http.AuthenticateAsync(Scheme);
        var caller = WebCaller.From(http.User);
        var tokens = antiforgery.GetAndStoreTokens(http);
        return Results.Ok(new
        {
            caller.DeviceId,
            ExpiresAt = session.Properties!.ExpiresUtc,
            AntiforgeryToken = tokens.RequestToken,
            AntiforgeryHeader = tokens.HeaderName,
        });
    }

    internal static async Task<IResult> SignOut(HttpContext http, ChaosDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var caller = WebCaller.From(http.User);
        var now = clock.GetUtcNow();
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == caller.DeviceId && d.AccountId == caller.AccountId && d.RevokedAt == null, ct);
        if (device is not null)
        {
            device.RevokedAt = now;
            db.AuditEvents.Add(new AuditEvent { AccountId = caller.AccountId, DeviceId = caller.DeviceId, Kind = AuditKinds.DeviceRevoked, At = now });
            await db.SaveChangesAsync(ct);
        }

        await http.SignOutAsync(Scheme);
        return Results.NoContent();
    }
}

internal sealed class WebAntiforgery(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        await antiforgery.IsRequestValidAsync(context.HttpContext)
            ? await next(context)
            : Results.Problem("Refresh the app and try again.", statusCode: StatusCodes.Status400BadRequest);
}
