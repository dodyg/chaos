using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Sqlite;

namespace Chaos.Infrastructure.Service;

/// <summary>
/// The device ID stored in this data root's <c>DeviceSettings</c> (made by StorageBootstrap), with the computer's name,
/// the platform and the app version for the device list.
/// </summary>
public sealed class SqliteDeviceIdentity(IApplicationStore store, string appVersion) : IDeviceIdentity
{
    /// <summary>The sync protocol schema this build speaks; P13 raises it when the protocol changes.</summary>
    public const int ClientSchema = 13;

    public async Task<DeviceIdentity> GetAsync(CancellationToken ct)
    {
        var id = await store.QueryAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';";
            return Task.FromResult(command.ExecuteScalar() as string);
        }, ct);
        if (!Guid.TryParse(id, out var deviceId))
        {
            throw new ChaosServiceException(ServiceFailure.Other, "This data root has no device identity.");
        }

        var name = Environment.MachineName;
        return new DeviceIdentity(deviceId, name.Length is > 0 and <= 100 ? name : "This computer", OperatingSystem.IsWindows() ? "windows" : "other", appVersion.Length <= 40 ? appVersion : appVersion[..40], ClientSchema);
    }
}
