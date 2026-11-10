using System.Xml;
using Chaos.Application.Abstractions;
using Chaos.Application.Settings;
using Chaos.Desktop.Platform;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Desktop.Windows.Notifications;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

public sealed class ShellRpcTests
{
    private sealed class RecordingShell : IDesktopShell
    {
        public List<ProfileSettings> Applied { get; } = [];
        public int QuitRequests { get; private set; }
        public ShellStatus Status { get; } = new(TrayAvailable: true, KeepRunningInTray: true, Packaged: false,
            [new ShortcutStatus("capture", "Ctrl+Alt+N", "Conflict", "Ctrl+Alt+N is already used by another app.")]);
        public Task ApplyAsync(ProfileSettings settings, CancellationToken ct) { Applied.Add(settings); return Task.CompletedTask; }
        public void RequestQuit() => QuitRequests++;
    }

    [Test]
    public async Task Settings_DefaultsUpdateAndRestart_PersistAndReachTheShell()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
        var shell = new RecordingShell();
        var zoneChanges = 0;
        using (var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None))
        {
            var rpc = new ShellRpcService(new ShellActivations((_, _) => throw new InvalidOperationException()), shell,
                new ProfileSettingsCommands(storage.Store, new SequentialIdGenerator(), clock), "1.2.3", _ => { zoneChanges++; return Task.CompletedTask; });

            var defaults = await rpc.SettingsAsync(new(), CancellationToken.None);
            await Assert.That(defaults.CaptureShortcut).IsEqualTo("Ctrl+Alt+N");
            await Assert.That(defaults.PadShortcut).IsEqualTo("Ctrl+Alt+P");
            await Assert.That(defaults.KeepRunningInTray).IsTrue();

            var zone = defaults.ZoneId == "Asia/Jakarta" ? "Europe/Rome" : "Asia/Jakarta";
            var updated = await rpc.UpdateSettingsAsync(new(Guid.NewGuid().ToString(), defaults.Version!, "  Dody  ", zone, "mdy", "alt + ctrl + k", null, false), CancellationToken.None);
            await Assert.That(updated.Ok).IsTrue();
            await Assert.That(updated.DisplayName).IsEqualTo("Dody");
            await Assert.That(updated.DateFormat).IsEqualTo("MDY");
            await Assert.That(updated.CaptureShortcut).IsEqualTo("Ctrl+Alt+K");
            await Assert.That(updated.PadShortcut).IsNull();
            await Assert.That(updated.ZoneChanged).IsTrue();
            await Assert.That(zoneChanges).IsEqualTo(1);
            await Assert.That(shell.Applied.Single().CaptureShortcut).IsEqualTo("Ctrl+Alt+K");

            var stale = await rpc.UpdateSettingsAsync(new(Guid.NewGuid().ToString(), defaults.Version!, "Dody", zone, "DMY", null, null, true), CancellationToken.None);
            await Assert.That(stale.Ok).IsFalse();
            await Assert.That(stale.Error!.Kind).IsEqualTo("versionConflict");

            var badShortcut = await rpc.UpdateSettingsAsync(new(Guid.NewGuid().ToString(), updated.Version!, "Dody", zone, "DMY", "Shift+K", null, true), CancellationToken.None);
            await Assert.That(badShortcut.Error!.Fields.Single().FieldPath).IsEqualTo("captureShortcut");
            var badZone = await rpc.UpdateSettingsAsync(new(Guid.NewGuid().ToString(), updated.Version!, "Dody", "Mars/Olympus_Mons", "DMY", null, null, true), CancellationToken.None);
            await Assert.That(badZone.Error!.Fields.Single().FieldPath).IsEqualTo("zoneId");

            var status = await rpc.StatusAsync(new(), CancellationToken.None);
            await Assert.That(status.AppVersion).IsEqualTo("1.2.3");
            await Assert.That(status.Shortcuts.Single().State).IsEqualTo("Conflict");
            await rpc.QuitAsync(new(), CancellationToken.None);
            await Assert.That(shell.QuitRequests).IsEqualTo(1);
            await Assert.That((await rpc.ZonesAsync(new(), CancellationToken.None)).Zones).Contains("UTC");
        }

        using var reopened = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new SequentialIdGenerator(), clock, CancellationToken.None);
        var again = await new ProfileSettingsCommands(reopened.Store, new SequentialIdGenerator(), clock).GetAsync();
        await Assert.That(again.CaptureShortcut).IsEqualTo("Ctrl+Alt+K");
        await Assert.That(again.PadShortcut).IsNull();
        await Assert.That(again.KeepRunningInTray).IsFalse();
        await Assert.That(again.DateFormat).IsEqualTo("MDY");
    }

    [Test]
    public async Task ToastContent_EscapesTextAndRoutesEveryButtonThroughTheChaosScheme()
    {
        var request = new NotificationRequest("3f2504e0-4f89-11d3-9a0c-0305e82c3301", 9, "00000001-0000-0000-0000-000000000000",
            "Pay <rent> & \"bills\"", "Due 18:00", DateTimeOffset.UnixEpoch, IsFollowUp: false);

        var document = new XmlDocument();
        document.LoadXml(WindowsToastAdapter.ContentXml(request));
        var actions = document.SelectNodes("/toast/actions/action")!.Cast<XmlElement>().ToList();

        await Assert.That(document.SelectSingleNode("/toast/visual/binding/text")!.InnerText).IsEqualTo("Pay <rent> & \"bills\"");
        await Assert.That(actions.Count).IsEqualTo(4);
        await Assert.That(actions.All(action => action.GetAttribute("activationType") == "protocol" && action.GetAttribute("arguments").StartsWith("chaos:reminder/3f2504e0-4f89-11d3-9a0c-0305e82c3301/9/", StringComparison.Ordinal))).IsTrue();
        await Assert.That(WindowsToastAdapter.OsId(request).Length).IsEqualTo(16);
        await Assert.That(WindowsToastAdapter.OsId(request)).IsNotEqualTo(WindowsToastAdapter.OsId(request with { Generation = 10 }));
    }
}
