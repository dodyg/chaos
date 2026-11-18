using Chaos.Server;
using Chaos.Server.Api;
using Chaos.Server.Auth;
using Chaos.Server.Blobs;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Server.Web;
using Chaos.ServiceDefaults;
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

// Sign-in pages and token responses are never cached or framed, and load nothing from elsewhere.
app.Use(async (http, next) =>
{
    http.Response.OnStarting(() =>
    {
        var headers = http.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'";
        if (!http.Request.Path.StartsWithSegments("/health"))
        {
            headers.CacheControl = "no-store";
        }

        return Task.CompletedTask;
    });
    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapDefaultEndpoints();
app.MapAuthEndpoints();
app.MapServiceEndpoints();
app.MapWebSessionEndpoints().MapWebReads();

await app.RunAsync();

/// <summary>Entry point type, public so the server tests can host it.</summary>
public partial class Program;
