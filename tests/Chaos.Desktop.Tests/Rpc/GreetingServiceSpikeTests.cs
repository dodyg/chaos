namespace Chaos.Desktop.Tests.Rpc;

/// <summary>
/// Work package 0 spike: assert a real C# RPC service result on the actual host
/// service type (PLAN §15 WP0 step 2). Full windowed acceptance runs on a real
/// interactive Windows desktop; this asserts the service contract headlessly.
/// </summary>
public sealed class GreetingServiceSpikeTests
{
    [Test]
    public async Task HelloAsync_WithName_ReturnsFormattedGreeting()
    {
        var service = new GreetingService();

        var response = await service.HelloAsync(new GreetingRequest("desktop"), CancellationToken.None);

        await Assert.That(response.Message).IsEqualTo("Hello, desktop!");
    }

    [Test]
    public async Task HelloAsync_EmptyName_StillReturnsGreeting()
    {
        var service = new GreetingService();

        var response = await service.HelloAsync(new GreetingRequest(string.Empty), CancellationToken.None);

        await Assert.That(response.Message).IsEqualTo("Hello, !");
    }
}
