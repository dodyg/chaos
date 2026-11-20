using System.Security.Claims;
using Chaos.Server.Api;
using Chaos.Server.Auth;
using Chaos.Server.Data;
using Chaos.Server.Web;
using Chaos.Sync;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Chaos.Server.Sync;

public static class SpaceEndpoints
{
    public static void MapSpaceEndpoints(this RouteGroupBuilder v2)
    {
        v2.MapPost("/spaces", async (ClaimsPrincipal user, CreateSpaceRequest request, SpaceService spaces, CancellationToken ct) =>
            Http(await spaces.CreateAsync(Account(user), request, Device(user), ct)));
        v2.MapDelete("/spaces/{spaceId:guid}", async (ClaimsPrincipal user, Guid spaceId, SpaceService spaces, CancellationToken ct) =>
            Http(await spaces.DeleteAsync(Account(user), Device(user), spaceId, ct)));
        v2.MapPost("/spaces/{spaceId:guid}/restore", async (ClaimsPrincipal user, Guid spaceId, SpaceService spaces, CancellationToken ct) =>
            Http(await spaces.RestoreAsync(Account(user), Device(user), spaceId, ct)));
        v2.MapGet("/spaces/{spaceId:guid}/members", async (ClaimsPrincipal user, Guid spaceId, SpaceService spaces, CancellationToken ct) =>
            Http(await spaces.ListMembersAsync(Account(user), spaceId, ct)));
        v2.MapPatch("/spaces/{spaceId:guid}/members/{memberId:guid}",
            async (ClaimsPrincipal user, Guid spaceId, Guid memberId, MemberRoleRequest request, SpaceService spaces, CancellationToken ct) =>
                Http(await spaces.ChangeRoleAsync(Account(user), Device(user), spaceId, memberId, request.Role, ct)));
        v2.MapDelete("/spaces/{spaceId:guid}/members/{memberId:guid}",
            async (ClaimsPrincipal user, Guid spaceId, Guid memberId, SpaceService spaces, CancellationToken ct) =>
                Http(await spaces.RemoveAsync(Account(user), Device(user), spaceId, memberId, ct)));
        v2.MapPost("/spaces/{spaceId:guid}/leave", async (ClaimsPrincipal user, Guid spaceId, SpaceService spaces, CancellationToken ct) =>
            Http(await spaces.RemoveCurrentAsync(Account(user), Device(user), spaceId, ct)));
        v2.MapPatch("/spaces/{spaceId:guid}/members/me",
            async (ClaimsPrincipal user, Guid spaceId, MemberProfileRequest request, SpaceService spaces, CancellationToken ct) =>
                Http(await spaces.UpdateProfileAsync(Account(user), spaceId, request, ct)));
        v2.MapPost("/spaces/{spaceId:guid}/invites",
            async (HttpContext http, ClaimsPrincipal user, Guid spaceId, CreateInviteRequest request, SpaceService spaces, CancellationToken ct) =>
                Http(await spaces.CreateInviteAsync(Account(user), Device(user), spaceId, request, BaseUrl(http), ct)));
        v2.MapGet("/spaces/{spaceId:guid}/invites", async (ClaimsPrincipal user, Guid spaceId, SpaceService spaces, CancellationToken ct) =>
            Http(await spaces.ListInvitesAsync(Account(user), spaceId, ct)));
        v2.MapDelete("/spaces/{spaceId:guid}/invites/{inviteId:guid}",
            async (ClaimsPrincipal user, Guid spaceId, Guid inviteId, SpaceService spaces, CancellationToken ct) =>
                Http(await spaces.RevokeInviteAsync(Account(user), Device(user), spaceId, inviteId, ct)));
        v2.MapPost("/invites/accept", Accept);
        v2.MapPost("/spaces/{spaceId:guid}/moves",
            async (ClaimsPrincipal user, Guid spaceId, MoveRequest request, SpaceService spaces, CancellationToken ct) =>
                Http(await spaces.MoveAsync(Account(user), Device(user), spaceId, request, ct)));
    }

    public static void MapInvitePages(this WebApplication app)
    {
        app.MapGet("/invite/{token}", ShowInvite);
        app.MapPost("/invite/{token}", AcceptInvitePage).AddEndpointFilter<WebAntiforgery>();
    }

    private static async Task<IResult> Accept(HttpContext http, ClaimsPrincipal user, AcceptInviteRequest request,
        InviteAcceptanceLimiter limiter, SpaceService spaces, CancellationToken ct)
    {
        var account = Account(user);
        if (!limiter.TryAcquire(account, http.Connection.RemoteIpAddress?.ToString()))
        {
            return ApiErrors.Problem(SyncRejection.RateLimited, "Too many invite attempts. Try again in an hour.",
                StatusCodes.Status429TooManyRequests);
        }
        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > 200)
        {
            return ApiErrors.Problem("invite-invalid", "That invitation is not valid.", StatusCodes.Status400BadRequest);
        }
        return Http(await spaces.AcceptAsync(account, Device(user), token, ct));
    }

    private static async Task<IResult> ShowInvite(HttpContext http, string token, ChaosDbContext db, CancellationToken ct)
    {
        var session = await http.AuthenticateAsync(SignInCookie.Scheme);
        if (!session.Succeeded || !Guid.TryParse(session.Principal?.FindFirst(Claims.Subject)?.Value, out var accountId))
        {
            return SignInPages.InviteLanding("/invite/" + Uri.EscapeDataString(token));
        }
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
        var invite = await db.SpaceInvites.AsNoTracking().Where(i => i.TokenHash == hash && i.RevokedAt == null
            && (i.ExpiresAt == null || i.ExpiresAt > TimeProvider.System.GetUtcNow())
            && (i.MaxUses == null || i.Uses < i.MaxUses))
            .Select(i => new { i.SpaceId, i.CreatedByMemberId }).SingleOrDefaultAsync(ct);
        if (invite is null)
        {
            return SignInPages.MessagePage("Invitation unavailable", "That invitation is expired or is no longer available.",
                StatusCodes.Status400BadRequest);
        }
        var space = await db.SyncEntities.AsNoTracking().Where(e => e.AccountId == invite.SpaceId && e.Kind == "space")
            .Select(e => e.Groups).SingleOrDefaultAsync(ct);
        var inviter = await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == invite.SpaceId
            && m.MemberId == invite.CreatedByMemberId).Select(m => m.DisplayName).SingleOrDefaultAsync(ct);
        var name = space is null ? "Shared space" : SyncName(space);
        return SignInPages.InviteJoin(http, accountId, token, name, inviter ?? "A Chaos member");
    }

    private static async Task<IResult> AcceptInvitePage(HttpContext http, string token, SpaceService spaces,
        InviteAcceptanceLimiter limiter, CancellationToken ct)
    {
        var session = await http.AuthenticateAsync(SignInCookie.Scheme);
        if (!session.Succeeded || !Guid.TryParse(session.Principal?.FindFirst(Claims.Subject)?.Value, out var accountId))
        {
            return Results.Unauthorized();
        }
        if (!limiter.TryAcquire(accountId, http.Connection.RemoteIpAddress?.ToString()))
        {
            return SignInPages.MessagePage("Invitation", "Too many invite attempts. Try again in an hour.",
                StatusCodes.Status429TooManyRequests);
        }
        var result = await spaces.AcceptAsync(accountId, null, token, ct);
        return result.Succeeded
            ? SignInPages.MessagePage("You're in", "Open Chaos on your computer, or continue on the web.")
            : SignInPages.MessagePage("Invitation", result.Detail ?? "That invitation could not be accepted.",
                StatusFor(result.Error!));
    }

    private static Guid Account(ClaimsPrincipal user) => Guid.Parse(user.FindFirst(Claims.Subject)!.Value);
    private static Guid Device(ClaimsPrincipal user) => Guid.Parse(user.FindFirst(DesktopClient.DeviceIdClaim)!.Value);
    private static string BaseUrl(HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}";
    private static string SyncName(string json) => SyncJson.ReadGroups(json).GetValueOrDefault("name")?
        .Values.Values.GetValueOrDefault("Name")?.ToString() ?? "Shared space";

    private static IResult Http<T>(SpaceOperation<T> operation) => operation.Succeeded
        ? Results.Ok(operation.Value)
        : ApiErrors.Problem(operation.Error!, operation.Detail ?? "The request was refused.", StatusFor(operation.Error!));

    private static int StatusFor(string code) => code switch
    {
        SyncRejection.NotMember or SyncRejection.Forbidden => StatusCodes.Status403Forbidden,
        SyncRejection.RateLimited => StatusCodes.Status429TooManyRequests,
        "space-unavailable" or "member-not-found" or "container-not-found" or "invite-not-found" => StatusCodes.Status404NotFound,
        "invite-expired" or "invite-used" or "invite-revoked" or "invite-invalid" or "invite-email-mismatch"
            or "invalid-space" or "invalid-role" or "invalid-invite" or "invalid-email" or "invalid-member" => StatusCodes.Status400BadRequest,
        "email-unavailable" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status409Conflict,
    };

}
