using System.Net;
using Chaos.Server.Data;
using Chaos.ServiceDefaults;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Chaos.Server.Tests;

/// <summary>PLAN2 §8.7: the hosted server answers its health endpoints, and refuses to start
/// against a database that has not been migrated.</summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class ServerHostTests(PostgresServer postgres)
{
    [Test]
    public async Task HealthLive_MigratedDatabase_ReturnsOk()
    {
        await using var factory = await CreateFactoryAsync(migrate: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ServiceDefaultsExtensions.LivePath);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("Healthy");
    }

    [Test]
    public async Task HealthReady_MigratedDatabase_ReturnsOk()
    {
        await using var factory = await CreateFactoryAsync(migrate: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ServiceDefaultsExtensions.ReadyPath);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Startup_UnmigratedDatabase_Refuses()
    {
        await using var factory = await CreateFactoryAsync(migrate: false);

        await Assert.That(() => factory.CreateClient()).Throws<ServerSchemaException>();
    }

    private async Task<WebApplicationFactory<Program>> CreateFactoryAsync(bool migrate)
    {
        var connectionString = migrate
            ? await ServerDatabase.CreateMigratedAsync(postgres)
            : await postgres.CreateDatabaseAsync();

        return new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.UseSetting($"ConnectionStrings:{ServerResources.DatabaseName}", connectionString));
    }
}
