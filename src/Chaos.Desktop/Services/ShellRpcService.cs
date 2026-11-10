using Chaos.Application.Abstractions;
using Chaos.Application.Settings;
using Chaos.Desktop.Platform;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record ShellActivationDto(string Sequence, string Kind, string? TaskId, string? Message);
public sealed record ShellTakeRequest();
public sealed record ShellTakeResponse(bool Ok, IReadOnlyList<ShellActivationDto> Activations);

public sealed record ShortcutStatusDto(string Id, string? Accelerator, string State, string Message);
public sealed record ShellStatusRequest();
/// <param name="AppVersion">The running Chaos version.</param>
/// <param name="Packaged">Running from the installed package, which Windows notifications after exit need.</param>
public sealed record ShellStatusResponse(bool Ok, string AppVersion, bool Packaged, bool TrayAvailable, bool KeepRunningInTray, IReadOnlyList<ShortcutStatusDto> Shortcuts);

public sealed record ProfileSettingsRequest();
public sealed record ProfileSettingsResponse(
    bool Ok,
    string? DisplayName,
    string? ZoneId,
    string? DateFormat,
    string? CaptureShortcut,
    string? PadShortcut,
    bool KeepRunningInTray,
    string? Version,
    bool ZoneChanged,
    TaskErrorResponse? Error);
public sealed record ProfileSettingsUpdateRequest(
    string OperationId,
    string ExpectedVersion,
    string DisplayName,
    string ZoneId,
    string DateFormat,
    string? CaptureShortcut,
    string? PadShortcut,
    bool KeepRunningInTray);
public sealed record ZoneListRequest();
public sealed record ZoneListResponse(bool Ok, IReadOnlyList<string> Zones);
public sealed record QuitRequest();
public sealed record QuitResponse(bool Ok);

/// <summary>
/// The desktop shell for the frontend (PLAN §13 Bootstrap/settings): activations waiting for the
/// window, shortcut and tray status, the general and desktop settings, and Quit. No paths,
/// process control or OS handles cross this boundary.
/// </summary>
[NeoRpcService("shell", Version = 1)]
public sealed class ShellRpcService(ShellActivations activations, IDesktopShell shell, ProfileSettingsCommands settings, string appVersion, Func<CancellationToken, Task>? onZoneChanged = null)
{
    [NeoRpcMethod("take")]
    public ValueTask<ShellTakeResponse> TakeAsync(ShellTakeRequest request, CancellationToken ct) =>
        ValueTask.FromResult(new ShellTakeResponse(true, [.. activations.Take().Select(item => new ShellActivationDto(
            item.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), item.Kind.ToString(), item.TaskId, item.Message))]));

    [NeoRpcMethod("status")]
    public ValueTask<ShellStatusResponse> StatusAsync(ShellStatusRequest request, CancellationToken ct)
    {
        var status = shell.Status;
        return ValueTask.FromResult(new ShellStatusResponse(true, appVersion, status.Packaged, status.TrayAvailable, status.KeepRunningInTray,
            [.. status.Shortcuts.Select(item => new ShortcutStatusDto(item.Id, item.Accelerator, item.State, item.Message))]));
    }

    [NeoRpcMethod("settings")]
    public async ValueTask<ProfileSettingsResponse> SettingsAsync(ProfileSettingsRequest request, CancellationToken ct)
    {
        try { return Map(await settings.GetAsync(ct), zoneChanged: false); }
        catch (AppException error) { return Failure(error); }
    }

    [NeoRpcMethod("updateSettings")]
    public async ValueTask<ProfileSettingsResponse> UpdateSettingsAsync(ProfileSettingsUpdateRequest request, CancellationToken ct)
    {
        try
        {
            var result = await settings.UpdateAsync(new UpdateProfileSettingsCommand(
                request.OperationId,
                ReminderRpcService.Number(request.ExpectedVersion, "expectedVersion"),
                request.DisplayName,
                request.ZoneId,
                request.DateFormat,
                request.CaptureShortcut,
                request.PadShortcut,
                request.KeepRunningInTray), ct);
            await shell.ApplyAsync(result.Settings, ct);
            if (result.ZoneChanged && onZoneChanged is not null)
            {
                await onZoneChanged(ct);
            }

            return Map(result.Settings, result.ZoneChanged);
        }
        catch (AppException error) { return Failure(error); }
    }

    [NeoRpcMethod("zones")]
    public ValueTask<ZoneListResponse> ZonesAsync(ZoneListRequest request, CancellationToken ct) =>
        ValueTask.FromResult(new ZoneListResponse(true, ProfileSettingsCommands.AvailableZones()));

    [NeoRpcMethod("quit")]
    public ValueTask<QuitResponse> QuitAsync(QuitRequest request, CancellationToken ct)
    {
        shell.RequestQuit();
        return ValueTask.FromResult(new QuitResponse(true));
    }

    private static ProfileSettingsResponse Map(ProfileSettings value, bool zoneChanged) => new(
        true,
        value.DisplayName,
        value.ZoneId,
        value.DateFormat,
        value.CaptureShortcut,
        value.PadShortcut,
        value.KeepRunningInTray,
        value.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        zoneChanged,
        null);

    private static ProfileSettingsResponse Failure(AppException error) =>
        new(false, null, null, null, null, null, false, null, false, TaskRpcService.ToErrorResponse(error));
}
