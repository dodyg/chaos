using System.Net;
using Chaos.Application.Abstractions;
using Chaos.Application.Containers;
using Chaos.Application.Tags;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Service;
using Chaos.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Tests.Sync;

/// <summary>
/// PLAN2 P14 item 6: joining an account (first device, merge, replace), adopting the account's IDs, interrupted joins,
/// signing out in both modes, a revoked device, and deleting the account. Real SQLite data roots against the in-process
/// service on real PostgreSQL.
/// </summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class JoinTests(PostgresServer postgres)
{
    private static string Op() => Guid.NewGuid().ToString();

    private static AccountJoinService JoinFor(SyncDevice device)
    {
        var protection = new DataProtectionService(device.Store, device.Storage.Options,
            new BackupManager(device.Store.Source, device.Storage.Options.BackupsDirectory, "0.2.1", device.Clock), device.Ids, device.Clock);
        return new AccountJoinService(device.Client, new SyncAccountState(device.Store), protection, device.Clock, () => device.Engine.Wake());
    }

    private static async Task AddDataAsync(SyncDevice device, string tag, string list, params string[] tasks)
    {
        await new TagCommands(device.Store, device.Ids, device.Clock).CreateAsync(new CreateTagCommand(Op(), tag, null, null));
        await new ContainerCommands(device.Store, device.Ids, device.Clock).CreateAsync(new CreateContainerCommand(Op(), list, ContainerKind.List, null));
        foreach (var title in tasks)
        {
            await device.Tasks.CaptureAsync(new(Op(), string.Empty, title, null));
        }
    }

    /// <summary>Joins, then lets the engine do what it does (take the account's data, upload what is new) until nothing moves.</summary>
    private static async Task JoinAndSettleAsync(SyncWorld world, SyncDevice device, JoinMode mode)
    {
        await JoinFor(device).JoinAsync(mode, CancellationToken.None);
        await world.SettleAsync();
    }

    [Test]
    public async Task The_first_device_binds_its_ids_and_uploads_everything_it_has()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        await AddDataAsync(a, "work", "Errands", "One", "Two", "Three");
        var ids = await new SyncAccountState(a.Store).ReadIdsAsync(CancellationToken.None);

        var preview = await JoinFor(a).PrepareAsync(CancellationToken.None);
        await Assert.That(preview.Case).IsEqualTo(JoinCase.FirstDevice);
        await JoinAndSettleAsync(world, a, JoinMode.Merge);

        await using var db = ServerDatabase.Open(world.Server.ConnectionString);
        var account = await db.Accounts.AsNoTracking().SingleAsync(x => x.PrimaryEmail == SyncWorld.Email);
        await Assert.That(account.ProfileId.ToString()).IsEqualTo(ids.ProfileId);
        await Assert.That(account.PersonalSpaceId.ToString()).IsEqualTo(ids.PersonalSpaceId);
        await Assert.That(account.InboxId.ToString()).IsEqualTo(ids.InboxId);
        await Assert.That(await db.SyncEntities.CountAsync(e => e.Kind == "task")).IsEqualTo(3);
        await Assert.That(await a.CountAsync("SELECT count(*) FROM SyncOutbox;")).IsEqualTo(0L);
        await Assert.That(await a.CountAsync("SELECT BackfillPending FROM SyncState;")).IsEqualTo(0L);
        // The account now describes itself: the join dialog on the next device can show both sides.
        var again = await JoinFor(a).PrepareAsync(CancellationToken.None);
        await Assert.That(again.Case).IsEqualTo(JoinCase.AlreadyJoined);
        await Assert.That(again.Account!.Tasks).IsEqualTo(3L);
    }

    [Test]
    public async Task Merging_a_second_computer_with_its_own_data_gives_the_union_without_duplicate_tags_or_lists()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        var b = await world.AddDeviceAsync(joined: false);
        await AddDataAsync(a, "work", "Home", "From A");
        await AddDataAsync(b, "work", "Home", "From B");
        await JoinAndSettleAsync(world, a, JoinMode.Merge);

        var preview = await JoinFor(b).PrepareAsync(CancellationToken.None);
        await Assert.That(preview.Case).IsEqualTo(JoinCase.ChooseData);
        await Assert.That(preview.DefaultMode).IsEqualTo(JoinMode.Merge);
        await Assert.That(preview.Local.Tasks).IsEqualTo(1L);
        await Assert.That(preview.Account!.Tasks).IsEqualTo(1L);

        var before = await new SyncAccountState(b.Store).ReadIdsAsync(CancellationToken.None);
        await JoinAndSettleAsync(world, b, JoinMode.Merge);
        await world.SettleAsync();

        foreach (var device in new[] { a, b })
        {
            await Assert.That(await device.CountAsync("SELECT count(*) FROM Tasks WHERE DeletedAtUtc IS NULL;")).IsEqualTo(2L);
            await Assert.That(await device.CountAsync("SELECT count(*) FROM Tags WHERE DeletedAtUtc IS NULL;")).IsEqualTo(1L);
            var lists = await device.RowsAsync("SELECT Name FROM Containers WHERE DeletedAtUtc IS NULL AND IsInbox = 0 ORDER BY Name;");
            await Assert.That(lists.Count).IsEqualTo(2);
            await Assert.That(lists.Distinct().Count()).IsEqualTo(2);
        }

        var after = await new SyncAccountState(b.Store).ReadIdsAsync(CancellationToken.None);
        var a2 = await new SyncAccountState(a.Store).ReadIdsAsync(CancellationToken.None);
        await Assert.That(after).IsEqualTo(a2);
        await Assert.That(after).IsNotEqualTo(before);
        await world.AssertConvergedAsync();
        // The safety backup made before the change is in the backups folder.
        await Assert.That(Directory.EnumerateFiles(b.Storage.Options.BackupsDirectory, "chaos-*.db").Any()).IsTrue();
    }

    [Test]
    public async Task Replacing_removes_the_local_data_and_takes_the_accounts()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        var b = await world.AddDeviceAsync(joined: false);
        await AddDataAsync(a, "work", "Home", "From A");
        await AddDataAsync(b, "other", "Garden", "Only on B");
        await JoinAndSettleAsync(world, a, JoinMode.Merge);
        var deviceBefore = await b.TextAsync("SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';");

        await JoinAndSettleAsync(world, b, JoinMode.Replace);

        await Assert.That(await b.CountAsync("SELECT count(*) FROM Tasks WHERE Title = 'Only on B';")).IsEqualTo(0L);
        await Assert.That(await b.CountAsync("SELECT count(*) FROM Tasks WHERE Title = 'From A';")).IsEqualTo(1L);
        await Assert.That(await b.CountAsync("SELECT count(*) FROM Tags WHERE DisplayName = 'other';")).IsEqualTo(0L);
        await Assert.That(await b.TextAsync("SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';")).IsEqualTo(deviceBefore);
        await world.AssertConvergedAsync();
    }

    [Test]
    public async Task Adopt_rewrites_every_reference_to_the_old_ids_and_the_old_ids_are_nowhere_in_the_database()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        await AddDataAsync(a, "work", "Home", "One", "Two");
        var old = await new SyncAccountState(a.Store).ReadIdsAsync(CancellationToken.None);
        var target = new LocalIdentityIds(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        await new SyncAccountState(a.Store).AdoptAsync(target);

        await Assert.That(await new SyncAccountState(a.Store).ReadIdsAsync(CancellationToken.None)).IsEqualTo(target);
        await Assert.That(await a.RowsAsync("PRAGMA foreign_key_check;")).IsEmpty();
        // No text column of any table still holds an old ID (finds a reference the foreign-key list does not declare). Local-only logs and
        // the idempotency receipts (which hold the text of an earlier answer) are not data the account sees.
        var leftovers = new List<string>();
        foreach (var id in new[] { old.ProfileId, old.PersonalSpaceId, old.InboxId })
        {
            foreach (var table in await a.RowsAsync("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'Search%' AND name NOT LIKE 'ActivityLog%' AND name <> 'OperationReceipts' AND sql NOT LIKE 'CREATE VIRTUAL%';"))
            {
                foreach (var column in await a.RowsAsync($"SELECT name FROM pragma_table_info('{table}') WHERE type LIKE '%TEXT%';"))
                {
                    if (await a.CountAsync($"SELECT count(*) FROM \"{table}\" WHERE \"{column}\" LIKE '%{id}%';") > 0)
                    {
                        leftovers.Add($"{table}.{column}");
                    }
                }
            }
        }

        await Assert.That(leftovers.Distinct().ToList()).IsEmpty();
    }

    [Test]
    public async Task A_failure_part_way_through_adopting_changes_nothing()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        await AddDataAsync(a, "work", "Home", "One");
        var state = new SyncAccountState(a.Store);
        var before = await state.ReadIdsAsync(CancellationToken.None);
        var snapshotBefore = await a.SnapshotAsync();
        var target = new LocalIdentityIds(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());

        for (var failAfter = 0; failAfter < 6; failAfter++)
        {
            await Assert.ThrowsAsync<AppException>(() => state.AdoptAsync(target, new AdoptFaultInjection(failAfter)));
            await Assert.That(await state.ReadIdsAsync(CancellationToken.None)).IsEqualTo(before);
            await Assert.That(await a.SnapshotAsync()).IsEqualTo(snapshotBefore);
            await Assert.That(await a.RowsAsync("PRAGMA foreign_key_check;")).IsEmpty();
        }
    }

    [Test]
    public async Task A_join_interrupted_after_the_backup_is_undone_at_the_next_start()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        var b = await world.AddDeviceAsync(joined: false);
        await AddDataAsync(a, "work", "Home", "From A");
        await AddDataAsync(b, "other", "Garden", "Only on B");
        await JoinAndSettleAsync(world, a, JoinMode.Merge);

        var join = JoinFor(b);
        var crash = new InvalidOperationException("The computer lost power.");
        join.AfterMarker = () => throw crash;
        await Assert.ThrowsAsync<InvalidOperationException>(() => join.JoinAsync(JoinMode.Replace, CancellationToken.None));
        // A real crash would not have run the cleanup; put the marker back as it would be.
        var markerPath = AccountJoinService.MarkerPath(b.Storage.Options);
        await Assert.That(File.Exists(markerPath)).IsFalse();
        var backup = Directory.EnumerateFiles(b.Storage.Options.BackupsDirectory, "chaos-*.db").OrderByDescending(File.GetCreationTimeUtc).First();
        await File.WriteAllTextAsync(markerPath, $$"""{"stage":"started","mode":1,"safetyBackupFileName":"{{Path.GetFileName(backup)}}","startedAtUtc":"2026-11-18T10:00:00+00:00"}""");

        // The replace emptied the data before the "crash".
        await new DataProtectionService(b.Store, b.Storage.Options, new BackupManager(b.Store.Source, b.Storage.Options.BackupsDirectory, "0.2.1", b.Clock), b.Ids, b.Clock)
            .ResetKeepingDeviceAsync(CancellationToken.None);
        await Assert.That(await b.CountAsync("SELECT count(*) FROM Tasks WHERE Title = 'Only on B';")).IsEqualTo(0L);

        var root = b.Root;
        await b.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var options = StorageOptions.FromDataRoot(root);
        var notice = AccountJoinService.RecoverInterrupted(options);

        await Assert.That(notice).IsNotNull();
        await Assert.That(notice!.KeptPrevious).IsFalse();
        await Assert.That(File.Exists(markerPath)).IsFalse();
        var check = BackupManager.CheckDatabaseFile(options.DatabasePath);
        await Assert.That(check.Valid).IsTrue();
        await Assert.That(check.Counts.Tasks).IsEqualTo(1L);
    }

    [Test]
    public async Task Signing_out_and_keeping_data_leaves_the_computer_offline_with_its_data_and_revokes_the_token()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        await AddDataAsync(a, "work", "Home", "One");
        await JoinAndSettleAsync(world, a, JoinMode.Merge);
        var refresh = a.Secrets.Values[ChaosServiceClient.RefreshKey];
        await a.Tasks.CaptureAsync(new(Op(), string.Empty, "Queued but not sent", null));

        await JoinFor(a).SignOutAsync(keepData: true, CancellationToken.None);

        await Assert.That(await a.CountAsync("SELECT Enabled FROM SyncState;")).IsEqualTo(0L);
        await Assert.That(await a.CountAsync("SELECT count(*) FROM SyncOutbox;")).IsEqualTo(0L);
        await Assert.That(await a.CountAsync("SELECT count(*) FROM Tasks WHERE DeletedAtUtc IS NULL;")).IsEqualTo(2L);
        await Assert.That(a.Secrets.Values.ContainsKey(ChaosServiceClient.RefreshKey)).IsFalse();

        // The token was revoked on the server, not just forgotten: it cannot be used again.
        using var http = world.Server.Factory.CreateClient();
        using var response = await http.PostAsync("/connect/token", new FormUrlEncodedContent(
            [new("grant_type", "refresh_token"), new("client_id", ChaosServiceClient.ClientId), new("refresh_token", refresh)]));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Signing_out_and_removing_data_resets_the_computer_but_keeps_its_identity()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        await AddDataAsync(a, "work", "Home", "One");
        await JoinAndSettleAsync(world, a, JoinMode.Merge);
        var device = await a.TextAsync("SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';");

        await JoinFor(a).SignOutAsync(keepData: false, CancellationToken.None);

        await Assert.That(await a.CountAsync("SELECT count(*) FROM Tasks;")).IsEqualTo(0L);
        await Assert.That(await a.CountAsync("SELECT Enabled FROM SyncState;")).IsEqualTo(0L);
        await Assert.That(await a.TextAsync("SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';")).IsEqualTo(device);
    }

    [Test]
    public async Task A_removed_device_goes_offline_only_and_keeps_its_data()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        var b = await world.AddDeviceAsync(joined: false);
        await AddDataAsync(a, "work", "Home", "From A");
        await JoinAndSettleAsync(world, a, JoinMode.Merge);
        await JoinAndSettleAsync(world, b, JoinMode.Replace);
        var bDevice = Guid.Parse((await b.TextAsync("SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';"))!);

        await a.Client.RevokeDeviceAsync(bDevice, CancellationToken.None);
        await b.Tasks.CaptureAsync(new(Op(), string.Empty, "Made after removal", null));
        var result = await b.SyncAsync();

        await Assert.That(result.Problem).IsNotNull();
        await Assert.That(await b.CountAsync("SELECT Enabled FROM SyncState;")).IsEqualTo(0L);
        await Assert.That(await b.CountAsync("SELECT count(*) FROM Tasks WHERE DeletedAtUtc IS NULL;")).IsEqualTo(2L);
        await Assert.That(b.Secrets.Values.ContainsKey(ChaosServiceClient.RefreshKey)).IsFalse();
    }

    [Test]
    public async Task Deleting_the_account_removes_every_server_row_and_other_devices_go_offline_with_their_data()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        var b = await world.AddDeviceAsync(joined: false);
        await AddDataAsync(a, "work", "Home", "From A");
        await JoinAndSettleAsync(world, a, JoinMode.Merge);
        await JoinAndSettleAsync(world, b, JoinMode.Replace);
        var accountId = (await a.Client.GetAccountAsync(CancellationToken.None)).AccountId!.Value;

        // The sign-in is older than ten minutes by the time the person decides.
        world.Server.Clock.Advance(TimeSpan.FromMinutes(11));
        var tooOld = await Assert.ThrowsAsync<ChaosServiceException>(() => JoinFor(a).DeleteAccountAsync("DELETE", CancellationToken.None));
        await Assert.That(tooOld!.Kind).IsEqualTo(ServiceFailure.FreshSignInRequired);

        await a.Client.ReauthenticateAsync(CancellationToken.None);
        var wrong = await Assert.ThrowsAsync<ChaosServiceException>(() => JoinFor(a).DeleteAccountAsync("delete it", CancellationToken.None));
        await Assert.That(wrong!.Kind).IsEqualTo(ServiceFailure.Refused);
        await JoinFor(a).DeleteAccountAsync("DELETE", CancellationToken.None);

        await using (var db = ServerDatabase.Open(world.Server.ConnectionString))
        {
            await Assert.That(await db.Accounts.CountAsync(x => x.Id == accountId)).IsEqualTo(0);
            await Assert.That(await db.Devices.CountAsync(x => x.AccountId == accountId)).IsEqualTo(0);
            await Assert.That(await db.AccountLogins.CountAsync(x => x.AccountId == accountId)).IsEqualTo(0);
            await Assert.That(await db.SyncEntities.CountAsync(x => x.AccountId == accountId)).IsEqualTo(0);
            await Assert.That(await db.SyncChanges.CountAsync(x => x.AccountId == accountId)).IsEqualTo(0);
            await Assert.That(await db.AuditEvents.CountAsync(x => x.AccountId == accountId && x.Kind == "account-deleted")).IsEqualTo(1);
            var subject = accountId.ToString("D");
            await Assert.That(await db.Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken>().CountAsync(t => t.Subject == subject)).IsEqualTo(0);
        }

        // The device that deleted it keeps its data and stops syncing; so does the other one, the next time it calls.
        await Assert.That(await a.CountAsync("SELECT Enabled FROM SyncState;")).IsEqualTo(0L);
        await Assert.That(await a.CountAsync("SELECT count(*) FROM Tasks WHERE DeletedAtUtc IS NULL;")).IsEqualTo(1L);
        await b.Tasks.CaptureAsync(new(Op(), string.Empty, "Made after deletion", null));
        var result = await b.SyncAsync();
        await Assert.That(result.Problem).IsNotNull();
        await Assert.That(await b.CountAsync("SELECT Enabled FROM SyncState;")).IsEqualTo(0L);
        await Assert.That(await b.CountAsync("SELECT count(*) FROM Tasks WHERE DeletedAtUtc IS NULL;")).IsEqualTo(2L);
    }

    [Test]
    public async Task A_computer_bound_to_a_deleted_account_can_join_the_new_one_that_replaces_it()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        await AddDataAsync(a, "work", "Home", "One");
        await JoinAndSettleAsync(world, a, JoinMode.Merge);
        await a.Client.ReauthenticateAsync(CancellationToken.None);
        await JoinFor(a).DeleteAccountAsync("DELETE", CancellationToken.None);

        await a.Client.SignInAsync(CancellationToken.None);
        var preview = await JoinFor(a).PrepareAsync(CancellationToken.None);

        await Assert.That(preview.Case).IsEqualTo(JoinCase.FirstDevice);
        await Assert.That(preview.Local.Tasks).IsEqualTo(1L);
    }

    [Test]
    public async Task Resetting_the_device_identity_gives_a_new_id_and_is_refused_while_signed_in()
    {
        await using var world = await SyncWorld.StartAsync(postgres);
        var a = await world.AddDeviceAsync(joined: false);
        var before = await a.TextAsync("SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';");

        await Assert.ThrowsAsync<AppException>(() => JoinFor(a).ResetDeviceIdentityAsync(CancellationToken.None));
        await a.Client.SignOutAsync(CancellationToken.None);
        await JoinFor(a).ResetDeviceIdentityAsync(CancellationToken.None);

        var after = await a.TextAsync("SELECT Value FROM DeviceSettings WHERE Key = 'DeviceId';");
        await Assert.That(after).IsNotEqualTo(before);
        await Assert.That(Guid.TryParse(after, out _)).IsTrue();
    }
}
