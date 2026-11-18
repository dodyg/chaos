using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Infrastructure.Service;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

/// <summary>A device on the account, as Settings lists it. <c>Current</c> is this computer.</summary>
public sealed record AccountDeviceDto(string Id, string Name, string Platform, string ClientVersion, string LastSeenUtc, bool Current);

/// <summary>The signed-in account. Never contains a token.</summary>
public sealed record AccountInfoDto(string Email, string? DisplayName, double StorageBytes, double StorageQuotaBytes, IReadOnlyList<AccountDeviceDto> Devices);

/// <summary>What failed, in plain words. <c>Kind</c> is a <see cref="ServiceFailure"/> name: NotConfigured, SignInAgain, Offline, Refused or Other.</summary>
public sealed record AccountErrorDto(string Kind, string Message);

public sealed record AccountStatusRequest();

/// <summary>What is known without the network: whether accounts are offered here, and whether a sign-in is kept.</summary>
public sealed record AccountStatusResponse(bool Ok, bool Configured, bool SignedIn, string? Email, string? ServiceHost, AccountErrorDto? Error);

public sealed record AccountRequest();
public sealed record AccountResponse(bool Ok, AccountInfoDto? Account, AccountErrorDto? Error);
public sealed record AccountDeviceRequest(string DeviceId);

/// <summary>Counts of live tasks, lists, tags and pages: this computer's, or the account's.</summary>
public sealed record ContentCountsDto(double Tasks, double Lists, double Tags, double Pages);

public sealed record JoinPreviewRequest();

/// <summary><c>Case</c>: AlreadyJoined, FirstDevice or ChooseData. <c>DefaultMode</c>: Merge or Replace (Merge whenever this computer has data).</summary>
public sealed record JoinPreviewResponse(bool Ok, string Case, ContentCountsDto Local, ContentCountsDto? Account, string DefaultMode, AccountErrorDto? Error);

/// <summary><c>Mode</c> is Merge or Replace; it matters only when the account already has data.</summary>
public sealed record JoinRequest(string Mode);

public sealed record JoinResponse(bool Ok, string Case, string? Mode, string? SafetyBackup, AccountErrorDto? Error);

public sealed record LeaveRequest(bool KeepData);

public sealed record DeleteAccountRequest(string Confirmation);

public sealed record DeviceIdentityResponse(bool Ok, string? Error);

public sealed record ServiceAddressRequest();
public sealed record SetServiceAddressRequest(string Address);

/// <summary>The address in use (<c>Source</c>: None, Setting, Environment or File) and what the person saved. <c>Error</c> is set when a change was refused.</summary>
public sealed record ServiceAddressResponse(bool Ok, string? Address, string Source, string? Saved, string? Error);

/// <summary>Settings → Account (PLAN2 P12 item 5). Failures are returned as data.</summary>
[NeoRpcService("account", Version = 1)]
public sealed class AccountRpcService(IChaosServiceClient service, IServiceAddressSettings address, AccountJoinService join)
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _signIn;

    [NeoRpcMethod("status")]
    public async ValueTask<AccountStatusResponse> StatusAsync(AccountStatusRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.status");
        try
        {
            var session = await service.GetSessionAsync(ct);
            return new(true, session.Configured, session.SignedIn, session.Email, session.ServiceHost, null);
        }
        catch (ChaosServiceException error)
        {
            return new(false, service.IsConfigured, false, null, null, Map(error));
        }
    }

    /// <summary>The account as the service has it now: email, devices, storage. Needs the network.</summary>
    [NeoRpcMethod("account")]
    public async ValueTask<AccountResponse> AccountAsync(AccountRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.account");
        return await RunAccountAsync(() => service.GetAccountAsync(ct));
    }

    /// <summary>Opens the browser and waits for the sign-in to finish (up to ten minutes, or until cancelled).</summary>
    [NeoRpcMethod("signIn")]
    public async ValueTask<AccountResponse> SignInAsync(AccountRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.signIn");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _signIn;
            _signIn = cts;
        }

        previous?.Cancel();
        try
        {
            return await RunAccountAsync(() => service.SignInAsync(cts.Token));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_signIn, cts))
                {
                    _signIn = null;
                }
            }
        }
    }

    /// <summary>Looks at the account and this computer without changing anything, so the person can choose how to join.</summary>
    [NeoRpcMethod("joinPreview")]
    public async ValueTask<JoinPreviewResponse> JoinPreviewAsync(JoinPreviewRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.joinPreview");
        try
        {
            var preview = await join.PrepareAsync(ct);
            return new(true, preview.Case.ToString(), Map(preview.Local), preview.Account is { } account ? Map(account) : null, preview.DefaultMode.ToString(), null);
        }
        catch (ChaosServiceException error)
        {
            return new(false, nameof(JoinCase.AlreadyJoined), new ContentCountsDto(0, 0, 0, 0), null, nameof(JoinMode.Merge), Map(error));
        }
    }

    /// <summary>Joins the account: the first device uploads what it has; otherwise a safety backup, then merge or replace.</summary>
    [NeoRpcMethod("join")]
    public async ValueTask<JoinResponse> JoinAsync(JoinRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.join");
        if (!Enum.TryParse<JoinMode>(request.Mode, ignoreCase: true, out var mode))
        {
            return new(false, nameof(JoinCase.AlreadyJoined), null, null, new AccountErrorDto(nameof(ServiceFailure.Refused), "Choose to merge or to replace."));
        }

        try
        {
            var outcome = await join.JoinAsync(mode, ct);
            return new(true, outcome.Case.ToString(), outcome.Mode?.ToString(), outcome.SafetyBackup, null);
        }
        catch (ChaosServiceException error)
        {
            return new(false, nameof(JoinCase.AlreadyJoined), null, null, Map(error));
        }
        catch (AppException error)
        {
            return new(false, nameof(JoinCase.AlreadyJoined), null, null, new AccountErrorDto(nameof(ServiceFailure.Other), error.Message));
        }
    }

    /// <summary>Signs out. <c>KeepData</c> leaves this computer's data and goes offline-only; otherwise the data is removed as "Delete all local data" does.</summary>
    [NeoRpcMethod("leave")]
    public async ValueTask<AccountStatusResponse> LeaveAsync(LeaveRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.leave");
        try
        {
            await join.SignOutAsync(request.KeepData, ct);
        }
        catch (ChaosServiceException error)
        {
            return new(false, service.IsConfigured, true, null, null, Map(error));
        }
        catch (AppException error)
        {
            return new(false, service.IsConfigured, true, null, null, new AccountErrorDto(nameof(ServiceFailure.Other), error.Message));
        }

        return await StatusAsync(new AccountStatusRequest(), ct);
    }

    /// <summary>Deletes the account on the service. Needs the typed phrase and a sign-in from the last ten minutes (<c>FreshSignInRequired</c> otherwise). This computer keeps its data.</summary>
    [NeoRpcMethod("deleteAccount")]
    public async ValueTask<AccountStatusResponse> DeleteAccountAsync(DeleteAccountRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.deleteAccount");
        try
        {
            await join.DeleteAccountAsync(request.Confirmation, ct);
        }
        catch (ChaosServiceException error)
        {
            return new(false, service.IsConfigured, true, null, null, Map(error));
        }

        return await StatusAsync(new AccountStatusRequest(), ct);
    }

    /// <summary>Signs in again with a fresh proof of identity (the service asks for one before deleting an account).</summary>
    [NeoRpcMethod("reauthenticate")]
    public async ValueTask<AccountResponse> ReauthenticateAsync(AccountRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.reauthenticate");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            previous = _signIn;
            _signIn = cts;
        }

        previous?.Cancel();
        try
        {
            return await RunAccountAsync(() => service.ReauthenticateAsync(cts.Token));
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_signIn, cts))
                {
                    _signIn = null;
                }
            }
        }
    }

    /// <summary>Gives a copied data folder its own identity (Settings → Data and backups). Refused while signed in.</summary>
    [NeoRpcMethod("resetDeviceIdentity")]
    public async ValueTask<DeviceIdentityResponse> ResetDeviceIdentityAsync(AccountRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.resetDeviceIdentity");
        try
        {
            await join.ResetDeviceIdentityAsync(ct);
            return new(true, null);
        }
        catch (AppException error)
        {
            return new(false, error.Message);
        }
    }

    private static ContentCountsDto Map(Chaos.Infrastructure.Sync.LocalContentCounts counts) => new(counts.Tasks, counts.Lists, counts.Tags, counts.Pages);

    private static ContentCountsDto Map(AccountContentCounts counts) => new(counts.Tasks, counts.Lists, counts.Tags, counts.Pages);

    [NeoRpcMethod("address")]
    public ValueTask<ServiceAddressResponse> AddressAsync(ServiceAddressRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.address");
        return ValueTask.FromResult(Map(address.Get(), null));
    }

    /// <summary>Saves the service address (empty clears it). Refused while signed in: the sign-in belongs to the old service.</summary>
    [NeoRpcMethod("setAddress")]
    public async ValueTask<ServiceAddressResponse> SetAddressAsync(SetServiceAddressRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.setAddress");
        if ((await service.GetSessionAsync(ct)).SignedIn)
        {
            return Map(address.Get(), "Sign out before changing the service address.");
        }

        var result = address.Set(request.Address);
        return Map(result.Info, result.Error);
    }

    private static ServiceAddressResponse Map(ServiceAddressInfo info, string? error) => new(error is null, info.Address, info.Source.ToString(), info.Saved, error);

    [NeoRpcMethod("cancelSignIn")]
    public ValueTask<AccountStatusResponse> CancelSignInAsync(AccountStatusRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.cancelSignIn");
        lock (_gate)
        {
            _signIn?.Cancel();
        }

        return StatusAsync(request, ct);
    }

    [NeoRpcMethod("signOut")]
    public async ValueTask<AccountStatusResponse> SignOutAsync(AccountStatusRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.signOut");
        try
        {
            await service.SignOutAsync(ct);
        }
        catch (ChaosServiceException error)
        {
            return new(false, service.IsConfigured, true, null, null, Map(error));
        }

        return await StatusAsync(request, ct);
    }

    /// <summary>Removes a device from the account and returns the new list. Removing this computer signs it out (Account is null).</summary>
    [NeoRpcMethod("revokeDevice")]
    public async ValueTask<AccountResponse> RevokeDeviceAsync(AccountDeviceRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("account.revokeDevice");
        if (!Guid.TryParse(request.DeviceId, out var id))
        {
            return new(false, null, new AccountErrorDto(nameof(ServiceFailure.Refused), "That device is not on the account."));
        }

        return await RunAsync(async () =>
        {
            await service.RevokeDeviceAsync(id, ct);
            return (await service.GetSessionAsync(ct)).SignedIn ? await service.GetAccountAsync(ct) : null;
        });
    }

    private static async ValueTask<AccountResponse> RunAccountAsync(Func<Task<ServiceAccount>> action) => await RunAsync(async () => (ServiceAccount?)await action());

    private static async ValueTask<AccountResponse> RunAsync(Func<Task<ServiceAccount?>> action)
    {
        try
        {
            var account = await action();
            return new(true, account is null ? null : Map(account), null);
        }
        catch (ChaosServiceException error)
        {
            return new(false, null, Map(error));
        }
        catch (OperationCanceledException)
        {
            return new(false, null, new AccountErrorDto(nameof(ServiceFailure.Other), "Sign-in was cancelled."));
        }
    }

    private static AccountErrorDto Map(ChaosServiceException error) => new(error.Kind.ToString(), error.Message);

    private static AccountInfoDto Map(ServiceAccount account) => new(account.Email, account.DisplayName, account.StorageBytes, account.StorageQuotaBytes,
        [.. account.Devices.Select(d => new AccountDeviceDto(d.Id.ToString("D"), d.Name, d.Platform, d.ClientVersion, d.LastSeenAt.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture), d.Current))]);
}
