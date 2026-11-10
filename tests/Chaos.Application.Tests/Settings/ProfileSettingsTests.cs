using Chaos.Application.Abstractions;
using Chaos.Application.Settings;

namespace Chaos.Application.Tests.Settings;

public sealed class ProfileSettingsTests
{
    [Test]
    [Arguments("Ctrl+Alt+N", "Ctrl+Alt+N")]
    [Arguments("alt+ctrl+n", "Ctrl+Alt+N")]
    [Arguments(" Control + Shift + p ", "Ctrl+Shift+P")]
    [Arguments("Alt+Shift+F12", "Alt+Shift+F12")]
    [Arguments("Ctrl+Alt+7", "Ctrl+Alt+7")]
    public async Task Accelerators_ValidText_IsNormalized(string text, string expected)
    {
        await Assert.That(Accelerators.TryNormalize(text, out var normalized, out _)).IsTrue();
        await Assert.That(normalized).IsEqualTo(expected);
    }

    [Test]
    [Arguments("N")]
    [Arguments("Shift+N")]
    [Arguments("Ctrl+Ctrl+N")]
    [Arguments("Ctrl+Win+N")]
    [Arguments("Ctrl+Alt+Enter")]
    [Arguments("Ctrl+Alt+F25")]
    [Arguments("Ctrl+Alt+F0")]
    [Arguments("Ctrl++N")]
    [Arguments("Ctrl+Alt+Shift+Meta+N")]
    public async Task Accelerators_InvalidText_IsRefusedWithAReason(string text)
    {
        await Assert.That(Accelerators.TryNormalize(text, out _, out var error)).IsFalse();
        await Assert.That(error).IsNotEmpty();
    }

    [Test]
    public async Task Normalize_BlankShortcuts_TurnThemOff()
    {
        var normalized = ProfileSettingsCommands.Normalize(Command(capture: " ", pad: null));

        await Assert.That(normalized.CaptureShortcut).IsNull();
        await Assert.That(normalized.PadShortcut).IsNull();
    }

    [Test]
    public async Task Normalize_SameShortcutTwice_IsRefused()
    {
        var error = Assert.Throws<AppException>(() => ProfileSettingsCommands.Normalize(Command(capture: "Ctrl+Alt+N", pad: "alt+ctrl+n")));

        await Assert.That(error!.Fields.Single().FieldPath).IsEqualTo("padShortcut");
    }

    [Test]
    [Arguments("", "displayName")]
    [Arguments("   ", "displayName")]
    public async Task Normalize_BlankName_IsRefused(string name, string field)
    {
        var error = Assert.Throws<AppException>(() => ProfileSettingsCommands.Normalize(Command() with { DisplayName = name }));

        await Assert.That(error!.Fields.Single().FieldPath).IsEqualTo(field);
    }

    [Test]
    public async Task Normalize_UnknownDateFormat_IsRefused()
    {
        var error = Assert.Throws<AppException>(() => ProfileSettingsCommands.Normalize(Command() with { DateFormat = "DDMMYY" }));

        await Assert.That(error!.Fields.Single().FieldPath).IsEqualTo("dateFormat");
    }

    [Test]
    public async Task NormalizeZone_WindowsIdBecomesIana_AndUnknownIsRefused()
    {
        await Assert.That(ProfileSettingsCommands.NormalizeZone("W. Europe Standard Time")).IsEqualTo("Europe/Berlin");
        await Assert.That(ProfileSettingsCommands.NormalizeZone("Asia/Jakarta")).IsEqualTo("Asia/Jakarta");
        await Assert.That(() => ProfileSettingsCommands.NormalizeZone("Mars/Olympus_Mons")).Throws<AppException>();
        await Assert.That(ProfileSettingsCommands.AvailableZones()).Contains("Asia/Jakarta");
    }

    private static UpdateProfileSettingsCommand Command(string? capture = "Ctrl+Alt+N", string? pad = "Ctrl+Alt+P") =>
        new(Guid.NewGuid().ToString(), 1, "Me", "UTC", "DMY", capture, pad, KeepRunningInTray: true);
}
