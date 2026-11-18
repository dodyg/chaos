using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
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

/// <summary>Settings → Account (PLAN2 P12 item 5). Failures are returned as data.</summary>
[NeoRpcService("account", Version = 1)]
public sealed class AccountRpcService(IChaosServiceClient service)
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
