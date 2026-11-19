using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync;

namespace Chaos.Infrastructure.Service;

/// <summary>What happens when this computer joins the account it just signed in to (PLAN2 §7.13).</summary>
public enum JoinCase
{
    /// <summary>This computer already syncs with this account.</summary>
    AlreadyJoined = 0,

    /// <summary>The account has no data yet: this computer's data becomes the account's.</summary>
    FirstDevice = 1,

    /// <summary>The account has data: the person chooses to merge this computer's data into it, or replace it.</summary>
    ChooseData = 2,
}

public enum JoinMode
{
    /// <summary>Back up, adopt the account's IDs, take the account's data, then upload what is new here.</summary>
    Merge = 0,

    /// <summary>Back up, remove this computer's data, take the account's.</summary>
    Replace = 1,
}

/// <summary>What the join dialog shows: this computer's counts and the account's, and what is recommended.</summary>
public sealed record JoinPreview(JoinCase Case, LocalContentCounts Local, AccountContentCounts? Account, JoinMode DefaultMode);

public sealed record JoinOutcome(JoinCase Case, JoinMode? Mode, string? SafetyBackup);

/// <summary>Where an interrupted join had got to; written before the first change so startup can undo it.</summary>
public sealed record JoinMarker(string Stage, JoinMode Mode, string SafetyBackupFileName, DateTimeOffset StartedAtUtc);

/// <summary>
/// Joining, leaving and deleting an account from this computer (PLAN2 P14). Joining the first time binds this computer's IDs as the
/// account's; joining an account that has data asks to merge or replace, always after a safety backup and with background work
/// paused. A marker file names the backup while the local steps run, so a crash at any point restarts into the data as it was
/// before (<see cref="RecoverInterrupted"/>); once sync is on, the rest (take the account's data, upload what is new) is the sync
/// engine's, driven by <c>SyncState.BackfillPending</c>, and survives a restart by itself.
/// </summary>
public sealed class AccountJoinService(IChaosServiceClient service, SyncAccountState account, DataProtectionService protection, TimeProvider clock, Action? wakeSync = null)
{
    public const string MarkerFileName = "join-pending.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string MarkerPath(StorageOptions options) => Path.Combine(Path.GetDirectoryName(options.DatabasePath)!, MarkerFileName);

    /// <summary>Test hook: runs after the safety backup and the marker exist, before any change, so a test can "crash" there.</summary>
    public Func<Task>? AfterMarker { get; set; }

    /// <summary>Test hook: injects a failure into the adopt step.</summary>
    public AdoptFaultInjection? AdoptFault { get; set; }

    // ---- Join ----------------------------------------------------------------------------------------

    /// <summary>
    /// Looks at the account and this computer without changing the data. A computer still bound to a different account (it was
    /// deleted and made again, or another person signed in) is first made offline-only: its data stays, only the binding goes.
    /// </summary>
    public async Task<JoinPreview> PrepareAsync(CancellationToken ct)
    {
        var remote = await service.GetAccountAsync(ct);
        var bound = await account.BoundAccountAsync(ct);
        if (bound is not null && remote.AccountId is { } id && !string.Equals(bound, id.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            await account.DisableAsync(ct);
            bound = null;
        }

        var local = await account.CountAsync(ct);
        if (bound is not null && await account.IsEnabledAsync(ct))
        {
            return new JoinPreview(JoinCase.AlreadyJoined, local, remote.Counts, JoinMode.Merge);
        }

        if (remote.Canonical is null)
        {
            return new JoinPreview(JoinCase.FirstDevice, local, remote.Counts, JoinMode.Merge);
        }

        // Merge is the default whenever this computer has anything of its own; an empty computer simply takes the account's data.
        return new JoinPreview(JoinCase.ChooseData, local, remote.Counts, local.Any ? JoinMode.Merge : JoinMode.Replace);
    }

    /// <summary>Joins as <see cref="PrepareAsync"/> described. <paramref name="mode"/> is used only when the account already has data.</summary>
    public async Task<JoinOutcome> JoinAsync(JoinMode mode, CancellationToken ct)
    {
        var preview = await PrepareAsync(ct);
        if (preview.Case == JoinCase.AlreadyJoined)
        {
            return new JoinOutcome(JoinCase.AlreadyJoined, null, null);
        }

        var remote = await service.GetAccountAsync(ct);
        var accountId = remote.AccountId ?? throw new ChaosServiceException(ServiceFailure.Other, "The service did not say which account this is.");
        var ids = await account.ReadIdsAsync(ct);
        var canonical = remote.Canonical;
        if (canonical is null)
        {
            // The first device: its IDs become the account's. If another device bound first, this one is joining after all.
            var (bound, answer) = await service.BindAccountAsync(Guid.Parse(ids.ProfileId), Guid.Parse(ids.PersonalSpaceId), Guid.Parse(ids.InboxId), ct);
            if (bound)
            {
                await account.EnableAsync(accountId, ct);
                wakeSync?.Invoke();
                return new JoinOutcome(JoinCase.FirstDevice, null, null);
            }

            canonical = answer;
        }

        var target = new LocalIdentityIds(canonical.ProfileId.ToString("D"), canonical.PersonalSpaceId.ToString("D"), canonical.InboxId.ToString("D"));
        var safety = await protection.GuardAsync(() => JoinLocalAsync(mode, accountId, target, ct), ct);
        wakeSync?.Invoke();
        return new JoinOutcome(JoinCase.ChooseData, mode, safety);
    }

    private async Task<string> JoinLocalAsync(JoinMode mode, Guid accountId, LocalIdentityIds target, CancellationToken ct)
    {
        var backup = await protection.Backups.CreateAsync(BackupReason.PreRestore, ct);
        var markerPath = MarkerPath(protection.Options);
        WriteMarker(markerPath, new JoinMarker("started", mode, backup.FileName, clock.GetUtcNow()));
        try
        {
            if (AfterMarker is not null)
            {
                await AfterMarker();
            }

            if (mode == JoinMode.Replace)
            {
                await protection.ResetKeepingDeviceAsync(ct);
            }

            await protection.Quiesce(async () =>
            {
                await account.AdoptAsync(target, AdoptFault, ct);
                await account.EnableAsync(accountId, ct);
            }, ct);
        }
        catch
        {
            // The adopt is one transaction, so a failure there changed nothing. A failure after a replace has emptied the data is
            // undone from the backup, the same way a crash would be at the next start.
            if (mode == JoinMode.Replace)
            {
                await TryRestoreAsync(backup.FileName, ct);
            }

            File.Delete(markerPath);
            throw;
        }

        File.Delete(markerPath);
        return backup.FileName;
    }

    private async Task TryRestoreAsync(string fileName, CancellationToken ct)
    {
        try
        {
            var path = protection.Backups.ResolveBackupPath(fileName);
            await protection.RestoreAfterFailedJoinAsync(path, ct);
        }
        catch (Exception error) when (error is AppException or IOException)
        {
            // Nothing more can be done here: the safety backup is still in the backups folder for the person to restore.
            System.Diagnostics.Trace.TraceWarning("Account join recovery failed; the safety backup remains available ({0}).", error.GetType().Name);
        }
    }

    private static void WriteMarker(string path, JoinMarker marker)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(marker, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Startup, before any connection opens: a join that was interrupted after its safety backup is undone by putting that
    /// backup back (the same swap-with-marker a restore uses). Nothing to do when no join was in progress.
    /// </summary>
    public static RecoveryNotice? RecoverInterrupted(StorageOptions options)
    {
        var markerPath = MarkerPath(options);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        JoinMarker? marker = null;
        try
        {
            marker = JsonSerializer.Deserialize<JoinMarker>(File.ReadAllText(markerPath), Json);
        }
        catch (JsonException error)
        {
            // An unreadable marker is treated as "a join was interrupted" with no backup to name.
            System.Diagnostics.Trace.TraceWarning("Account join marker could not be read: {0}", error.GetType().Name);
        }

        var backup = marker is null ? null : Path.Combine(options.BackupsDirectory, Path.GetFileName(marker.SafetyBackupFileName));
        if (backup is null || !File.Exists(backup) || !BackupManager.CheckDatabaseFile(backup).Valid)
        {
            File.Delete(markerPath);
            return new RecoveryNotice(true, "Joining your account was interrupted. Chaos kept the data on this computer as it was.");
        }

        var staged = DatabaseReplacement.StagedPath(options);
        File.Copy(backup, staged, overwrite: true);
        DatabaseReplacement.Swap(options, staged, "join of an account", DateTimeOffset.UtcNow);
        if (BackupManager.CheckDatabaseFile(options.DatabasePath).Valid)
        {
            DatabaseReplacement.Complete(options);
        }
        else
        {
            DatabaseReplacement.Revert(options);
        }

        File.Delete(markerPath);
        return new RecoveryNotice(false, "Joining your account was interrupted, so Chaos put back the data this computer had before. You can try again from Settings.");
    }

    // ---- Leaving -------------------------------------------------------------------------------------

    /// <summary>
    /// Signs out (PLAN2 §7.13 case 3). Either way sync stops and the outbox is cleared, the service is asked to revoke this device's
    /// refresh token, and it is removed from the secret store. <paramref name="keepData"/> leaves this computer's data as it is;
    /// otherwise the local data is reset as "Delete all local data" does (this computer's identity is kept).
    /// </summary>
    public async Task SignOutAsync(bool keepData, CancellationToken ct)
    {
        await account.DisableAsync(ct);
        await service.SignOutAsync(ct);
        if (!keepData)
        {
            await protection.GuardAsync(async () =>
            {
                await protection.ResetKeepingDeviceAsync(ct);
                return 0;
            }, ct);
        }
    }

    /// <summary>Deletes the account on the service (typed phrase and a recent sign-in required there). This computer keeps its data and goes offline-only.</summary>
    public async Task DeleteAccountAsync(string confirmation, CancellationToken ct)
    {
        await service.DeleteAccountAsync(confirmation, ct);
        await account.DisableAsync(ct);
    }

    /// <summary>
    /// "Reset device identity" for a data root that was copied from another computer: a new device ID and a fresh clock, with sync
    /// off. Refused while signed in, because the account knows this computer by the old ID.
    /// </summary>
    public async Task ResetDeviceIdentityAsync(CancellationToken ct)
    {
        if ((await service.GetSessionAsync(ct)).SignedIn)
        {
            throw AppException.Validation("device", "Sign out of your account first. The account knows this computer by its current identity.");
        }

        await account.DisableAsync(ct);
        await account.ReplaceDeviceIdAsync(Guid.NewGuid(), ct);
    }
}
