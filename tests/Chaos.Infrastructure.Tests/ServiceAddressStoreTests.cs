using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Service;

namespace Chaos.Infrastructure.Tests;

/// <summary>The service address typed in Settings (PLAN2 P12): validated, kept in a file, and ahead of the environment and the exe-side file.</summary>
[NotInParallel]
public sealed class ServiceAddressStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chaos-address-" + Guid.NewGuid().ToString("N"));
    private readonly string? _env = Environment.GetEnvironmentVariable(ChaosServiceOptions.EnvironmentVariable);

    public ServiceAddressStoreTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(ChaosServiceOptions.EnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ChaosServiceOptions.EnvironmentVariable, _env);
        Directory.Delete(_dir, recursive: true);
    }

    private ServiceAddressStore Store(bool allowLoopbackHttp = false) =>
        new(Path.Combine(_dir, "service-address.json"), Path.Combine(_dir, "chaos-service.json"), allowLoopbackHttp);

    [Test]
    public async Task Nothing_is_configured_by_default()
    {
        var info = Store().Get();
        await Assert.That(info.Source).IsEqualTo(ServiceAddressSource.None);
        await Assert.That(Store().Resolve()).IsNull();
    }

    [Test]
    public async Task A_bare_host_gets_https_and_is_saved_and_survives_a_new_store()
    {
        var result = Store().Set("sync.example.com");
        await Assert.That(result.Ok).IsTrue();
        await Assert.That(result.Info.Address).IsEqualTo("https://sync.example.com/");
        await Assert.That(Store().Get().Source).IsEqualTo(ServiceAddressSource.Setting);
        await Assert.That(Store().Resolve()!.BaseAddress.Authority).IsEqualTo("sync.example.com");
    }

    [Test]
    public async Task Plain_http_is_refused_unless_loopback_in_a_development_build()
    {
        await Assert.That(Store().Set("http://sync.example.com").Ok).IsFalse();
        await Assert.That(Store().Set("localhost:5080").Ok).IsFalse();
        await Assert.That(Store(allowLoopbackHttp: true).Set("localhost:5080").Ok).IsTrue();
        await Assert.That(Store(allowLoopbackHttp: true).Get().Address).IsEqualTo("http://localhost:5080/");
        await Assert.That(Store(allowLoopbackHttp: true).Set("http://sync.example.com").Ok).IsFalse();
    }

    [Test]
    public async Task A_refused_address_keeps_the_previous_one()
    {
        await Assert.That(Store().Set("https://one.example.com").Ok).IsTrue();
        var result = Store().Set("not a url at all");
        await Assert.That(result.Ok).IsFalse();
        await Assert.That(result.Error).IsNotNull();
        await Assert.That(Store().Get().Address).IsEqualTo("https://one.example.com/");
    }

    [Test]
    public async Task The_saved_address_wins_over_the_environment_and_the_file_and_clearing_falls_back()
    {
        File.WriteAllText(Path.Combine(_dir, "chaos-service.json"), "{\"baseUrl\":\"https://file.example.com\"}");
        await Assert.That(Store().Get().Source).IsEqualTo(ServiceAddressSource.File);

        Environment.SetEnvironmentVariable(ChaosServiceOptions.EnvironmentVariable, "https://env.example.com");
        await Assert.That(Store().Get().Source).IsEqualTo(ServiceAddressSource.Environment);

        await Assert.That(Store().Set("https://mine.example.com").Ok).IsTrue();
        var info = Store().Get();
        await Assert.That(info.Source).IsEqualTo(ServiceAddressSource.Setting);
        await Assert.That(info.Saved).IsEqualTo("https://mine.example.com/");

        await Assert.That(Store().Set("  ").Ok).IsTrue();
        await Assert.That(Store().Get().Source).IsEqualTo(ServiceAddressSource.Environment);
    }

    [Test]
    public async Task A_corrupt_saved_file_is_ignored()
    {
        File.WriteAllText(Path.Combine(_dir, "service-address.json"), "{not json");
        await Assert.That(Store().Get().Source).IsEqualTo(ServiceAddressSource.None);
    }
}
