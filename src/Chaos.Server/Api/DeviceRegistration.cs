using Chaos.Server.Auth;
using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Api;

public enum DeviceRegistrationOutcome { Registered, Updated, WrongDevice, Invalid, OtherAccount, Removed, Busy }

public sealed record DeviceRegistrationResult(DeviceRegistrationOutcome Outcome, DeviceDto? Device);

/// <summary>Registers a device on the account, or refreshes what the account knows about it.</summary>
public sealed class DeviceRegistration(ChaosDbContext db, TimeProvider clock)
{
    private const int MaxNameLength = 100;
    private const int MaxShortLength = 40;

    public async Task<DeviceRegistrationResult> RegisterAsync(Guid accountId, Guid tokenDeviceId, RegisterDeviceRequest request, CancellationToken ct)
    {
        // A token is for one device; it cannot register another.
        if (request.DeviceId != tokenDeviceId)
        {
            return new(DeviceRegistrationOutcome.WrongDevice, null);
        }

        var name = request.Name?.Trim() ?? string.Empty;
        var platform = request.Platform?.Trim() ?? string.Empty;
        var version = request.ClientVersion?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength || platform.Length is 0 or > MaxShortLength || version.Length is 0 or > MaxShortLength || request.ClientSchema < 0)
        {
            return new(DeviceRegistrationOutcome.Invalid, null);
        }

        var now = clock.GetUtcNow();
        var device = await db.Devices.FirstOrDefaultAsync(d => d.Id == request.DeviceId, ct);
        var created = device is null;
        if (device is null)
        {
            device = new Device { Id = request.DeviceId, AccountId = accountId, Name = name, Platform = platform, ClientVersion = version, ClientSchema = request.ClientSchema, CreatedAt = now, LastSeenAt = now };
            db.Devices.Add(device);
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, Kind = AuditKinds.DeviceAdded, At = now, DeviceId = device.Id });
        }
        else if (device.AccountId != accountId)
        {
            return new(DeviceRegistrationOutcome.OtherAccount, null);
        }
        else if (device.RevokedAt is not null)
        {
            return new(DeviceRegistrationOutcome.Removed, null);
        }
        else
        {
            device.Name = name;
            device.Platform = platform;
            device.ClientVersion = version;
            device.ClientSchema = request.ClientSchema;
            device.LastSeenAt = now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (created)
        {
            // Two first calls from one device raced; the other one registered it.
            return new(DeviceRegistrationOutcome.Busy, null);
        }

        var dto = new DeviceDto(device.Id, device.Name, device.Platform, device.ClientVersion, device.CreatedAt, device.LastSeenAt, true);
        return new(created ? DeviceRegistrationOutcome.Registered : DeviceRegistrationOutcome.Updated, dto);
    }
}

public static class AccountServices
{
    public static IServiceCollection AddAccountServices(this IServiceCollection services) =>
        services.AddScoped<DeviceRegistration>().AddScoped<AccountDeletion>();
}
