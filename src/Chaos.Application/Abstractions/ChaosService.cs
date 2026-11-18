namespace Chaos.Application.Abstractions;

/// <summary>Why a call to the online service failed, in the words Settings uses (PLAN2 P12 item 5).</summary>
public enum ServiceFailure
{
    /// <summary>This build has no service address, so accounts are not offered.</summary>
    NotConfigured = 0,

    /// <summary>The sign-in ended or was revoked: sign in again.</summary>
    SignInAgain = 1,

    /// <summary>The service could not be reached.</summary>
    Offline = 2,

    /// <summary>The service refused the request (not allowed, not found, too many requests).</summary>
    Refused = 3,

    /// <summary>Cancelled, timed out, or anything else; the message says what.</summary>
    Other = 4,
}

/// <summary>A service failure the person can read. The message never contains a token or a code.</summary>
public sealed class ChaosServiceException(ServiceFailure kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public ServiceFailure Kind { get; } = kind;
}

/// <summary>This device as the service should know it (PLAN2 §8.4 step 6).</summary>
public sealed record DeviceIdentity(Guid Id, string Name, string Platform, string ClientVersion, int ClientSchema);

/// <summary>The device ID Chaos made for this data root, with a name, platform and version for the device list.</summary>
public interface IDeviceIdentity
{
    Task<DeviceIdentity> GetAsync(CancellationToken ct);
}

/// <summary>A device registered to the account, as the service lists it.</summary>
public sealed record ServiceDevice(Guid Id, string Name, string Platform, string ClientVersion, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool Current);

/// <summary>The signed-in account: email, devices and storage used.</summary>
public sealed record ServiceAccount(string Email, string? DisplayName, long StorageBytes, long StorageQuotaBytes, IReadOnlyList<ServiceDevice> Devices);

/// <summary>What Settings can say without asking the network: whether a sign-in is kept, and for which email.</summary>
public sealed record ServiceSession(bool Configured, bool SignedIn, string? Email, string? ServiceHost);

/// <summary>
/// The online service from the desktop (PLAN2 §8.4, P12 item 5). Sign-in uses the system browser with a loopback
/// redirect and PKCE. The refresh token lives in <see cref="ISecretStore"/> and nowhere else; access tokens stay in memory.
/// </summary>
public interface IChaosServiceClient
{
    /// <summary>True when this build has a service address. Without one Settings explains instead of offering sign-in.</summary>
    bool IsConfigured { get; }

    /// <summary>Whether a sign-in is kept on this device. Local only; never touches the network.</summary>
    Task<ServiceSession> GetSessionAsync(CancellationToken ct);

    /// <summary>Opens the browser, waits for the sign-in, registers this device and returns the account.</summary>
    /// <exception cref="ChaosServiceException">Cancelled, timed out, refused, offline or not configured.</exception>
    Task<ServiceAccount> SignInAsync(CancellationToken ct);

    /// <summary>Forgets the sign-in on this device. The device stays registered until it is removed from the account.</summary>
    Task SignOutAsync(CancellationToken ct);

    Task<ServiceAccount> GetAccountAsync(CancellationToken ct);

    /// <summary>Removes a device from the account. Removing this device also signs it out here.</summary>
    Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct);

    /// <summary>A valid access token, refreshed when it is about to expire. One refresh at a time, however many callers.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken ct);

    /// <summary>
    /// One call to a <c>/v1</c> endpoint with the sync protocol headers (PLAN2 §7.12). Any answer, including a 4xx, comes back
    /// as a <see cref="ServiceResponse"/>; only a lost sign-in, an unreachable service or a cancellation throw.
    /// </summary>
    /// <exception cref="ChaosServiceException">Not signed in, the service no longer accepts the sign-in, or it could not be reached.</exception>
    Task<ServiceResponse> CallAsync(HttpMethod method, string path, string? json, CancellationToken ct);
}

/// <summary>An answer from the service: the status code and the body text. Never contains a token.</summary>
public sealed record ServiceResponse(int Status, string Body)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>Where the service address came from. A saved address wins over the environment, which wins over the file beside the exe.</summary>
public enum ServiceAddressSource
{
    None = 0,
    Setting = 1,
    Environment = 2,
    File = 3,
}

/// <summary>The address in use, if any, and where it came from. <c>Saved</c> is what the person typed, even while another source is overridden.</summary>
public sealed record ServiceAddressInfo(string? Address, ServiceAddressSource Source, string? Saved);

/// <summary>Changing the address could not be done; the message is for the person.</summary>
public sealed record ServiceAddressResult(bool Ok, string? Error, ServiceAddressInfo Info);

/// <summary>The service address the person can set in Settings → Account, so no file or environment variable is needed.</summary>
public interface IServiceAddressSettings
{
    ServiceAddressInfo Get();

    /// <summary>Saves the address; an empty one clears it. Rejects anything that is not a usable address.</summary>
    ServiceAddressResult Set(string? address);
}
