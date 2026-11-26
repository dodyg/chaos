using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Chaos.Server.Data;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Chaos.Server.Auth;

/// <summary>
/// The OAuth client every MCP assistant uses against <c>/mcp</c> (PLAN3 12.1, the Q0 spike 2): a public client, authorization
/// code with PKCE and loopback redirects. Where the spike hard-wired one redirect, this client is stored like the desktop's,
/// the <c>/connect/register</c> endpoint answers an assistant's dynamic registration with it after checking the redirect
/// addresses, and the consent page makes the account's owner pick which of their agents the token acts for. The agent is the
/// <c>agent_id</c> claim; a token without it, or the desktop's token, never reaches the tools.
/// </summary>
public static class McpOAuth
{
    public const string ClientId = "chaos-mcp";
    public const string Scope = "mcp";
    public const string AgentIdClaim = "agent_id";
    public const string Path = "/mcp";
    public const string RegisterPath = "/connect/register";

    /// <summary>The hosted assistant that connects to remote servers from the cloud (Claude's custom connectors).</summary>
    public const string HostedRedirect = "https://claude.ai/api/mcp/auth_callback";

    private static readonly string[] LoopbackHosts = ["127.0.0.1", "[::1]", "localhost"];
    private static readonly string[] LoopbackPaths = ["/callback", "/"];

    /// <summary>Creates or updates the client.</summary>
    public static async Task EnsureClientAsync(IOpenIddictApplicationManager manager, CancellationToken ct)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "MCP assistant",
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                Permissions.Prefixes.Scope + Scope,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        foreach (var host in LoopbackHosts)
        {
            foreach (var path in LoopbackPaths)
            {
                descriptor.RedirectUris.Add(new Uri("http://" + host + path));
            }
        }

        descriptor.RedirectUris.Add(new Uri(HostedRedirect));
        if (await manager.FindByClientIdAsync(ClientId, ct) is { } existing)
        {
            await manager.UpdateAsync(existing, descriptor, ct);
        }
        else
        {
            await manager.CreateAsync(descriptor, ct);
        }
    }

    /// <summary>The service's public address: the configured one, else the one this request arrived on.</summary>
    public static string BaseUrl(HttpContext http)
    {
        var configured = http.RequestServices.GetRequiredService<IOptions<SharingOptions>>().Value.PublicBaseUrl;
        return (string.IsNullOrWhiteSpace(configured) ? $"{http.Request.Scheme}://{http.Request.Host}" : configured).TrimEnd('/');
    }

    public static void MapMcpAuth(this WebApplication app)
    {
        app.MapGet("/.well-known/oauth-protected-resource", ProtectedResource).ExcludeFromDescription();
        app.MapGet("/.well-known/oauth-protected-resource" + Path, ProtectedResource).ExcludeFromDescription();
        app.MapPost(RegisterPath, Register).ExcludeFromDescription();
    }

    private static IResult ProtectedResource(HttpContext http) =>
        Results.Json(new Dictionary<string, object?>
        {
            ["resource"] = BaseUrl(http) + Path,
            ["authorization_servers"] = new[] { BaseUrl(http) },
            ["scopes_supported"] = new[] { Scope },
            ["bearer_methods_supported"] = new[] { "header" },
        });

    /// <summary>
    /// RFC 7591 for public clients: nothing is stored. The answer is the one shared client, and only when every redirect
    /// address is one the client already accepts (a loopback address on any port, or the hosted assistant's callback).
    /// </summary>
    private static async Task<IResult> Register(HttpContext http, IOpenIddictApplicationManager manager)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, http.RequestAborted)) > 0)
        {
            if (buffer.Length + read > 16 * 1024)
            {
                return RegistrationError("invalid_client_metadata");
            }

            buffer.Write(chunk, 0, read);
        }

        string[] redirects;
        string? name = null;
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("redirect_uris", out var list) || list.ValueKind != JsonValueKind.Array
                || list.GetArrayLength() is 0 or > 10 || list.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
            {
                return RegistrationError("invalid_redirect_uri");
            }

            redirects = [.. list.EnumerateArray().Select(item => item.GetString()!)];
            if (root.TryGetProperty("client_name", out var title) && title.ValueKind == JsonValueKind.String)
            {
                name = title.GetString() is { Length: <= 100 } text ? text : null;
            }
        }
        catch (JsonException)
        {
            return RegistrationError("invalid_client_metadata");
        }

        var application = await manager.FindByClientIdAsync(ClientId, http.RequestAborted);
        if (application is null)
        {
            return RegistrationError("server_error", StatusCodes.Status503ServiceUnavailable);
        }

        foreach (var redirect in redirects)
        {
            if (!await manager.ValidateRedirectUriAsync(application, redirect, http.RequestAborted))
            {
                return RegistrationError("invalid_redirect_uri");
            }
        }

        return Results.Json(new Dictionary<string, object?>
        {
            ["client_id"] = ClientId,
            ["client_id_issued_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["client_name"] = name,
            ["redirect_uris"] = redirects,
            ["token_endpoint_auth_method"] = "none",
            ["grant_types"] = new[] { "authorization_code", "refresh_token" },
            ["response_types"] = new[] { "code" },
            ["scope"] = Scope + " " + Scopes.OfflineAccess,
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult RegistrationError(string error, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new Dictionary<string, object?> { ["error"] = error }, statusCode: status);

    // ---- The authorize step: pick the agent ---------------------------------------------------------

    private static readonly HashSet<string> OwnFields = new(StringComparer.Ordinal) { "agent_id", "decision", "consent" };

    /// <summary>
    /// The authorize request of an assistant, after sign-in: a page where the account's owner picks which of their agents the
    /// token acts for, then the authorization code for it. Only an agent the signed-in account owns, and has not removed, can be picked.
    /// </summary>
    internal static async Task<IResult> AuthorizeAsync(HttpContext http, ChaosDbContext db, IAntiforgery antiforgery, OpenIddictRequest request,
        Guid accountId, DateTimeOffset signedAt)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId && a.DeletedAt == null, http.RequestAborted);
        if (account is null)
        {
            return Fail(Errors.AccessDenied, "The account is no longer available.");
        }

        var agents = await db.Agents.AsNoTracking().Where(a => a.OwnerAccountId == accountId && a.RemovedAt == null)
            .OrderBy(a => a.Name).ThenBy(a => a.Id).ToListAsync(http.RequestAborted);
        var form = http.Request.HasFormContentType ? http.Request.Form : null;
        if (form is null || !form.ContainsKey("consent"))
        {
            return ConsentPage(http, antiforgery, request, agents, null, StatusCodes.Status200OK);
        }

        try
        {
            await antiforgery.ValidateRequestAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            return ConsentPage(http, antiforgery, request, agents, "That page expired. Choose again.", StatusCodes.Status400BadRequest);
        }

        if (form["decision"] == "deny")
        {
            return Fail(Errors.AccessDenied, "The owner declined.");
        }

        if (!Guid.TryParse(form["agent_id"], out var agentId) || agents.FirstOrDefault(a => a.Id == agentId) is not { } agent)
        {
            return ConsentPage(http, antiforgery, request, agents, "Choose one of your agents.", StatusCodes.Status400BadRequest);
        }

        var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.SetClaim(Claims.Subject, account.Id.ToString("D"));
        identity.SetClaim(AgentIdClaim, agent.Id.ToString("D"));
        identity.AddClaim(new Claim(DesktopClient.AuthTimeClaim, signedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        var principal = new ClaimsPrincipal(identity);
        principal.SetScopes(request.GetScopes().Where(scope => scope is Scope or Scopes.OfflineAccess));
        principal.SetDestinations(_ => [Destinations.AccessToken]);
        return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    internal static IResult Fail(string error, string description) =>
        Results.Forbid(new Microsoft.AspNetCore.Authentication.AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }), [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);

    private static IResult ConsentPage(HttpContext http, IAntiforgery antiforgery, OpenIddictRequest request, IReadOnlyList<AgentRow> agents, string? error, int status)
    {
        var body = new StringBuilder();
        body.Append("<h1>Connect an AI assistant</h1>");
        body.Append("<p>An assistant is asking to work in your shared spaces. Choose the agent it acts as. It can then do only what that agent is allowed to do: "
            + "the spaces and lists the agent was added to, and no more.</p>");
        if (error is not null)
        {
            body.Append($"<p class=\"alert\" role=\"alert\">{Encode(error)}</p>");
        }

        if (agents.Count == 0)
        {
            body.Append("<p>You have no agents yet. Add one in Chaos under Settings → Agents, add it to a shared space, then connect the assistant again.</p>");
            return Page(body.ToString(), status);
        }

        var tokens = antiforgery.GetAndStoreTokens(http);
        body.Append("<form method=\"post\" action=\"/connect/authorize\">");
        body.Append($"<input type=\"hidden\" name=\"{Encode(tokens.FormFieldName)}\" value=\"{Encode(tokens.RequestToken ?? string.Empty)}\">");
        body.Append("<input type=\"hidden\" name=\"consent\" value=\"1\">");
        var pairs = http.Request.HasFormContentType
            ? http.Request.Form.Select(pair => KeyValuePair.Create(pair.Key, pair.Value.ToString()))
            : http.Request.Query.Select(pair => KeyValuePair.Create(pair.Key, pair.Value.ToString()));
        foreach (var (name, value) in pairs.Where(pair => !OwnFields.Contains(pair.Key) && pair.Key != tokens.FormFieldName))
        {
            body.Append($"<input type=\"hidden\" name=\"{Encode(name)}\" value=\"{Encode(value)}\">");
        }

        body.Append("<fieldset><legend>Agent</legend>");
        for (var index = 0; index < agents.Count; index++)
        {
            body.Append($"<label class=\"choice\"><input type=\"radio\" name=\"agent_id\" value=\"{agents[index].Id:D}\" {(index == 0 ? "checked" : string.Empty)}> {Encode(agents[index].Name)}"
                + (agents[index].PausedAt is null ? string.Empty : " (paused)") + "</label>");
        }

        body.Append("</fieldset><button class=\"primary\" type=\"submit\" name=\"decision\" value=\"allow\">Allow</button>");
        body.Append("<button type=\"submit\" name=\"decision\" value=\"deny\" formnovalidate>Cancel</button></form>");
        return Page(body.ToString(), status);
    }

    private static IResult Page(string body, int status) => SignInPages.MessageBody("Connect an assistant", body, status);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}
