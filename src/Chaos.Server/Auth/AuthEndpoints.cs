using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using Chaos.Server.Data;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Client.AspNetCore;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Chaos.Server.Auth;

/// <summary>The sign-in pages, the OAuth endpoints (PLAN2 §8.4) and the external-provider callbacks.</summary>
public static class AuthEndpoints
{
    private const string AuthorizePath = "/connect/authorize";
    private static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(15);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/signin", ShowEmailPage).ExcludeFromDescription();
        app.MapPost("/signin/email", PostEmail).ExcludeFromDescription();
        app.MapPost("/signin/verify", PostVerify).ExcludeFromDescription();
        app.MapGet("/signin/confirm", ShowConfirmPage).ExcludeFromDescription();
        app.MapGet("/signin/external/{provider}", StartExternal).ExcludeFromDescription();
        app.MapMethods("/callback/login/{provider}", ["GET", "POST"], ExternalCallback).ExcludeFromDescription();
        app.MapMethods(AuthorizePath, ["GET", "POST"], Authorize).ExcludeFromDescription();
        app.MapPost("/connect/token", Exchange).ExcludeFromDescription();
    }

    // ---- Sign-in pages --------------------------------------------------------------------------------

    private static IResult ShowEmailPage(HttpContext http, IAntiforgery antiforgery, IOptions<ProvidersOptions> providers,
        [FromQuery(Name = "ReturnUrl")] string? returnUrl) =>
        SignInPages.EmailPage(http, antiforgery, ExternalProviders.Enabled(providers.Value), SafeReturn(returnUrl));

    private static async Task<IResult> PostEmail(HttpContext http, IAntiforgery antiforgery, IOptions<ProvidersOptions> providers,
        SignInThrottle throttle, EmailCodeService codes, [FromForm] string? email, [FromForm] string? returnUrl)
    {
        var ret = SafeReturn(returnUrl);
        var enabled = ExternalProviders.Enabled(providers.Value);
        if (!throttle.TryAcquire(http.Connection.RemoteIpAddress?.ToString()))
        {
            return SignInPages.EmailPage(http, antiforgery, enabled, ret, "Too many sign-in attempts from this network. Try again in an hour.", StatusCodes.Status429TooManyRequests);
        }

        if (!TryParseEmail(email, out var address))
        {
            return SignInPages.EmailPage(http, antiforgery, enabled, ret, "Enter a valid email address.", StatusCodes.Status400BadRequest);
        }

        // The same page follows whether or not the address already has an account.
        return await codes.IssueAsync(address, http.RequestAborted) switch
        {
            CodeIssue.Sent => SignInPages.CodePage(http, antiforgery, address, ret),
            CodeIssue.RateLimited => SignInPages.EmailPage(http, antiforgery, enabled, ret, "Too many codes were sent to that address. Wait a while and try again.", StatusCodes.Status429TooManyRequests),
            _ => SignInPages.EmailPage(http, antiforgery, enabled, ret, "We could not send the code. Try again in a moment.", StatusCodes.Status503ServiceUnavailable),
        };
    }

    private static async Task<IResult> PostVerify(HttpContext http, IAntiforgery antiforgery, IOptions<ProvidersOptions> providers,
        SignInThrottle throttle, EmailCodeService codes, AccountService accounts, IDataProtectionProvider protection,
        [FromForm] string? email, [FromForm] string? code, [FromForm] string? returnUrl)
    {
        var ret = SafeReturn(returnUrl);
        var enabled = ExternalProviders.Enabled(providers.Value);
        if (!throttle.TryAcquire(http.Connection.RemoteIpAddress?.ToString()))
        {
            return SignInPages.EmailPage(http, antiforgery, enabled, ret, "Too many sign-in attempts from this network. Try again in an hour.", StatusCodes.Status429TooManyRequests);
        }

        if (!TryParseEmail(email, out var address))
        {
            return SignInPages.EmailPage(http, antiforgery, enabled, ret, "Enter a valid email address.", StatusCodes.Status400BadRequest);
        }

        var digits = (code ?? string.Empty).Trim();
        if (digits.Length != 6 || !digits.All(char.IsAsciiDigit))
        {
            return SignInPages.CodePage(http, antiforgery, address, ret, error: "The code is 6 digits.", status: StatusCodes.Status400BadRequest);
        }

        switch (await codes.VerifyAsync(address, digits, http.RequestAborted))
        {
            case CodeCheck.Wrong:
                return SignInPages.CodePage(http, antiforgery, address, ret, error: "That code is not right. Check it and try again.", status: StatusCodes.Status400BadRequest);
            case CodeCheck.NoLiveCode:
                return SignInPages.EmailPage(http, antiforgery, enabled, ret, "That code has expired or can no longer be used. Ask for a new one.", StatusCodes.Status400BadRequest);
        }

        var pending = ReadLink(http, protection);
        var account = pending is not null && pending.Email == address
            ? await accounts.LinkExternalAsync(address, pending.Provider, pending.Subject, http.RequestAborted)
            : await accounts.SignInWithEmailAsync(address, http.RequestAborted);
        http.Response.Cookies.Delete(SignInCookie.LinkCookie);
        if (account is null)
        {
            return SignInPages.MessagePage("Account unavailable", "This account was deleted.", StatusCodes.Status403Forbidden);
        }

        await SignInAsync(http, account);
        return ret is null ? SignInPages.MessagePage("You are signed in", "You can close this tab and return to Chaos.") : Results.Redirect(ret);
    }

    private static IResult ShowConfirmPage(HttpContext http, IAntiforgery antiforgery, IDataProtectionProvider protection,
        [FromQuery(Name = "ReturnUrl")] string? returnUrl)
    {
        var pending = ReadLink(http, protection);
        return pending is null
            ? Results.Redirect("/signin" + (SafeReturn(returnUrl) is { } ret ? "?ReturnUrl=" + Uri.EscapeDataString(ret) : string.Empty))
            : SignInPages.CodePage(http, antiforgery, pending.Email, SafeReturn(returnUrl),
                notice: $"That {pending.Provider} account uses an email address that already has a Chaos account. To confirm it is yours, enter the code we sent to");
    }

    // ---- External providers ---------------------------------------------------------------------------

    private static IResult StartExternal(string provider, IOptions<ProvidersOptions> providers, [FromQuery(Name = "ReturnUrl")] string? returnUrl)
    {
        var info = ExternalProviders.Enabled(providers.Value).FirstOrDefault(p => p.Key == provider);
        if (info is null)
        {
            return Results.NotFound();
        }

        var properties = new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) ?? "/signin" };
        properties.Items[OpenIddictClientAspNetCoreConstants.Properties.ProviderName] = info.Name;
        return Results.Challenge(properties, [OpenIddictClientAspNetCoreDefaults.AuthenticationScheme]);
    }

    private static async Task<IResult> ExternalCallback(HttpContext http, string provider, IOptions<ProvidersOptions> providers,
        AccountService accounts, EmailCodeService codes, IDataProtectionProvider protection)
    {
        var info = ExternalProviders.Enabled(providers.Value).FirstOrDefault(p => p.Key == provider);
        var result = await http.AuthenticateAsync(OpenIddictClientAspNetCoreDefaults.AuthenticationScheme);
        var principal = result.Principal;
        var subject = principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (info is null || principal is null || string.IsNullOrEmpty(subject))
        {
            return SignInPages.MessagePage("Sign-in failed", "The sign-in did not complete. Close this tab and try again from Chaos.", StatusCodes.Status400BadRequest);
        }

        var email = principal.FindFirst(ClaimTypes.Email)?.Value ?? principal.FindFirst(Claims.Email)?.Value;
        var verified = string.Equals(principal.FindFirst(Claims.EmailVerified)?.Value, "true", StringComparison.OrdinalIgnoreCase);
        var ret = SafeReturn(result.Properties?.RedirectUri);
        var login = await accounts.SignInExternalAsync(info.Key, subject, email, verified, http.RequestAborted);
        switch (login.Outcome)
        {
            case ExternalLoginOutcome.SignedIn:
                await SignInAsync(http, login.Account!);
                return ret is null ? SignInPages.MessagePage("You are signed in", "You can close this tab and return to Chaos.") : Results.Redirect(ret);
            case ExternalLoginOutcome.NeedsEmailConfirmation:
                var address = AccountService.NormalizeEmail(email!);
                // Linking waits for an email code to that address; the pending login rides in a short-lived protected cookie.
                if (await codes.IssueAsync(address, http.RequestAborted) != CodeIssue.Sent)
                {
                    return SignInPages.MessagePage("Sign-in failed", "We could not send the confirmation code. Try again in a moment.", StatusCodes.Status503ServiceUnavailable);
                }

                WriteLink(http, protection, new PendingLink(info.Key, subject, address));
                return Results.Redirect("/signin/confirm" + (ret is null ? string.Empty : "?ReturnUrl=" + Uri.EscapeDataString(ret)));
            default:
                return SignInPages.MessagePage("Sign-in failed", $"{info.Display} did not share a verified email address, so Chaos cannot create an account from it. Use an email code instead.", StatusCodes.Status403Forbidden);
        }
    }

    // ---- OAuth endpoints ------------------------------------------------------------------------------

    private static async Task<IResult> Authorize(HttpContext http, ChaosDbContext db)
    {
        var request = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");
        var session = await http.AuthenticateAsync(SignInCookie.Scheme);
        // prompt=login asks for a new proof of identity even when a sign-in cookie exists (account deletion needs a fresh one).
        var forceLogin = request.HasPromptValue(PromptValues.Login);
        if (forceLogin || !session.Succeeded || !Guid.TryParse(session.Principal?.FindFirst(Claims.Subject)?.Value, out var accountId))
        {
            var pairs = http.Request.HasFormContentType
                ? http.Request.Form.Select(p => KeyValuePair.Create(p.Key, (string?)p.Value.ToString()))
                : http.Request.Query.Select(p => KeyValuePair.Create(p.Key, (string?)p.Value.ToString()));
            if (forceLogin)
            {
                // The sign-in comes back here without the prompt, so it is asked for once.
                var remaining = string.Join(' ', request.GetPromptValues().Where(v => v != PromptValues.Login));
                pairs = pairs.Where(p => p.Key != Parameters.Prompt);
                if (remaining.Length > 0)
                {
                    pairs = pairs.Append(KeyValuePair.Create(Parameters.Prompt, (string?)remaining));
                }
            }

            return Results.Challenge(new AuthenticationProperties { RedirectUri = http.Request.PathBase + http.Request.Path + QueryString.Create(pairs) }, [SignInCookie.Scheme]);
        }

        if (!Guid.TryParse(request.GetParameter(DesktopClient.DeviceIdParameter)?.ToString(), out var deviceId))
        {
            return Fail(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Errors.InvalidRequest, "The device_id parameter is required.");
        }

        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId && a.DeletedAt == null, http.RequestAborted);
        if (account is null)
        {
            return Fail(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Errors.AccessDenied, "The account is no longer available.");
        }

        if (await DeviceRefusedAsync(db, accountId, deviceId, http.RequestAborted))
        {
            return Fail(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Errors.AccessDenied, "This device was removed from the account.");
        }

        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, account.Id.ToString("D"));
        identity.SetClaim(Claims.Email, account.PrimaryEmail);
        identity.SetClaim(DesktopClient.DeviceIdClaim, deviceId.ToString("D"));
        var signedAt = session.Properties?.IssuedUtc ?? DateTimeOffset.UnixEpoch;
        // OpenIddict reads auth_time as a number, so it is typed as one.
        identity.AddClaim(new Claim(DesktopClient.AuthTimeClaim, signedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes());
        principal.SetDestinations(_ => [Destinations.AccessToken]);
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    private static async Task<IResult> Exchange(HttpContext http, ChaosDbContext db)
    {
        var request = http.GetOpenIddictServerRequest() ?? throw new InvalidOperationException("The OpenID Connect request cannot be retrieved.");
        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            return Fail(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Errors.UnsupportedGrantType, "The specified grant type is not supported.");
        }

        var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        var principal = result.Principal;
        if (principal is null
            || !Guid.TryParse(principal.GetClaim(Claims.Subject), out var accountId)
            || !Guid.TryParse(principal.GetClaim(DesktopClient.DeviceIdClaim), out var deviceId))
        {
            return Fail(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Errors.InvalidGrant, "The token is no longer valid.");
        }

        // A deleted account or a revoked device stops at the next refresh (access tokens last 15 minutes at most).
        if (!await db.Accounts.AnyAsync(a => a.Id == accountId && a.DeletedAt == null, http.RequestAborted))
        {
            return Fail(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Errors.InvalidGrant, $"The token is no longer valid ({DesktopClient.AccountDeletedCode}).");
        }

        if (await DeviceRefusedAsync(db, accountId, deviceId, http.RequestAborted))
        {
            return Fail(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Errors.InvalidGrant, $"The token is no longer valid ({DesktopClient.DeviceRevokedCode}).");
        }

        principal.SetDestinations(_ => [Destinations.AccessToken]);
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>True when the device was revoked, or belongs to another account. A device not registered yet is fine.</summary>
    internal static async Task<bool> DeviceRefusedAsync(ChaosDbContext db, Guid accountId, Guid deviceId, CancellationToken ct)
    {
        var known = await db.Devices.AsNoTracking().Where(d => d.Id == deviceId).Select(d => new { d.AccountId, d.RevokedAt }).FirstOrDefaultAsync(ct);
        return known is not null && (known.AccountId != accountId || known.RevokedAt is not null);
    }

    private static IResult Fail(string scheme, string error, string description) =>
        Results.Forbid(new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), [scheme]);

    // ---- Helpers --------------------------------------------------------------------------------------

    private static async Task SignInAsync(HttpContext http, Account account)
    {
        var identity = new ClaimsIdentity(SignInCookie.Scheme, Claims.Name, Claims.Role);
        identity.AddClaim(new Claim(Claims.Subject, account.Id.ToString("D")));
        identity.AddClaim(new Claim(Claims.Name, account.PrimaryEmail));
        await http.SignInAsync(SignInCookie.Scheme, new ClaimsPrincipal(identity));
    }

    /// <summary>Only the authorize endpoint may be returned to: no open redirect.</summary>
    internal static string? SafeReturn(string? returnUrl) =>
        returnUrl is not null && returnUrl.StartsWith(AuthorizePath + "?", StringComparison.Ordinal) && returnUrl.Length < 4096 ? returnUrl : null;

    private static bool TryParseEmail(string? value, out string address)
    {
        address = string.Empty;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 254 || !MailAddress.TryCreate(trimmed, out var parsed) || parsed.Address != trimmed)
        {
            return false;
        }

        address = AccountService.NormalizeEmail(trimmed);
        return true;
    }

    private sealed record PendingLink(string Provider, string Subject, string Email);

    private static void WriteLink(HttpContext http, IDataProtectionProvider protection, PendingLink link)
    {
        var protector = protection.CreateProtector("chaos.link.v1").ToTimeLimitedDataProtector();
        http.Response.Cookies.Append(SignInCookie.LinkCookie, protector.Protect(JsonSerializer.Serialize(link), LinkLifetime), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            MaxAge = LinkLifetime,
        });
    }

    private static PendingLink? ReadLink(HttpContext http, IDataProtectionProvider protection)
    {
        if (!http.Request.Cookies.TryGetValue(SignInCookie.LinkCookie, out var value))
        {
            return null;
        }

        try
        {
            var protector = protection.CreateProtector("chaos.link.v1").ToTimeLimitedDataProtector();
            return JsonSerializer.Deserialize<PendingLink>(protector.Unprotect(value));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
