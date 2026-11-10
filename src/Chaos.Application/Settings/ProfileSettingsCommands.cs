using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Reminders;
using Chaos.Application.Tasks;

namespace Chaos.Application.Settings;

/// <summary>The general and desktop settings of the local profile (PLAN §13 Settings).</summary>
/// <param name="DateFormat">How quick add reads numeric dates such as 03/04: DMY, MDY or YMD.</param>
/// <param name="CaptureShortcut">Global capture shortcut, null when off.</param>
/// <param name="PadShortcut">Global Personal-pad shortcut, null when off.</param>
public sealed record ProfileSettings(
    string DisplayName,
    string ZoneId,
    string DateFormat,
    string? CaptureShortcut,
    string? PadShortcut,
    bool KeepRunningInTray,
    long Version);

/// <summary>Replaces every general and desktop setting at once; null shortcuts turn them off.</summary>
public sealed record UpdateProfileSettingsCommand(
    string OperationId,
    long ExpectedVersion,
    string DisplayName,
    string ZoneId,
    string DateFormat,
    string? CaptureShortcut,
    string? PadShortcut,
    bool KeepRunningInTray);

/// <param name="ZoneChanged">The time zone changed: reminders were rescheduled and the host should refresh its day.</param>
public sealed record ProfileSettingsUpdate(ProfileSettings Settings, bool ZoneChanged, bool Replayed);

/// <summary>
/// Reads and changes the profile's name, time zone, date format and desktop shell settings
/// (PLAN §6 zones, §13 settings and shortcuts). A zone change reinterprets floating local
/// planning dates (they are stored as dates, so nothing is rewritten) and keeps explicitly zoned
/// appointments; every desired notification is recomputed in the same transaction, which gives
/// changed deliveries new generations so stale OS callbacks are ignored.
/// </summary>
public sealed class ProfileSettingsCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    public const int MaxDisplayNameLength = 100;
    public static readonly IReadOnlyList<string> DateFormats = ["DMY", "MDY", "YMD"];

    public async Task<ProfileSettings> GetAsync(CancellationToken ct = default) =>
        await store.QueryAsync(async (session, token) => Of(await ReminderCommands.RequireProfileAsync(session, token)), ct);

    public async Task<ProfileSettingsUpdate> UpdateAsync(UpdateProfileSettingsCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        var normalized = Normalize(command);
        var (zoneChanged, replayed) = await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var current = await ReminderCommands.RequireProfileAsync(session, token);
            var (changed, replay) = await TaskMutationSupport.ReplayOrRunAsync(session, current.Id, normalized.OperationId, "profile.settings", normalized, now, async inner =>
            {
                if (current.Version != normalized.ExpectedVersion)
                {
                    throw AppException.VersionConflict("Settings changed elsewhere. Refresh and try again.");
                }

                var updated = current with
                {
                    DisplayName = normalized.DisplayName,
                    ZoneId = normalized.ZoneId,
                    DateFormat = normalized.DateFormat,
                    CaptureShortcut = normalized.CaptureShortcut,
                    PadShortcut = normalized.PadShortcut,
                    KeepRunningInTray = normalized.KeepRunningInTray,
                    Version = current.Version + 1,
                    UpdatedAtUtc = now,
                };
                _ = await session.Profiles.UpdateGeneralSettingsAsync(updated, current.Version, inner)
                    ?? throw AppException.VersionConflict("Settings changed elsewhere. Refresh and try again.");
                var zoneChanged = !string.Equals(current.ZoneId, updated.ZoneId, StringComparison.Ordinal);
                if (zoneChanged)
                {
                    await ReminderSync.SyncAllAsync(session, updated, now, ids, inner);
                }

                return zoneChanged;
            }, token);
            return (changed, replay);
        }, ct);
        return new ProfileSettingsUpdate(await GetAsync(ct), zoneChanged, replayed);
    }

    /// <summary>Validates every field before anything is written; errors name the field.</summary>
    public static UpdateProfileSettingsCommand Normalize(UpdateProfileSettingsCommand command)
    {
        var name = (command.DisplayName ?? "").Trim();
        if (name.Length == 0)
        {
            throw AppException.Validation("displayName", "Enter a name.");
        }

        if (name.EnumerateRunes().Count() > MaxDisplayNameLength)
        {
            throw AppException.Validation("displayName", $"Use at most {MaxDisplayNameLength} characters for the name.");
        }

        var zone = NormalizeZone(command.ZoneId);
        var format = (command.DateFormat ?? "").Trim().ToUpperInvariant();
        if (!DateFormats.Contains(format, StringComparer.Ordinal))
        {
            throw AppException.Validation("dateFormat", "Choose day-month-year, month-day-year or year-month-day.");
        }

        var capture = Accelerators.NormalizeOrNull(command.CaptureShortcut, "captureShortcut");
        var pad = Accelerators.NormalizeOrNull(command.PadShortcut, "padShortcut");
        if (capture is not null && string.Equals(capture, pad, StringComparison.Ordinal))
        {
            throw AppException.Validation("padShortcut", "The pad shortcut must differ from the capture shortcut.");
        }

        return command with { DisplayName = name, ZoneId = zone, DateFormat = format, CaptureShortcut = capture, PadShortcut = pad };
    }

    /// <summary>A canonical IANA time zone ID known to this device (PLAN §5: validated canonical IDs).</summary>
    public static string NormalizeZone(string? zoneId)
    {
        var text = (zoneId ?? "").Trim();
        if (text.Length == 0 || text.Length > 64 || !TimeZoneInfo.TryFindSystemTimeZoneById(text, out var zone))
        {
            throw AppException.Validation("zoneId", "Choose a time zone from the list.");
        }

        if (zone.HasIanaId)
        {
            return zone.Id;
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && iana is not null
            ? iana
            : throw AppException.Validation("zoneId", "Choose a time zone from the list.");
    }

    /// <summary>The IANA zone IDs offered in Settings, sorted by ID.</summary>
    public static IReadOnlyList<string> AvailableZones() => Zones.Value;

    /// <summary>
    /// Windows lists its own zone IDs, each of which covers many IANA zones (one per country or
    /// region), so the list maps every Windows zone through every region this device knows.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<string>> Zones = new(() =>
    {
        var zones = new SortedSet<string>(StringComparer.Ordinal) { "UTC" };
        var regions = System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.SpecificCultures)
            .Select(culture => culture.Name.Split('-')[^1])
            .Where(region => region.Length == 2 && region.All(char.IsAsciiLetterUpper))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            if (zone.HasIanaId)
            {
                zones.Add(zone.Id);
                continue;
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var primary) && primary is not null)
            {
                zones.Add(primary);
            }

            foreach (var region in regions)
            {
                if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, region, out var iana) && iana is not null && !iana.StartsWith("Etc/", StringComparison.Ordinal))
                {
                    zones.Add(iana);
                }
            }
        }

        return [.. zones];
    });

    private static ProfileSettings Of(ProfileRecord profile) => new(
        profile.DisplayName,
        profile.ZoneId,
        profile.DateFormat,
        profile.CaptureShortcut,
        profile.PadShortcut,
        profile.KeepRunningInTray,
        profile.Version);
}

/// <summary>
/// Global shortcut text (PLAN §13): one or more of Ctrl, Alt and Shift, then a letter, a digit
/// or F1–F24. Ctrl or Alt is required so a shortcut never takes ordinary typing. The normal form
/// orders modifiers Ctrl, Alt, Shift and upper-cases the key, which is also the form the desktop
/// host registers.
/// </summary>
public static class Accelerators
{
    public static string? NormalizeOrNull(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return TryNormalize(text, out var normalized, out var error)
            ? normalized
            : throw AppException.Validation(field, error);
    }

    public static bool TryNormalize(string text, out string normalized, out string error)
    {
        normalized = "";
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 4 || parts.Any(part => part.Length == 0))
        {
            error = "Write a shortcut such as Ctrl+Alt+N.";
            return false;
        }

        bool ctrl = false, alt = false, shift = false;
        foreach (var modifier in parts[..^1])
        {
            switch (modifier.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL" when !ctrl: ctrl = true; break;
                case "ALT" when !alt: alt = true; break;
                case "SHIFT" when !shift: shift = true; break;
                default:
                    error = $"“{modifier}” is not Ctrl, Alt or Shift, or it is repeated.";
                    return false;
            }
        }

        if (!ctrl && !alt)
        {
            error = "A shortcut needs Ctrl or Alt.";
            return false;
        }

        var key = parts[^1].ToUpperInvariant();
        var validKey = (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
            || (key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out var function) && function is >= 1 and <= 24 && key[1] != '0');
        if (!validKey)
        {
            error = "End the shortcut with a letter, a digit or F1–F24.";
            return false;
        }

        normalized = string.Join('+', new[] { ctrl ? "Ctrl" : null, alt ? "Alt" : null, shift ? "Shift" : null, key }.Where(part => part is not null));
        error = "";
        return true;
    }
}
