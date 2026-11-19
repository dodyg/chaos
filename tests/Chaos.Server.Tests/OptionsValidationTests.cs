using Chaos.Server.Auth;
using Chaos.Server.Blobs;
using Chaos.Server.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Tests;

/// <summary>A misconfigured option must be refused with a message naming the setting, not surface later as a divide-by-zero or a hung job. Needs no database.</summary>
public sealed class OptionsValidationTests
{
    private static ServiceProvider Build(string key, string value)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        var services = new ServiceCollection();
        services.AddBlobService(configuration, builder.Environment);
        services.AddChaosAuth(configuration, builder.Environment);
        services.AddSyncService(configuration);
        return services.BuildServiceProvider();
    }

    [Test]
    [Arguments("Blob:PartSize", "0", "Blob:PartSize")]
    [Arguments("Blob:MaxBlobBytes", "0", "Blob:MaxBlobBytes")]
    [Arguments("Blob:UploadExpiry", "00:00:00", "Blob:UploadExpiry")]
    [Arguments("Clam:Port", "0", "Clam:Port")]
    [Arguments("Limits:AccountQuotaBytes", "0", "Limits:AccountQuotaBytes")]
    [Arguments("Limits:RequestsPerMinutePerDevice", "0", "Limits:RequestsPerMinutePerDevice")]
    [Arguments("SignIn:MaxCodesPerEmailPerHour", "0", "SignIn:")]
    [Arguments("Sync:ChangeLogHorizon", "00:00:00", "Sync:ChangeLogHorizon")]
    [Arguments("Sync:EventKeepAlive", "00:00:00", "Sync:EventKeepAlive")]
    public async Task A_setting_that_cannot_work_is_refused_with_its_name(string key, string value, string expectedInMessage)
    {
        using var provider = Build(key, value);
        var section = key.Split(':')[0];
        var read = () => section switch
        {
            "Blob" => (object)provider.GetRequiredService<IOptions<BlobOptions>>().Value,
            "Clam" => provider.GetRequiredService<IOptions<ClamOptions>>().Value,
            "Limits" => provider.GetRequiredService<IOptions<LimitsOptions>>().Value,
            "SignIn" => provider.GetRequiredService<IOptions<SignInOptions>>().Value,
            _ => provider.GetRequiredService<IOptions<SyncOptions>>().Value,
        };

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => Task.FromResult(read()));
        await Assert.That(error!.Message).Contains(expectedInMessage);
    }

    [Test]
    public async Task The_defaults_are_valid()
    {
        using var provider = Build("Unrelated:Setting", "x");
        await Assert.That(provider.GetRequiredService<IOptions<BlobOptions>>().Value.PartSize).IsGreaterThan(0);
        await Assert.That(provider.GetRequiredService<IOptions<LimitsOptions>>().Value.AccountQuotaBytes).IsGreaterThan(0);
        await Assert.That(provider.GetRequiredService<IOptions<SyncOptions>>().Value.ChangeLogHorizon).IsGreaterThan(TimeSpan.Zero);
    }
}
