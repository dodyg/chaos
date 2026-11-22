using System.Diagnostics;
using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.Attachments;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Containers;
using Chaos.Application.Planning;
using Chaos.Application.Queries;
using Chaos.Application.Spaces;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Activity;
using Chaos.Infrastructure.Attachments;
using Chaos.Server.Tests.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Tests.Sharing;

/// <summary>The seeded profile, Personal space and Inbox of one account; every later id is random.</summary>
internal sealed class AccountSeedIds(int account) : IIdGenerator
{
    private int _seeded;

    public Guid Profile => Seed(0);
    public Guid Space => Seed(1);
    public Guid Inbox => Seed(2);

    private Guid Seed(int index) => new(index, (short)account, 0, [0xA, 0xC, 0, 0, 0, 0, 0, (byte)account]);

    public Guid NewId() => _seeded < 3 ? Seed(_seeded++) : Guid.NewGuid();
}

/// <summary>One person with one or more desktops, and the space commands of each desktop.</summary>
internal sealed class SharingPerson
{
    public required string Email { get; init; }
    public required Guid AccountId { get; init; }
    public required Guid ProfileId { get; init; }
    public required List<SyncDevice> Devices { get; init; }
    public SyncDevice Desktop => Devices[0];

    public SpaceCommands Spaces(int device = 0) =>
        new(Devices[device].Store, Devices[device].Client, new SyncSpaceStreams(Devices[device].Store, Devices[device].Engine), Devices[device].Ids);
}

/// <summary>A hosted service on real PostgreSQL with several people, each on real desktops (PLAN3 Q4 step 6).</summary>
internal sealed class SharingWorld : IAsyncDisposable
{
    private readonly List<SyncDevice> _devices = [];
    private int _accounts;

    public required TestServer Server { get; init; }

    public static async Task<SharingWorld> StartAsync(PostgresServer postgres) => new() { Server = await TestServer.StartAsync(postgres) };

    public async Task<SharingPerson> AddPersonAsync(string email, string displayName, int devices = 1, SyncEngineOptions? options = null)
    {
        var account = ++_accounts;
        var list = new List<SyncDevice>();
        Guid profile = default;
        for (var index = 0; index < devices; index++)
        {
            var seeds = new AccountSeedIds(account);
            profile = seeds.Profile;
            var device = await SyncDevice.StartAsync(Server, email, new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero),
                options ?? new SyncEngineOptions { Jitter = () => 0 }, joined: true, seedIds: seeds);
            list.Add(device);
            _devices.Add(device);
            if (index == 0)
            {
                // The join flow binds the account's canonical ids on its first desktop; the tests do it directly.
                await using var db = ServerDatabase.Open(Server.ConnectionString);
                await db.Accounts.Where(a => a.PrimaryEmail == email).ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.ProfileId, seeds.Profile)
                    .SetProperty(a => a.PersonalSpaceId, seeds.Space)
                    .SetProperty(a => a.InboxId, seeds.Inbox)
                    .SetProperty(a => a.DisplayName, displayName));
            }
        }

        await using var read = ServerDatabase.Open(Server.ConnectionString);
        var id = await read.Accounts.Where(a => a.PrimaryEmail == email).Select(a => a.Id).SingleAsync();
        return new SharingPerson { Email = email, AccountId = id, ProfileId = profile, Devices = list };
    }

    /// <summary>Syncs every desktop in turn until a whole round pushes and pulls nothing.</summary>
    public async Task SettleAsync()
    {
        for (var round = 0; round < 10; round++)
        {
            var busy = false;
            foreach (var device in _devices)
            {
                var result = await device.SyncAsync();
                if (result.Problem is not null)
                {
                    throw new InvalidOperationException("A sync cycle failed: " + result.Problem);
                }

                busy |= result.Pushed > 0 || result.Pulled > 0 || result.SnapshotUsed;
            }

            if (!busy)
            {
                return;
            }
        }

        throw new InvalidOperationException("The desktops did not settle within ten rounds.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var device in _devices)
        {
            await device.DisposeAsync();
        }

        await Server.DisposeAsync();
    }
}

/// <summary>
/// PLAN3 Q4 exit: two people share a space end to end through real desktops (real SQLite, real
/// <see cref="SyncEngine"/>, real <see cref="SpaceCommands"/>) against the in-process service on real PostgreSQL.
/// </summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class SharingDesktopTests(PostgresServer postgres)
{
    private static string Op() => Guid.NewGuid().ToString();

    [Test]
    public async Task EachAssignment_ThreeMembersSeeTheirWorkAndOfflineCompletionsConverge()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("assignment-three-a@example.com", "Ada");
        var b = await world.AddPersonAsync("assignment-three-b@example.com", "Bo");
        var c = await world.AddPersonAsync("assignment-three-c@example.com", "Cy");
        var (space, _, taskId) = await ShareAsync(world, a, b);
        await c.Spaces().JoinWithLinkAsync(await InviteAsync(a, space));
        await world.SettleAsync();
        var current = (await a.Desktop.Tasks.GetAsync(taskId))!;
        foreach (var person in new[] { a, b, c })
            current = (await a.Desktop.Tasks.ChangeAssigneeAsync(new(Op(), taskId, current.Version, person.ProfileId.ToString("D"), true))).Task;
        current = (await a.Desktop.Tasks.SetAssignModeAsync(new(Op(), taskId, current.Version, TaskAssignMode.Each))).Task;
        await world.SettleAsync();
        foreach (var person in new[] { a, b, c })
        {
            var page = await new TaskQueries(person.Desktop.Store, person.Desktop.Clock).RunAsync(new TaskQuerySpec { View = TaskView.AssignedToMe });
            await Assert.That(page.Rows.Select(row => row.Task.Id)).Contains(taskId);
        }
        await a.Desktop.Tasks.SetStatusAsync(new(Op(), taskId, current.Version, TaskItemStatus.Done, null));
        await world.SettleAsync();
        b.Desktop.Clock.Now = b.Desktop.Clock.Now.AddMinutes(1);
        c.Desktop.Clock.Now = c.Desktop.Clock.Now.AddMinutes(2);
        var bTask = (await b.Desktop.Tasks.GetAsync(taskId))!;
        var cTask = (await c.Desktop.Tasks.GetAsync(taskId))!;
        await b.Desktop.Tasks.SetStatusAsync(new(Op(), taskId, bTask.Version, TaskItemStatus.Done, null));
        await c.Desktop.Tasks.SetStatusAsync(new(Op(), taskId, cTask.Version, TaskItemStatus.Done, null));
        await Assert.That((await b.Desktop.Tasks.GetAsync(taskId))!.Status).IsEqualTo(TaskItemStatus.NotStarted);
        await Assert.That((await c.Desktop.Tasks.GetAsync(taskId))!.Status).IsEqualTo(TaskItemStatus.NotStarted);
        await world.SettleAsync();
        foreach (var person in new[] { a, b, c })
        {
            var task = (await person.Desktop.Tasks.GetAsync(taskId))!;
            await Assert.That(task.Status).IsEqualTo(TaskItemStatus.Done);
            await Assert.That(task.CompletedAtUtc).IsEqualTo(c.Desktop.Clock.Now);
        }
    }

    private static async Task<string> CreateListAsync(SyncDevice device, string spaceId, string name) =>
        (await new ContainerCommands(device.Store, device.Ids, device.Clock).CreateAsync(new(Op(), name, ContainerKind.List, spaceId))).Container.Id;

    private static async Task<string> CaptureAsync(SyncDevice device, string containerId, string title) =>
        (await device.Tasks.CaptureAsync(new(Op(), containerId, title, null))).Task.Id;

    private static async Task EditAsync(SyncDevice device, string taskId, string? title = null, string? notes = null)
    {
        var current = (await device.Tasks.GetAsync(taskId))!;
        await device.Tasks.UpdateDetailsAsync(new(Op(), taskId, current.Version, title ?? current.Title, notes ?? current.Notes,
            null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
    }

    private static async Task<string> InviteAsync(SharingPerson owner, string spaceId)
    {
        var response = await owner.Desktop.Client.CallAsync(HttpMethod.Post, $"v2/spaces/{spaceId}/invites",
            JsonSerializer.Serialize(new { role = 2, maxUses = 10 }), CancellationToken.None);
        if (!response.Ok)
        {
            throw new InvalidOperationException($"Invite failed {response.Status}: {response.Body}");
        }

        using var json = JsonDocument.Parse(response.Body);
        return json.RootElement.GetProperty("link").GetString()!;
    }

    /// <summary>A creates "Team", adds a list with a task, invites B, and B joins with the pasted link.</summary>
    private static async Task<(string Space, string List, string Task)> ShareAsync(SharingWorld world, SharingPerson a, SharingPerson b)
    {
        var space = await a.Spaces().CreateAsync("Team");
        var list = await CreateListAsync(a.Desktop, space.SpaceId, "Launch");
        var task = await CaptureAsync(a.Desktop, list, "Write the plan");
        await world.SettleAsync();
        var joined = await b.Spaces().JoinWithLinkAsync(await InviteAsync(a, space.SpaceId));
        await Assert.That(joined.SpaceId).IsEqualTo(space.SpaceId);
        await Assert.That(joined.Role).IsEqualTo(2);
        await world.SettleAsync();
        return (space.SpaceId, list, task);
    }

    [Test]
    public async Task SharedPull_ReportsTheAuthenticatedChangeActor()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("actor-owner@example.com", "Ada");
        var b = await world.AddPersonAsync("actor-editor@example.com", "Bo");
        var (space, _, task) = await ShareAsync(world, a, b);
        await EditAsync(b.Desktop, task, notes: "A note by Bo");
        await world.SettleAsync();
        var response = await a.Desktop.Client.CallAsync(HttpMethod.Get, $"v2/sync/pull?stream={space}&after=0&limit=1000", null, CancellationToken.None);
        await Assert.That(response.Ok).IsTrue();
        using var page = JsonDocument.Parse(response.Body);
        var changes = page.RootElement.GetProperty("changes").EnumerateArray()
            .Where(row => row.GetProperty("op").GetString() == "upsert")
            .Select(Chaos.Sync.SyncJson.ParseChange).ToList();
        var edit = changes.Single(change => change.Kind == "task" && change.EntityKey == task
            && change.Groups.TryGetValue("notes", out var group) && Equals(group.Values.Values["Notes"], "A note by Bo"));
        await Assert.That(edit.ActorId).IsEqualTo(b.ProfileId.ToString("D"));
    }

    [Test]
    public async Task SharedComment_MentionsOnlyTheNamedMemberOnce()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("mention-a@example.com", "Ada");
        var b = await world.AddPersonAsync("mention-b@example.com", "Bo");
        var c = await world.AddPersonAsync("mention-c@example.com", "Cy");
        var (space, _, task) = await ShareAsync(world, a, b);
        await c.Spaces().JoinWithLinkAsync(await InviteAsync(a, space));
        await world.SettleAsync();
        var diary = new DiaryCommands(a.Desktop.Store, a.Desktop.Ids, a.Desktop.Clock);
        var mention = $"[@Bo](chaos:member/{b.ProfileId:D})";
        var entry = (await diary.AddAsync(new(Op(), task, $"Please review {mention}", null))).Entry;
        await world.SettleAsync();
        var bDeriver = new CollaborationNoticeDeriver(b.Desktop.Store, b.Desktop.Clock);
        await Assert.That(await bDeriver.DeriveAsync()).IsEqualTo(1);
        await Assert.That(await new CollaborationNoticeDeriver(a.Desktop.Store, a.Desktop.Clock).DeriveAsync()).IsEqualTo(0);
        await Assert.That(await new CollaborationNoticeDeriver(c.Desktop.Store, c.Desktop.Clock).DeriveAsync()).IsEqualTo(0);
        var due = await new CollaborationInbox(b.Desktop.Store, b.Desktop.Clock).DueAsync();
        await Assert.That(due.Count).IsEqualTo(1);
        await Assert.That(due[0].TaskId).IsEqualTo(task);
        await diary.EditAsync(new(Op(), entry.Id, entry.Version, $"Please review {mention} tomorrow"));
        await world.SettleAsync();
        await Assert.That(await bDeriver.DeriveAsync()).IsEqualTo(0);
        await Assert.That((await new CollaborationInbox(b.Desktop.Store, b.Desktop.Clock).ListAsync()).Count).IsEqualTo(1);
        var current = (await a.Desktop.Tasks.GetAsync(task))!;
        await a.Desktop.Tasks.ChangeAssigneeAsync(new(Op(), task, current.Version, b.ProfileId.ToString("D"), true));
        await world.SettleAsync();
        await Assert.That(await bDeriver.DeriveAsync()).IsEqualTo(1);
        await Assert.That((await new CollaborationInbox(b.Desktop.Store, b.Desktop.Clock).ListAsync()).Any(n => n.Kind == "assigned" && n.TaskId == task)).IsTrue();
    }

    [Test]
    public async Task OwnerDeletesAnotherMembersComment_LeavesPlaceholderOnBothDevices()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var owner = await world.AddPersonAsync("remove-owner@example.com", "Ada");
        var author = await world.AddPersonAsync("remove-author@example.com", "Bo");
        var (_, _, task) = await ShareAsync(world, owner, author);
        var authorDiary = new DiaryCommands(author.Desktop.Store, author.Desktop.Ids, author.Desktop.Clock);
        var entry = (await authorDiary.AddAsync(new(Op(), task, "Original comment", null))).Entry;
        await world.SettleAsync();
        var ownerDiary = new DiaryCommands(owner.Desktop.Store, owner.Desktop.Ids, owner.Desktop.Clock);
        var ownerEntry = (await ownerDiary.ListAsync(task, false)).Single(e => e.Id == entry.Id);
        await ownerDiary.DeleteAsync(new(Op(), entry.Id, ownerEntry.Version));
        await world.SettleAsync();
        foreach (var diary in new[] { ownerDiary, authorDiary })
        {
            var placeholder = (await diary.TimelineAsync(task)).Single(item => item.Diary?.Id == entry.Id).Diary!;
            await Assert.That(placeholder.DeletedAtUtc).IsNotNull();
            await Assert.That(placeholder.RemovedByOwner).IsTrue();
        }
    }

    [Test]
    public async Task ViewerDiaryAttachment_UploadsFromDesktopAndReachesOwner()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("diary-file-owner@example.com", "Ada");
        var b = await world.AddPersonAsync("diary-file-viewer@example.com", "Bo");
        var (space, _, task) = await ShareAsync(world, a, b);
        var role = await a.Desktop.Client.CallAsync(HttpMethod.Patch, $"v2/spaces/{space}/members/{b.ProfileId:D}",
            JsonSerializer.Serialize(new { role = 3 }), CancellationToken.None);
        await Assert.That(role.Ok).IsTrue();
        b.Desktop.Engine.RequestDiscovery();
        await world.SettleAsync();
        var diary = (await new DiaryCommands(b.Desktop.Store, b.Desktop.Ids, b.Desktop.Clock).AddAsync(new(Op(), task, "An update", null))).Entry;
        var aFiles = AttachmentFileStoreFactory.Create(a.Desktop.Storage.Options);
        var bFiles = AttachmentFileStoreFactory.Create(b.Desktop.Storage.Options);
        using var aTransfer = new AttachmentTransfer(a.Desktop.Store, aFiles, a.Desktop.Client, a.Desktop.Clock);
        using var bTransfer = new AttachmentTransfer(b.Desktop.Store, bFiles, b.Desktop.Client, b.Desktop.Clock);
        var commands = new AttachmentCommands(b.Desktop.Store, bFiles, b.Desktop.Ids, b.Desktop.Clock,
            new DirectoryUploadStaging(b.Desktop.Storage.Options.UploadsDirectory), availability: bTransfer);
        var bytes = System.Text.Encoding.UTF8.GetBytes("Viewer's attached note");
        var upload = await commands.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Diary, diary.Id, "note.txt", bytes.Length));
        await commands.ChunkAsync(upload.UploadId, 0, bytes);
        var attachment = (await commands.CommitAsync(new CommitAttachmentCommand(Op(), upload.UploadId))).Attachment;

        await world.SettleAsync();
        await Assert.That((await bTransfer.RunOnceAsync(CancellationToken.None)).Uploaded).IsEqualTo(1);
        await world.SettleAsync();
        await Assert.That((await aTransfer.RunOnceAsync(CancellationToken.None)).Downloaded).IsEqualTo(1);
        await using var body = await aFiles.OpenReadAsync(attachment.Sha256, 0, CancellationToken.None);
        using var copy = new MemoryStream();
        await body!.CopyToAsync(copy);
        await Assert.That(copy.ToArray().AsSpan().SequenceEqual(bytes)).IsTrue();
    }

    [Test]
    public async Task SharedAttachment_UploadedByOneDesktop_DownloadsOnAnotherAccount()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("attachment-a@example.com", "Ada");
        var b = await world.AddPersonAsync("attachment-b@example.com", "Bo");
        var (_, _, task) = await ShareAsync(world, a, b);
        var aFiles = AttachmentFileStoreFactory.Create(a.Desktop.Storage.Options);
        var bFiles = AttachmentFileStoreFactory.Create(b.Desktop.Storage.Options);
        using var aTransfer = new AttachmentTransfer(a.Desktop.Store, aFiles, a.Desktop.Client, a.Desktop.Clock);
        using var bTransfer = new AttachmentTransfer(b.Desktop.Store, bFiles, b.Desktop.Client, b.Desktop.Clock);
        var commands = new AttachmentCommands(a.Desktop.Store, aFiles, a.Desktop.Ids, a.Desktop.Clock,
            new DirectoryUploadStaging(a.Desktop.Storage.Options.UploadsDirectory), availability: aTransfer);
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEklEQVR4nGPQCFigEbCAAUIBABzOBGFvelvCAAAAAElFTkSuQmCC");
        var upload = await commands.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Task, task, "image.png", bytes.Length));
        await commands.ChunkAsync(upload.UploadId, 0, bytes);
        var attachment = (await commands.CommitAsync(new CommitAttachmentCommand(Op(), upload.UploadId))).Attachment;
        await Assert.That(attachment.MediaType).IsEqualTo("image/png");

        await world.SettleAsync();
        var sent = await aTransfer.RunOnceAsync(CancellationToken.None);
        await Assert.That(sent.Uploaded).IsEqualTo(1);
        await world.SettleAsync();
        var received = await bTransfer.RunOnceAsync(CancellationToken.None);
        await Assert.That(received.Downloaded).IsEqualTo(1);
        await using var body = await bFiles.OpenReadAsync(attachment.Sha256, 0, CancellationToken.None);
        using var copy = new MemoryStream();
        await body!.CopyToAsync(copy);
        await Assert.That(copy.ToArray().AsSpan().SequenceEqual(bytes)).IsTrue();
    }

    [Test]
    public async Task A_creates_a_space_invites_B_and_B_gets_the_space_and_its_lists()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("a@example.com", "Ada");
        var b = await world.AddPersonAsync("b@example.com", "Bo");
        var (space, list, task) = await ShareAsync(world, a, b);

        await Assert.That(await b.Desktop.TextAsync($"SELECT Name FROM Spaces WHERE Id = '{space}' AND Kind = 2;")).IsEqualTo("Team");
        await Assert.That(await b.Desktop.TextAsync($"SELECT Name FROM Containers WHERE Id = '{list}' AND SpaceId = '{space}';")).IsEqualTo("Launch");
        await Assert.That(await b.Desktop.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{task}';")).IsEqualTo("Write the plan");
        await Assert.That(await b.Desktop.CountAsync($"SELECT count(*) FROM SyncStreams WHERE StreamId = '{space}' AND State = 'active';")).IsEqualTo(1L);

        // The sidebar and the people directory see both members, with names, on both desktops.
        var bSpaces = await new SpaceQueries(b.Desktop.Store).ListAsync();
        var shared = bSpaces.Single(s => s.SpaceId == space);
        await Assert.That(shared.Role).IsEqualTo(2);
        await Assert.That(shared.MemberCount).IsEqualTo(2);
        var aMembers = await new SpaceQueries(a.Desktop.Store).MembersAsync(space);
        await Assert.That(aMembers.Select(m => m.DisplayName).Order().ToArray()).IsEquivalentTo(new[] { "Ada", "Bo" });
        await Assert.That(aMembers.Single(m => m.IsMe).DisplayName).IsEqualTo("Ada");
        // B's Personal data never reached A.
        await Assert.That(await a.Desktop.CountAsync($"SELECT count(*) FROM Containers WHERE SpaceId <> '{space}' AND SpaceId <> (SELECT Id FROM Spaces WHERE Kind = 1 AND OwnerProfileId = '{a.ProfileId}');")).IsEqualTo(0L);
    }

    [Test]
    public async Task An_edit_by_B_appears_on_A_within_three_seconds()
    {
        var realtime = new SyncEngineOptions { Jitter = () => 0, PollInterval = TimeSpan.FromHours(1), Debounce = TimeSpan.FromMilliseconds(100) };
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("a@example.com", "Ada", options: realtime);
        var b = await world.AddPersonAsync("b@example.com", "Bo", options: realtime);
        var (_, _, task) = await ShareAsync(world, a, b);
        a.Desktop.Engine.Start();
        b.Desktop.Engine.Start();
        var engines = new[] { a.Desktop.Engine, b.Desktop.Engine };
        await Assert.That(await SyncRealtimeTests.WaitForRuntimeAsync(engines,
            () => a.Desktop.Engine.Runtime.Streaming && b.Desktop.Engine.Runtime.Streaming, TimeSpan.FromSeconds(10))).IsTrue();
        await Assert.That(await SyncRealtimeTests.WaitForRuntimeAsync(engines,
            () => a.Desktop.Engine.Runtime.LastSuccessAt is not null && b.Desktop.Engine.Runtime.LastSuccessAt is not null, TimeSpan.FromSeconds(10))).IsTrue();
        var watch = Stopwatch.StartNew();
        await EditAsync(b.Desktop, task, title: "Plan written by Bo");
        var arrived = await SyncRealtimeTests.WaitForCycleAsync(a.Desktop.Engine,
            async () => await a.Desktop.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{task}';") == "Plan written by Bo", TimeSpan.FromSeconds(3));

        await Assert.That(arrived).IsTrue();
        await Assert.That(watch.Elapsed < TimeSpan.FromSeconds(3)).IsTrue();
    }

    [Test]
    public async Task Offline_edits_on_both_sides_merge_by_field_group_with_history()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("a@example.com", "Ada");
        var b = await world.AddPersonAsync("b@example.com", "Bo");
        var (_, _, task) = await ShareAsync(world, a, b);
        a.Desktop.Network.Offline = true;
        b.Desktop.Network.Offline = true;
        await EditAsync(a.Desktop, task, title: "Title from Ada");
        b.Desktop.Clock.Now = b.Desktop.Clock.Now.AddMinutes(1);
        await EditAsync(b.Desktop, task, notes: "Notes from Bo");
        a.Desktop.Network.Offline = false;
        b.Desktop.Network.Offline = false;
        await world.SettleAsync();

        foreach (var device in new[] { a.Desktop, b.Desktop })
        {
            await Assert.That(await device.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{task}';")).IsEqualTo("Title from Ada");
            await Assert.That(await device.TextAsync($"SELECT Notes FROM Tasks WHERE Id = '{task}';")).IsEqualTo("Notes from Bo");
        }

        // Each person's change is in the shared history under their own name.
        var actors = await a.Desktop.RowsAsync($"SELECT DISTINCT Actor FROM TaskHistory WHERE TaskId = '{task}' AND EventKind <> 'sync.merged' ORDER BY Actor;");
        await Assert.That(actors).Contains($"person:{a.ProfileId}");
        await Assert.That(actors).Contains($"person:{b.ProfileId}");
        await Assert.That((await b.Desktop.RowsAsync($"SELECT Id FROM TaskHistory WHERE TaskId = '{task}' AND EventKind <> 'sync.merged' ORDER BY Id;")))
            .IsEquivalentTo(await a.Desktop.RowsAsync($"SELECT Id FROM TaskHistory WHERE TaskId = '{task}' AND EventKind <> 'sync.merged' ORDER BY Id;"));
    }

    [Test]
    public async Task B_is_removed_and_evicts_counting_unsent_changes_and_B_My_Day_items_are_tombstoned_on_all_of_B_devices()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("a@example.com", "Ada");
        var b = await world.AddPersonAsync("b@example.com", "Bo", devices: 2);
        var (space, list, task) = await ShareAsync(world, a, b);
        // B's other desktop learns of the join from the service's membership event; these desktops are driven by hand,
        // without their event streams, so the test delivers it.
        b.Devices[1].Engine.RequestDiscovery();
        await world.SettleAsync();
        await Assert.That(await b.Devices[1].CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{task}';")).IsEqualTo(1L);

        // B puts the shared task in My Day; it syncs to B's other desktop and never to A.
        var myDay = new MyDayCommands(b.Desktop.Store, b.Desktop.Clock);
        var today = (await myDay.ClockAsync()).Today;
        await myDay.AddAsync(new DayItemCommand(Op(), today, task));
        await world.SettleAsync();
        var dayItem = $"SELECT count(*) FROM DayPlanItems WHERE TaskId = '{task}' AND RemovedAtUtc IS NULL;";
        await Assert.That(await b.Devices[1].CountAsync(dayItem)).IsEqualTo(1L);
        await Assert.That(await a.Desktop.CountAsync(dayItem)).IsEqualTo(0L);

        // B edits while offline, then A removes B.
        b.Desktop.Network.Offline = true;
        await EditAsync(b.Desktop, task, title: "Never uploaded");
        var removed = await a.Desktop.Client.CallAsync(HttpMethod.Delete, $"v2/spaces/{space}/members/{b.ProfileId:D}", null, CancellationToken.None);
        await Assert.That(removed.Ok).IsTrue();
        b.Desktop.Network.Offline = false;
        b.Devices[1].Engine.RequestDiscovery(); // the membership event, as above
        await world.SettleAsync();

        foreach (var device in b.Devices)
        {
            await Assert.That(await device.CountAsync($"SELECT count(*) FROM Spaces WHERE Id = '{space}';")).IsEqualTo(0L);
            await Assert.That(await device.CountAsync($"SELECT count(*) FROM Containers WHERE Id = '{list}';")).IsEqualTo(0L);
            await Assert.That(await device.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{task}';")).IsEqualTo(0L);
            await Assert.That(await device.CountAsync($"SELECT count(*) FROM SyncStreams WHERE StreamId = '{space}';")).IsEqualTo(0L);
            // The server tombstoned the overlay in B's account stream, so every desktop of B converges.
            await Assert.That(await device.CountAsync(dayItem)).IsEqualTo(0L);
        }

        var attention = await b.Desktop.TextAsync("SELECT Message FROM SyncAttention WHERE Category = 'sync.evicted';");
        await Assert.That(attention).StartsWith("You no longer have access to Team.");
        await Assert.That(attention).Contains("discarded");
        await Assert.That(await b.Devices[1].CountAsync("SELECT count(*) FROM SyncAttention WHERE Category = 'sync.evicted';")).IsEqualTo(0L);

        // A is unaffected and still sees B's departure under B's name.
        await Assert.That(await a.Desktop.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{task}';")).IsEqualTo("Write the plan");
        var people = await new Chaos.Application.People.PeopleQueries(a.Desktop.Store).ListAsync(space);
        var bo = people.Single(p => p.ActorId == $"person:{b.ProfileId:D}");
        await Assert.That(bo.DisplayName).IsEqualTo("Bo");
        await Assert.That(bo.Former).IsTrue();
    }

    [Test]
    public async Task A_moves_a_Personal_list_in_and_B_sees_it_while_A_My_Day_stays()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("a@example.com", "Ada");
        var b = await world.AddPersonAsync("b@example.com", "Bo");
        var (space, _, _) = await ShareAsync(world, a, b);
        var personalSpace = await a.Desktop.TextAsync("SELECT Id FROM Spaces WHERE Kind = 1;");
        var list = await CreateListAsync(a.Desktop, personalSpace!, "Garden");
        var task = await CaptureAsync(a.Desktop, list, "Plant bulbs");
        var myDay = new MyDayCommands(a.Desktop.Store, a.Desktop.Clock);
        await myDay.AddAsync(new DayItemCommand(Op(), (await myDay.ClockAsync()).Today, task));
        await world.SettleAsync();
        await Assert.That(await b.Desktop.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{task}';")).IsEqualTo(0L);

        var moved = await a.Spaces().MoveAsync(list, space);
        await Assert.That(moved.EntityCount).IsGreaterThan(1);
        await world.SettleAsync();

        foreach (var device in new[] { a.Desktop, b.Desktop })
        {
            await Assert.That(await device.TextAsync($"SELECT SpaceId FROM Containers WHERE Id = '{list}';")).IsEqualTo(space);
            await Assert.That(await device.TextAsync($"SELECT SpaceId FROM Tasks WHERE Id = '{task}';")).IsEqualTo(space);
        }

        // A's My Day item is a personal overlay: it stays in A's account stream and still names the moved task.
        await Assert.That(await a.Desktop.CountAsync($"SELECT count(*) FROM DayPlanItems WHERE TaskId = '{task}' AND RemovedAtUtc IS NULL;")).IsEqualTo(1L);
        await Assert.That(await b.Desktop.CountAsync($"SELECT count(*) FROM DayPlanItems WHERE TaskId = '{task}';")).IsEqualTo(0L);

        // An edit by B on the moved task reaches A.
        await EditAsync(b.Desktop, task, title: "Plant tulip bulbs");
        await world.SettleAsync();
        await Assert.That(await a.Desktop.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{task}';")).IsEqualTo("Plant tulip bulbs");
    }

    [Test]
    public async Task Leaving_a_space_evicts_it_from_the_leaver_only()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("a@example.com", "Ada");
        var b = await world.AddPersonAsync("b@example.com", "Bo");
        var (space, _, task) = await ShareAsync(world, a, b);

        await b.Spaces().LeaveAsync(space);
        await world.SettleAsync();

        await Assert.That(await b.Desktop.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{task}';")).IsEqualTo(0L);
        await Assert.That(await b.Desktop.CountAsync($"SELECT count(*) FROM Spaces WHERE Id = '{space}';")).IsEqualTo(0L);
        await Assert.That(await a.Desktop.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{task}';")).IsEqualTo(1L);
        // No unsent changes, so no attention item.
        await Assert.That(await b.Desktop.CountAsync("SELECT count(*) FROM SyncAttention WHERE Category = 'sync.evicted';")).IsEqualTo(0L);
    }

    [Test]
    public async Task Creating_a_space_offline_says_to_connect_and_changes_nothing()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("a@example.com", "Ada");
        a.Desktop.Network.Offline = true;
        var error = await Assert.That(async () => await a.Spaces().CreateAsync("Team")).Throws<AppException>();
        await Assert.That(error!.Message).IsEqualTo("Connect to create a shared space.");
        await Assert.That(await a.Desktop.CountAsync("SELECT count(*) FROM Spaces WHERE Kind = 2;")).IsEqualTo(0L);
        await Assert.That(await a.Desktop.CountAsync("SELECT count(*) FROM SyncStreams WHERE Kind = 'space';")).IsEqualTo(0L);
    }

    [Test]
    public async Task A_protocol_1_desktop_on_the_same_account_stays_personal_only_and_is_told_to_update()
    {
        await using var world = await SharingWorld.StartAsync(postgres);
        var a = await world.AddPersonAsync("a@example.com", "Ada", devices: 2);
        var b = await world.AddPersonAsync("b@example.com", "Bo");
        var (space, _, _) = await ShareAsync(world, a, b);
        var personalSpace = await a.Desktop.TextAsync("SELECT Id FROM Spaces WHERE Kind = 1;");
        var list = await CreateListAsync(a.Desktop, personalSpace!, "Garden");
        var task = await CaptureAsync(a.Desktop, list, "Plant bulbs");
        await world.SettleAsync();
        var old = a.Devices[1];
        await Assert.That(await old.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{task}';")).IsEqualTo(1L);
        var cursor = await old.CountAsync("SELECT Cursor FROM SyncStreams WHERE Kind = 'account';");

        // From here A's second desktop behaves like the released build: protocol 1, account stream only.
        await a.Spaces().MoveAsync(list, space);
        var pulled = await old.Client.CallAsync(HttpMethod.Get, $"v1/sync/pull?after={cursor}&limit=1000", null, CancellationToken.None);
        await Assert.That(pulled.Ok).IsTrue();
        using var page = JsonDocument.Parse(pulled.Body);
        var changes = page.RootElement.GetProperty("changes").EnumerateArray().Select(c => c.GetRawText()).ToList();
        await Assert.That(changes.Count).IsGreaterThan(0);
        // Every change parses with the released build's parser: moved work arrives as purges, never as "moved".
        var parsed = changes.Select(Chaos.Sync.SyncJson.ParseChange).ToList();
        await Assert.That(parsed.Any(c => c.Kind == "task" && c.EntityKey == task && c.Op == Chaos.Sync.SyncOp.Purge)).IsTrue();
        await Assert.That(parsed.Any(c => c.Kind == "container" && c.EntityKey == list && c.Op == Chaos.Sync.SyncOp.Purge)).IsTrue();

        // A child purge after its parent's purge finds nothing left and is skipped; nothing is unreadable.
        await new SyncApplier(old.Store, old.Clock, old.Ids).ApplyAsync(changes);
        await Assert.That(await old.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{task}';")).IsEqualTo(0L);
        await Assert.That(await old.CountAsync($"SELECT count(*) FROM Containers WHERE Id = '{list}';")).IsEqualTo(0L);
        await Assert.That(await old.CountAsync("SELECT count(*) FROM SyncAttention WHERE Category = 'sync.unexpected';")).IsEqualTo(0L);

        // An edit to the moved work, made before that pull landed, is refused as too old: the released build shows "Update Chaos".
        var hlc = Chaos.Sync.Hlc.Create(old.Clock.Now.AddMinutes(5).ToUnixTimeMilliseconds(), 0, Guid.NewGuid()).Encode();
        var edit = JsonSerializer.Serialize(new
        {
            changes = new[]
            {
                new { changeId = Guid.NewGuid().ToString("D"), kind = "task", key = task, op = "upsert",
                    groups = new Dictionary<string, object> { ["title"] = new { hlc, values = new { Title = "Old desktop edit" } } } },
            },
        });
        var pushed = await old.Client.CallAsync(HttpMethod.Post, "v1/sync/push", edit, CancellationToken.None);
        await Assert.That(pushed.Ok).IsTrue();
        using var answer = JsonDocument.Parse(pushed.Body);
        await Assert.That(answer.RootElement.GetProperty("rejected")[0].GetProperty("reason").GetString()).IsEqualTo(Chaos.Sync.SyncRejection.SchemaTooOld);

        // The shared copy is untouched, and B still sees it.
        await world.SettleAsync();
        await Assert.That(await b.Desktop.TextAsync($"SELECT Title FROM Tasks WHERE Id = '{task}';")).IsEqualTo("Plant bulbs");
    }
}
