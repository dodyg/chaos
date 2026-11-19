using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Domain.Tasks;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

public sealed class SqliteProfileStore(SqliteConnection connection) : IProfileStore
{
    public async Task<ProfileRecord?> GetAsync(string profileId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Profiles WHERE Id = $id AND DeletedAtUtc IS NULL;";
        command.Parameters.AddWithValue("$id", profileId);
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProfile(reader) : null;
    }

    public async Task<ProfileRecord?> GetPrimaryAsync(CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Profiles WHERE DeletedAtUtc IS NULL ORDER BY CreatedAtUtc LIMIT 1;";
        using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadProfile(reader) : null;
    }

    public async Task AddAsync(ProfileRecord profile, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Profiles (Id, DisplayName, ZoneId, Locale, DateFormat, FirstWeekday,
                PlanningTime, SummaryTime, MorningTime, WeeklySummaryDay, DefaultReminderTime, QuietHoursEnabled, QuietStart, QuietEnd,
                CaptureShortcut, PadShortcut, KeepRunningInTray,
                Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $name, $zone, $locale, $dateFormat, $firstWeekday,
                $planningTime, $summaryTime, $morningTime, $weeklyDay, $defaultReminderTime, $quietEnabled, $quietStart, $quietEnd,
                $captureShortcut, $padShortcut, $keepRunning,
                $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$name", profile.DisplayName);
        command.Parameters.AddWithValue("$zone", profile.ZoneId);
        command.Parameters.AddWithValue("$locale", profile.Locale);
        command.Parameters.AddWithValue("$dateFormat", profile.DateFormat);
        command.Parameters.AddWithValue("$firstWeekday", (long)profile.FirstWeekday);
        command.Parameters.AddWithValue("$planningTime", Optional(profile.PlanningTime));
        command.Parameters.AddWithValue("$summaryTime", Optional(profile.SummaryTime));
        command.Parameters.AddWithValue("$morningTime", Optional(profile.MorningTime));
        command.Parameters.AddWithValue("$weeklyDay", profile.WeeklySummaryDay is { } weeklyDay ? (long)weeklyDay : DBNull.Value);
        command.Parameters.AddWithValue("$defaultReminderTime", StorageFormats.Time(profile.DefaultReminderTime));
        command.Parameters.AddWithValue("$quietEnabled", profile.QuietHoursEnabled ? 1L : 0L);
        command.Parameters.AddWithValue("$quietStart", Optional(profile.QuietStart));
        command.Parameters.AddWithValue("$quietEnd", Optional(profile.QuietEnd));
        command.Parameters.AddWithValue("$captureShortcut", OptionalValue(profile.CaptureShortcut));
        command.Parameters.AddWithValue("$padShortcut", OptionalValue(profile.PadShortcut));
        command.Parameters.AddWithValue("$keepRunning", profile.KeepRunningInTray ? 1L : 0L);
        command.Parameters.AddWithValue("$version", profile.Version);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(profile.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(profile.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", OptionalInstant(profile.DeletedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<ProfileRecord?> UpdateFirstWeekdayAsync(ProfileRecord profile, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Profiles SET FirstWeekday = $firstWeekday, Version = $version, UpdatedAtUtc = $updated
            WHERE Id = $id AND Version = $expected AND DeletedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$firstWeekday", (long)profile.FirstWeekday);
        command.Parameters.AddWithValue("$version", profile.Version);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(profile.UpdatedAtUtc));
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? profile : null;
    }

    public async Task<ProfileRecord?> UpdateReminderSettingsAsync(ProfileRecord profile, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Profiles SET DefaultReminderTime = $defaultReminderTime, QuietHoursEnabled = $quietEnabled,
                QuietStart = $quietStart, QuietEnd = $quietEnd, Version = $version, UpdatedAtUtc = $updated
            WHERE Id = $id AND Version = $expected AND DeletedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$defaultReminderTime", StorageFormats.Time(profile.DefaultReminderTime));
        command.Parameters.AddWithValue("$quietEnabled", profile.QuietHoursEnabled ? 1L : 0L);
        command.Parameters.AddWithValue("$quietStart", Optional(profile.QuietStart));
        command.Parameters.AddWithValue("$quietEnd", Optional(profile.QuietEnd));
        command.Parameters.AddWithValue("$version", profile.Version);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(profile.UpdatedAtUtc));
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? profile : null;
    }

    public async Task<ProfileRecord?> UpdateSummarySettingsAsync(ProfileRecord profile, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Profiles SET MorningTime = $morningTime, SummaryTime = $summaryTime, WeeklySummaryDay = $weeklyDay, IncludeMeetingsInSummaries = $meetings,
                Version = $version, UpdatedAtUtc = $updated
            WHERE Id = $id AND Version = $expected AND DeletedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$morningTime", Optional(profile.MorningTime));
        command.Parameters.AddWithValue("$summaryTime", Optional(profile.SummaryTime));
        command.Parameters.AddWithValue("$weeklyDay", profile.WeeklySummaryDay is { } weeklyDay ? (long)weeklyDay : DBNull.Value);
        command.Parameters.AddWithValue("$version", profile.Version);
        command.Parameters.AddWithValue("$meetings", profile.IncludeMeetingsInSummaries ? 1L : 0L);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(profile.UpdatedAtUtc));
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? profile : null;
    }

    public async Task<ProfileRecord?> UpdateGeneralSettingsAsync(ProfileRecord profile, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Profiles SET DisplayName = $name, ZoneId = $zone, DateFormat = $dateFormat,
                CaptureShortcut = $captureShortcut, PadShortcut = $padShortcut, KeepRunningInTray = $keepRunning,
                Version = $version, UpdatedAtUtc = $updated
            WHERE Id = $id AND Version = $expected AND DeletedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$name", profile.DisplayName);
        command.Parameters.AddWithValue("$zone", profile.ZoneId);
        command.Parameters.AddWithValue("$dateFormat", profile.DateFormat);
        command.Parameters.AddWithValue("$captureShortcut", OptionalValue(profile.CaptureShortcut));
        command.Parameters.AddWithValue("$padShortcut", OptionalValue(profile.PadShortcut));
        command.Parameters.AddWithValue("$keepRunning", profile.KeepRunningInTray ? 1L : 0L);
        command.Parameters.AddWithValue("$version", profile.Version);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(profile.UpdatedAtUtc));
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? profile : null;
    }

    internal static ProfileRecord ReadProfile(SqliteDataReader r) => new()
    {
        Id = r.S("Id"),
        DisplayName = r.S("DisplayName"),
        ZoneId = r.S("ZoneId"),
        Locale = r.S("Locale"),
        DateFormat = r.S("DateFormat"),
        FirstWeekday = (DayOfWeek)(int)r.L("FirstWeekday"),
        PlanningTime = r.Tn("PlanningTime"),
        SummaryTime = r.Tn("SummaryTime"),
        MorningTime = r.Tn("MorningTime"),
        WeeklySummaryDay = r.Ln("WeeklySummaryDay") is { } weeklyDay ? (DayOfWeek)(int)weeklyDay : null,
        IncludeMeetingsInSummaries = r.B("IncludeMeetingsInSummaries"),
        DefaultReminderTime = StorageFormats.ParseTime(r.S("DefaultReminderTime"))!.Value,
        QuietHoursEnabled = r.B("QuietHoursEnabled"),
        QuietStart = r.Tn("QuietStart"),
        QuietEnd = r.Tn("QuietEnd"),
        CaptureShortcut = r.Sn("CaptureShortcut"),
        PadShortcut = r.Sn("PadShortcut"),
        KeepRunningInTray = r.B("KeepRunningInTray"),
        Version = r.L("Version"),
        CreatedAtUtc = r.I("CreatedAtUtc"),
        UpdatedAtUtc = r.I("UpdatedAtUtc"),
        DeletedAtUtc = r.In("DeletedAtUtc"),
    };

    internal static object Optional(TimeOnly? time) => time is null ? DBNull.Value : StorageFormats.Time(time.Value);

    internal static object OptionalValue(string? value) => value is null ? DBNull.Value : value;

    internal static object OptionalInstant(DateTimeOffset? instant) => instant is null ? DBNull.Value : StorageFormats.Instant(instant.Value);

    internal static object OptionalDate(DateOnly? date) => date is null ? DBNull.Value : StorageFormats.Date(date.Value);
}
