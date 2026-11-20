using System.ComponentModel;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using ModelContextProtocol.Server;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using OpenIddict.Server.AspNetCore;
using OpenIddict.Validation.AspNetCore;

// Disposable local experiment: degraded mode has no persistent registration or consent UI.
const string clientId = "phase3-probe";
const string redirect = "http://127.0.0.1:55442/callback";
var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
builder.Services.AddAuthorization();
builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
builder.Services.AddOpenIddict()
    .AddServer(options =>
    {
        options.EnableDegradedMode();
        options.SetAuthorizationEndpointUris("/authorize").SetTokenEndpointUris("/token");
        options.AllowAuthorizationCodeFlow().RequireProofKeyForCodeExchange();
        options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
        options.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(handler => handler.UseInlineHandler(context =>
        {
            if (context.ClientId != clientId || context.RedirectUri != redirect)
                context.Reject(OpenIddictConstants.Errors.InvalidClient);
            return default;
        }));
        options.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(handler => handler.UseInlineHandler(context =>
        {
            if (context.ClientId != clientId) context.Reject(OpenIddictConstants.Errors.InvalidClient);
            return default;
        }));
        options.UseAspNetCore().DisableTransportSecurityRequirement()
            .EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
    })
    .AddValidation(options => { options.UseLocalServer(); options.UseAspNetCore(); });
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/authorize", () =>
{
    var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    identity.AddClaim(OpenIddictConstants.Claims.Subject, "spike-user");
    return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
});
app.MapPost("/token", async (HttpContext context) =>
{
    var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    if (result.Principal is null) return Results.Unauthorized();
    return Results.SignIn(result.Principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
});
app.MapMcp("/mcp").RequireAuthorization();
app.Run();

[McpServerToolType]
public static class SpikeTools
{
    [McpServerTool, Description("Return a greeting for a name.")]
    public static string Greet(string name) => $"Hello, {name}!";
}
