using Chaos.Application.Abstractions;
using Chaos.Application.Calendar;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record TimeBlockStatusRequest();
/// <summary>Time blocking as Settings shows it. ProfileVersion is a string (RPC rule: versions and ids as strings).</summary>
public sealed record TimeBlockStatusDto(
    bool Enabled,
    string? AccountId,
    string? AccountEmail,
    bool ShowTitles,
    IReadOnlyList<TimeBlockSpaceDto> Spaces,
    string? CalendarId,
    bool IsRunner,
    string? LastPassUtc,
    string? LastError,
    bool NeedsSignIn,
    int EventCount,
    string ProfileVersion,
    IReadOnlyList<TimeBlockAccountDto> Accounts);
/// <summary>Chosen is false only when the person picked specific spaces and left this one out.</summary>
public sealed record TimeBlockSpaceDto(string Id, string Name, bool Personal, bool Chosen);
public sealed record TimeBlockAccountDto(string Id, string Email, bool SignedInHere);
public sealed record TimeBlockStatusResponse(bool Ok, bool Configured, TimeBlockStatusDto? Status, TaskErrorResponse? Error);
public sealed record TimeBlockEnableRequest(string AccountId, bool ShowTitles, IReadOnlyList<string>? SpaceIds);
/// <summary>SpaceIds null or empty means every space. The first pass runs before the answer; LastError on the status says whether it failed.</summary>
public sealed record TimeBlockEnableResponse(bool Ok, TimeBlockStatusDto? Status, TaskErrorResponse? Error);
public sealed record TimeBlockUpdateRequest(string ExpectedVersion, bool ShowTitles, IReadOnlyList<string>? SpaceIds);
public sealed record TimeBlockUpdateResponse(bool Ok, TimeBlockStatusDto? Status, TaskErrorResponse? Error);
/// <summary>Makes this device the one that talks to Google ("Run on this device").</summary>
public sealed record TimeBlockRunHereRequest(string ExpectedVersion);
public sealed record TimeBlockRunHereResponse(bool Ok, TimeBlockStatusDto? Status, TaskErrorResponse? Error);
/// <summary>Turning off offers "Delete the Chaos calendar in Google"; DeleteCalendar is the answer, yes by default.</summary>
public sealed record TimeBlockDisableRequest(string ExpectedVersion, bool DeleteCalendar);
public sealed record TimeBlockDisableResponse(bool Ok, TimeBlockStatusDto? Status, bool CalendarDeleted, TaskErrorResponse? Error);
public sealed record TimeBlockSyncRequest();
public sealed record TimeBlockPassDto(bool Ran, string? Error, bool NeedsSignIn, int Pushed, int Updated, int Removed, int Moved, int Conflicts);
public sealed record TimeBlockSyncResponse(bool Ok, TimeBlockPassDto? Pass, TimeBlockStatusDto? Status, TaskErrorResponse? Error);
/// <summary>"Sign in again" for the enabled account: the browser opens with the time blocking permission only.</summary>
public sealed record TimeBlockSignInRequest();
public sealed record TimeBlockSignInResponse(bool Ok, TimeBlockStatusDto? Status, TaskErrorResponse? Error);

/// <summary>Google Calendar time blocking (PLAN3 §11): status, turn on, settings, the runner device, turn off and Sync now. Errors are returned as data.</summary>
[NeoRpcService("timeBlock", Version = 1)]
public sealed class TimeBlockRpcService(TimeBlockCommands commands, Chaos.Desktop.Platform.TimeBlockScheduler scheduler)
{
    [NeoRpcMethod("status")]
    public ValueTask<TimeBlockStatusResponse> StatusAsync(TimeBlockStatusRequest request, CancellationToken ct) =>
        Rpc.Run<TimeBlockStatusResponse>("timeBlock.status", async () => new(true, commands.IsConfigured, Map(await commands.StatusAsync(ct)), null),
            error => new(false, commands.IsConfigured, null, error));

    [NeoRpcMethod("enable")]
    public ValueTask<TimeBlockEnableResponse> EnableAsync(TimeBlockEnableRequest request, CancellationToken ct) =>
        Rpc.Run<TimeBlockEnableResponse>("timeBlock.enable", async () =>
        {
            await commands.EnableAsync(new EnableTimeBlockCommand(request.AccountId ?? string.Empty, request.ShowTitles, request.SpaceIds), ct, runPass: false);
            return new(true, Map(await PassThenStatusAsync(ct)), null);
        },
            error => new(false, null, error));

    [NeoRpcMethod("update")]
    public ValueTask<TimeBlockUpdateResponse> UpdateAsync(TimeBlockUpdateRequest request, CancellationToken ct) =>
        Rpc.Run<TimeBlockUpdateResponse>("timeBlock.update", async () =>
            new(true, Map(await commands.UpdateAsync(new UpdateTimeBlockCommand(RpcParse.Version(request.ExpectedVersion, "expectedVersion"), request.ShowTitles, request.SpaceIds), ct)), null),
            error => new(false, null, error));

    [NeoRpcMethod("runOnThisDevice")]
    public ValueTask<TimeBlockRunHereResponse> RunOnThisDeviceAsync(TimeBlockRunHereRequest request, CancellationToken ct) =>
        Rpc.Run<TimeBlockRunHereResponse>("timeBlock.runOnThisDevice", async () =>
            new(true, Map(await commands.RunOnThisDeviceAsync(RpcParse.Version(request.ExpectedVersion, "expectedVersion"), ct)), null),
            error => new(false, null, error));

    [NeoRpcMethod("disable")]
    public ValueTask<TimeBlockDisableResponse> DisableAsync(TimeBlockDisableRequest request, CancellationToken ct) =>
        Rpc.Run<TimeBlockDisableResponse>("timeBlock.disable", async () =>
        {
            var result = await commands.DisableAsync(RpcParse.Version(request.ExpectedVersion, "expectedVersion"), request.DeleteCalendar, ct);
            return new(true, Map(result.Status), result.CalendarDeleted, null);
        },
            error => new(false, null, false, error));

    /// <summary>A pass now, whatever the timer says; on a device that is not the runner it reports Ran = false and makes no request.</summary>
    [NeoRpcMethod("syncNow")]
    public ValueTask<TimeBlockSyncResponse> SyncNowAsync(TimeBlockSyncRequest request, CancellationToken ct) =>
        Rpc.Run<TimeBlockSyncResponse>("timeBlock.syncNow", async () =>
        {
            var pass = await scheduler.RunNowAsync(ct) ?? new TimeBlockPassResult(false, null, false, 0, 0, 0, 0, 0);
            return new(true, Map(pass), Map(await commands.StatusAsync(ct)), null);
        },
            error => new(false, null, null, error));

    [NeoRpcMethod("signIn")]
    public ValueTask<TimeBlockSignInResponse> SignInAsync(TimeBlockSignInRequest request, CancellationToken ct) =>
        Rpc.Run<TimeBlockSignInResponse>("timeBlock.signIn", async () =>
        {
            await commands.SignInAgainAsync(ct, runPass: false);
            return new(true, Map(await PassThenStatusAsync(ct)), null);
        },
            error => new(false, null, error));

    /// <summary>The first pass after turning on or signing in, through the scheduler's gate so it never overlaps a scheduled pass or a restore.</summary>
    private async Task<TimeBlockStatus> PassThenStatusAsync(CancellationToken ct)
    {
        await scheduler.RunNowAsync(ct);
        return await commands.StatusAsync(ct);
    }

    private static TimeBlockStatusDto Map(TimeBlockStatus status) => new(
        status.Enabled, status.AccountId, status.AccountEmail, status.ShowTitles,
        [.. status.Spaces.Select(space => new TimeBlockSpaceDto(space.Id, space.Name, space.Personal, space.Chosen))],
        status.CalendarId, status.IsRunner, status.LastPassUtc is { } pass ? StorageFormats.Instant(pass) : null, status.LastError, status.NeedsSignIn,
        status.EventCount, status.ProfileVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        [.. status.Accounts.Select(account => new TimeBlockAccountDto(account.Id, account.Email, account.SignedInHere))]);

    private static TimeBlockPassDto Map(TimeBlockPassResult pass) => new(pass.Ran, pass.Error, pass.NeedsSignIn, pass.Pushed, pass.Updated, pass.Removed, pass.Moved, pass.Conflicts);
}
