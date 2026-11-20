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
builder.Services.AddServiceRateLimits().AddAccountServices();
builder.Services.AddSyncService(builder.Configuration);
builder.Services.AddBlobService(builder.Configuration, builder.Environment);
builder.Services.AddWebSessions(builder.Environment);
builder.Services.AddScoped<WebReadModel>();
builder.Services.AddScoped<WebTaskWrites>();

builder.Services.AddProblemDetails();
builder.Services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto;
    foreach (var proxy in builder.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [])
    {
        options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    }

    foreach (var network in builder.Configuration.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? [])
    {
        var parts = network.Split('/');
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(System.Net.IPAddress.Parse(parts[0]), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)));
    }
});

var app = builder.Build();

// Sign-in throttling and device rate limits key on the client address, so behind a proxy every user would share one bucket
// unless the proxy is trusted. Outside development the host must say which proxies it trusts, or that it has none.
if (!app.Environment.IsDevelopment())
{
    var proxies = app.Configuration.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [];
    var networks = app.Configuration.GetSection("Proxy:KnownNetworks").Get<string[]>() ?? [];
    if (proxies.Length == 0 && networks.Length == 0 && !app.Configuration.GetValue<bool>("Proxy:Direct"))
    {
        throw new InvalidOperationException("Set Proxy:KnownProxies or Proxy:KnownNetworks to the reverse proxies in front of this server, or Proxy:Direct=true when clients connect to it directly.");
    }

    // Invite emails link to this address. Without it the link would come from the request's Host header, which the caller picks.
    if (string.IsNullOrWhiteSpace(app.Configuration[$"{SharingOptions.Section}:{nameof(SharingOptions.PublicBaseUrl)}"]))
    {
        throw new InvalidOperationException("Set Sharing:PublicBaseUrl to this service's public address, such as https://chaos.example.com.");
    }
}

app.UseExceptionHandler();
app.UseForwardedHeaders();

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

app.UseSecurityHeaders();

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
app.MapInvitePages();
app.MapWebSessionEndpoints().MapWebReads().MapWebWrites();

await app.RunAsync();

/// <summary>Entry point type, public so the server tests can host it.</summary>
public partial class Program;
