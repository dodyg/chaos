using Chaos.Server;
using Chaos.Server.Api;
using Chaos.Server.Auth;
using Chaos.Server.Blobs;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Server.Web;
using Chaos.ServiceDefaults;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

// The Chaos online service (PLAN2 §8). `Chaos.Server migrate` applies the EF Core migrations and
// exits; without it the service starts only against a database that is exactly current (§8.7).
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// OpenIddict logs whole token requests (codes, verifiers, refresh tokens) at Information (PLAN2 §8.5:
// never log tokens). Fixed in code so configuration cannot turn it back on by accident.
builder.Logging.AddFilter("OpenIddict", LogLevel.Warning);

// ConnectionStrings:chaos (ConnectionStrings__Chaos in the container host, injected by Aspire in
// development). Aspire registers the context with its PostgreSQL readiness check and telemetry.
builder.AddNpgsqlDbContext<ChaosDbContext>(
    ServerResources.DatabaseName,
    configureDbContextOptions: options => options.UseOpenIddict());

builder.Services.AddChaosAuth(builder.Configuration, builder.Environment);
builder.Services.AddServiceRateLimits();
builder.Services.AddSyncService(builder.Configuration);
builder.Services.AddBlobService(builder.Configuration, builder.Environment);
builder.Services.AddWebSessions(builder.Environment);
builder.Services.AddScoped<WebReadModel>();
builder.Services.AddScoped<WebTaskWrites>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChaosDbContext>();
    if (args is ["migrate", ..])
    {
        var latest = await ServerSchemaGate.MigrateAsync(db, app.Lifetime.ApplicationStopping);
        app.Logger.LogInformation("Server database is at migration {Migration}", latest ?? "(none)");
        return;
    }

    await ServerSchemaGate.EnsureCurrentAsync(db, app.Lifetime.ApplicationStopping);
    await AuthSetup.EnsureDesktopClientAsync(scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>(), app.Lifetime.ApplicationStopping);
}

if (!app.Environment.IsDevelopment())
{
    // TLS only (PLAN2 §8.5). Behind a TLS-terminating proxy, enable forwarded headers on the host
    // (ASPNETCORE_FORWARDEDHEADERS_ENABLED=true) so the redirect sees the original scheme.
    app.UseHsts();
    app.UseHttpsRedirection();
}

// Sign-in pages and API responses are never cached or framed. The app shell loads same-origin assets.
app.Use(async (http, next) =>
{
    var path = http.Request.Path;
    var isApp = path.StartsWithSegments(WebSession.AppPath);
    var isBundledAsset = path.StartsWithSegments("/app/assets");
    if (HttpMethods.IsPost(http.Request.Method) && path.StartsWithSegments(WebSession.ApiPath) &&
        http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
    {
        size.MaxRequestBodySize = 8192;
    }

    http.Response.OnStarting(() =>
    {
        var headers = http.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] = isApp
            ? "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'"
            : "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'";
        if (!path.StartsWithSegments("/health"))
        {
            headers.CacheControl = isBundledAsset && http.Response.StatusCode == StatusCodes.Status200OK
                ? "public, max-age=31536000, immutable"
                : "no-store";
        }

        return Task.CompletedTask;
    });
    await next();
});

app.UseWhen(http => http.Request.Path.StartsWithSegments(WebSession.AppPath), mobile =>
{
    var contentTypes = new FileExtensionContentTypeProvider();
    contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
    mobile.UseDefaultFiles();
    mobile.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = contentTypes });
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapDefaultEndpoints();
app.MapAuthEndpoints();
app.MapServiceEndpoints();
app.MapWebSessionEndpoints().MapWebReads().MapWebWrites();

await app.RunAsync();

/// <summary>Entry point type, public so the server tests can host it.</summary>
public partial class Program;
