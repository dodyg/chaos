using System.Security.Claims;
using Chaos.Server.Auth;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Chaos.Server.Api;

/// <summary>The one place the signed-in account and device are read from a token's claims.</summary>
internal static class CallerIdentity
{
    public static (Guid AccountId, Guid DeviceId)? TryGet(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirst(Claims.Subject)?.Value, out var account) && Guid.TryParse(user.FindFirst(DesktopClient.DeviceIdClaim)?.Value, out var device)
            ? (account, device)
            : null;
}
