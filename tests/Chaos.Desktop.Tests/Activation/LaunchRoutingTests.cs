using System.Collections.Concurrent;
using Chaos.Desktop.Windows.Activation;

namespace Chaos.Desktop.Tests.Activation;

public sealed class LaunchRoutingTests
{
    private const string Job = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    [Test]
    [Arguments("--capture", LaunchIntentKind.Capture)]
    [Arguments("--pad", LaunchIntentKind.Pad)]
    [Arguments("chaos:capture", LaunchIntentKind.Capture)]
    [Arguments("chaos://pad/", LaunchIntentKind.Pad)]
    [Arguments("chaos:open", LaunchIntentKind.Open)]
    [Arguments("--data-dir", LaunchIntentKind.Open)]
    public async Task FromArguments_KnownSwitchOrUri_GivesItsIntent(string argument, LaunchIntentKind expected)
    {
        var intent = LaunchIntents.FromArguments(["--data-dir", @"C:\somewhere", argument]);

        await Assert.That(intent.Kind).IsEqualTo(expected);
    }

    [Test]
    public async Task ReminderUri_RoundTrips_JobGenerationAndAction()
    {
        var uri = LaunchIntents.ReminderUri(Job, 42, ReminderActivation.SnoozeOneHour);

        var parsed = LaunchIntents.FromArguments([uri]);

        await Assert.That(uri).IsEqualTo($"chaos:reminder/{Job}/42/snooze-1h");
        await Assert.That(parsed.Kind).IsEqualTo(LaunchIntentKind.Reminder);
        await Assert.That(parsed.JobId).IsEqualTo(Job);
        await Assert.That(parsed.Generation).IsEqualTo(42L);
        await Assert.That(parsed.Action).IsEqualTo(ReminderActivation.SnoozeOneHour);
    }

    [Test]
    public async Task TaskUri_CarriesTheTaskId()
    {
        var parsed = LaunchIntents.FromArguments([LaunchIntents.TaskUri(Job)]);

        await Assert.That(parsed.Kind).IsEqualTo(LaunchIntentKind.OpenTask);
        await Assert.That(parsed.TaskId).IsEqualTo(Job);
    }

    [Test]
    [Arguments("chaos:task/../../windows/system32")]
    [Arguments("chaos:task/not-a-guid")]
    [Arguments("chaos:reminder/not-a-guid/1/done")]
    [Arguments("chaos:reminder/3f2504e0-4f89-11d3-9a0c-0305e82c3301/-1/done")]
    [Arguments("chaos:reminder/3f2504e0-4f89-11d3-9a0c-0305e82c3301/1/delete-everything")]
    [Arguments("chaos:run/cmd.exe")]
    [Arguments("https://example.com/chaos:capture")]
    [Arguments("")]
    public async Task TryParseUri_UnexpectedText_IsRejected(string text)
    {
        await Assert.That(LaunchIntents.TryParseUri(text, out _)).IsFalse();
        await Assert.That(LaunchIntents.FromArguments([text]).Kind).IsEqualTo(LaunchIntentKind.Open);
    }

    [Test]
    public async Task FromArguments_OverlongArgument_IsIgnored()
    {
        var intent = LaunchIntents.FromArguments(["chaos:capture" + new string('/', LaunchIntents.MaxArgumentLength)]);

        await Assert.That(intent.Kind).IsEqualTo(LaunchIntentKind.Open);
    }

    [Test]
    public async Task SendAsync_ToListeningPrimary_DeliversTheArgumentsAndIsAcknowledged()
    {
        var key = "test-" + Guid.NewGuid().ToString("N");
        var received = new ConcurrentQueue<IReadOnlyList<string>>();
        await using var router = LaunchRouter.Listen(key, args => { received.Enqueue(args); return true; });

        var accepted = await LaunchRouter.SendAsync(key, ["--data-dir", @"C:\data", "chaos:pad"], TimeSpan.FromSeconds(10));
        var second = await LaunchRouter.SendAsync(key, ["--capture"], TimeSpan.FromSeconds(10));

        await Assert.That(accepted).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(received.Count).IsEqualTo(2);
        await Assert.That(received.First()).IsEquivalentTo(new[] { "--data-dir", @"C:\data", "chaos:pad" });
    }

    [Test]
    public async Task SendAsync_PrimaryRefuses_ReportsNotAccepted()
    {
        var key = "test-" + Guid.NewGuid().ToString("N");
        await using var router = LaunchRouter.Listen(key, _ => false);

        await Assert.That(await LaunchRouter.SendAsync(key, ["--capture"], TimeSpan.FromSeconds(10))).IsFalse();
    }

    [Test]
    public async Task SendAsync_NoPrimary_ReportsNotAccepted()
    {
        var accepted = await LaunchRouter.SendAsync("test-" + Guid.NewGuid().ToString("N"), ["--capture"], TimeSpan.FromMilliseconds(300));

        await Assert.That(accepted).IsFalse();
    }

    [Test]
    public async Task Encode_DropsArgumentsBeyondTheLimits()
    {
        string[] args = [.. Enumerable.Range(0, 20).Select(index => $"a{index}"), new string('x', LaunchIntents.MaxArgumentLength + 1), "nul\0inside"];

        var decoded = LaunchRouter.Decode(LaunchRouter.Encode(args).AsSpan(4));

        await Assert.That(decoded!.Count).IsEqualTo(LaunchIntents.MaxArguments);
        await Assert.That(decoded[0]).IsEqualTo("a0");
    }

    [Test]
    public async Task Decode_TooManyArgumentsOrInvalidUtf8_IsRefused()
    {
        var tooMany = System.Text.Encoding.UTF8.GetBytes(string.Join('\0', Enumerable.Range(0, LaunchIntents.MaxArguments + 1).Select(index => $"a{index}")));

        await Assert.That(LaunchRouter.Decode(tooMany)).IsNull();
        await Assert.That(LaunchRouter.Decode(new byte[] { 0xC3, 0x28 })).IsNull();
    }
}
