using Chaos.Server;
using Chaos.Server.Data;
using Chaos.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

// The Chaos online service (PLAN2 §8). `Chaos.Server migrate` applies the EF Core migrations and
// exits; without it the service starts only against a database that is exactly current (§8.7).
var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// ConnectionStrings:chaos (ConnectionStrings__Chaos in the container host, injected by Aspire in
// development). Aspire registers the context with its PostgreSQL readiness check and telemetry.
builder.AddNpgsqlDbContext<ChaosDbContext>(
    ServerResources.DatabaseName,
    configureDbContextOptions: options => options.UseOpenIddict());

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
}

app.MapDefaultEndpoints();

await app.RunAsync();

/// <summary>Entry point type, public so the server tests can host it.</summary>
public partial class Program;
