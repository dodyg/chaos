using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chaos.Server.Api;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chaos.Server.Tests.Sharing;

[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class SharingServerTests(PostgresServer postgres)
{
    private static string Now() => DateTimeOffset.UtcNow.ToString("o");

    [Test]
    public async Task Two_accounts_create_invite_accept_and_receive_space_generated_changes()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var alice = await world.SignInAsync("share-alice@example.com");
        var bob = await world.SignInAsync("share-bob@example.com");
        var space = await world.CreateSpaceAsync(alice);
        var token = await world.InviteLinkAsync(alice, space, SpaceMember.EditorRole);
        await world.JoinAsync(bob, token);

        var streams = await world.CallAsync(bob, HttpMethod.Get, "/v2/streams");
        await Assert.That(streams.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(streams.Body).Contains(space.ToString("D"));
        var snapshot = await world.CallAsync(bob, HttpMethod.Get, $"/v2/sync/snapshot?stream={space:D}&kind=space_member");
        await Assert.That(snapshot.Body).DoesNotContain("Former member");
        await Assert.That(snapshot.Body).Contains(bob.ProfileId.ToString("D"));
        await using var db = ServerDatabase.Open(server.ConnectionString);
        var rows = db.SyncChanges.Where(c => c.AccountId == space).OrderBy(c => c.Seq).ToList();
        await Assert.That(rows.Select(r => r.Kind).Take(2)).IsEquivalentTo(["space", "space_member"]);
        await Assert.That(rows.All(r => r.ActorId is not null)).IsTrue();
    }

    [Test]
    public async Task Canonical_profile_ids_are_unique_and_shared_members_cannot_be_hijacked()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("unique-owner@example.com");
        var unbound = await world.SignInAsync("unique-second@example.com", bind: false);
        var bind = await world.CallAsync(unbound, HttpMethod.Post, "/v1/account/bind", new
        {
            profileId = owner.ProfileId, personalSpaceId = unbound.PersonalSpaceId, inboxId = unbound.InboxId,
        }, version: 1, schema: 4);
        await Assert.That(bind.Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(bind.Body).Contains("profile-in-use");
        await using var db = ServerDatabase.Open(server.ConnectionString);
        await Assert.That(db.Accounts.Count(a => a.ProfileId == owner.ProfileId)).IsEqualTo(1);
    }

    [Test]
    public async Task Permission_matrix_is_enforced_by_raw_clients()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("matrix-owner@example.com");
        var editor = await world.SignInAsync("matrix-editor@example.com");
        var viewer = await world.SignInAsync("matrix-viewer@example.com");
        var outsider = await world.SignInAsync("matrix-outsider@example.com");
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(editor, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        await world.JoinAsync(viewer, await world.InviteLinkAsync(owner, space, SpaceMember.ViewerRole));

        var taskId = Guid.NewGuid().ToString("D");
        var createTask = SharingHarness.Change("task", taskId, "create",
            new { SpaceId = space.ToString("D"), CreatedAtUtc = Now() });
        await Assert.That((await world.PushAsync(owner, createTask)).Rejections()).IsEmpty();
        await Assert.That((await world.PushAsync(editor,
            SharingHarness.Change("task", Guid.NewGuid().ToString("D"), "create",
                new { SpaceId = space.ToString("D"), CreatedAtUtc = Now() }))).Rejections()).DoesNotContain(SyncRejection.Forbidden);
        await Assert.That((await world.PushAsync(viewer,
            SharingHarness.Change("task", Guid.NewGuid().ToString("D"), "create",
                new { SpaceId = space.ToString("D"), CreatedAtUtc = Now() }))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await world.PushAsync(outsider,
            SharingHarness.Change("task", Guid.NewGuid().ToString("D"), "create",
                new { SpaceId = space.ToString("D"), CreatedAtUtc = Now() }))).Rejections()).Contains(SyncRejection.NotMember);

        var commentId = Guid.NewGuid().ToString("D");
        await Assert.That((await world.PushAsync(viewer, SharingHarness.Change("diary_entry", commentId, "create",
            new { TaskId = taskId, AuthorProfileId = viewer.ProfileId.ToString("D"), EntryAtUtc = Now(), CreatedAtUtc = Now() })))
            .Rejections()).IsEmpty();
        await Assert.That((await world.PushAsync(editor, SharingHarness.Change("diary_entry", commentId, "entry",
            new { RawMarkdown = "forged", EditedAtUtc = Now() }))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await world.PushAsync(owner, SharingHarness.Change("diary_entry", commentId, "deleted",
            new { DeletedAtUtc = Now() }))).Rejections()).IsEmpty();

        var purge = SharingHarness.Purge("task", taskId);
        await Assert.That((await world.PushAsync(editor, purge)).Rejections())
            .Contains(SyncRejection.Forbidden);
        await Assert.That((await world.PushAsync(viewer, SharingHarness.Purge("task", taskId))).Rejections())
            .Contains(SyncRejection.Forbidden);
        await Assert.That((await world.PushAsync(owner, purge)).Rejections()).IsEmpty();
        await Assert.That((await world.PushAsync(owner, purge)).Rejections()).IsEmpty();
        await Assert.That((await world.PushAsync(owner, SharingHarness.Change("space", space.ToString("D"), "name",
            new { Name = "Renamed by owner" }))).Rejections()).IsEmpty();
        await Assert.That((await world.PushAsync(editor, SharingHarness.Change("space", space.ToString("D"), "name",
            new { Name = "Not allowed" }))).Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await world.PushAsync(owner, SharingHarness.Change("space_member", Guid.NewGuid().ToString("D"), "role",
            new { Role = 2L }))).Rejections()).Contains(SyncRejection.ServerOnly);

        await Assert.That((await world.CallAsync(editor, HttpMethod.Post, $"/v2/spaces/{space:D}/invites",
            new CreateInviteRequest(null, SpaceMember.ViewerRole, null, 1))).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await world.CallAsync(viewer, HttpMethod.Delete, $"/v2/spaces/{space:D}")).Status)
            .IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await world.CallAsync(outsider, HttpMethod.Get,
            $"/v2/sync/pull?stream={space:D}&after=0")).Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await world.CallAsync(outsider, HttpMethod.Get,
            $"/v2/sync/snapshot?stream={space:D}&kind=task")).Status).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Invite_page_hides_details_until_sign_in_and_accepts_with_antiforgery()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("page-owner@example.com");
        var member = await world.SignInAsync("page-member@example.com");
        var space = await world.CreateSpaceAsync(owner, "Browser-only invite");
        var link = await world.InviteLinkAsync(owner, space, SpaceMember.ViewerRole);
        var path = new Uri(link).PathAndQuery;
        using var browser = server.NewBrowser();
        var landing = await browser.GetStringAsync(path);
        await Assert.That(landing).DoesNotContain("Browser-only invite");
        await Assert.That(landing).DoesNotContain(owner.Email);

        var signIn = await browser.GetAsync("/signin?ReturnUrl=" + Uri.EscapeDataString(path));
        var signInPage = await signIn.Content.ReadAsStringAsync();
        var verify = await browser.PostAsync("/signin/email",
            DesktopFlow.Form(signInPage, ("email", member.Email), ("returnUrl", path)));
        var codePage = await verify.Content.ReadAsStringAsync();
        var signedIn = await browser.PostAsync("/signin/verify",
            DesktopFlow.Form(codePage, ("email", member.Email), ("code", server.Mail.LastCode(member.Email)), ("returnUrl", path)));
        var inviteResponse = await browser.GetAsync(signedIn.Headers.Location!.OriginalString);
        var invitePage = await inviteResponse.Content.ReadAsStringAsync();
        await Assert.That(invitePage).Contains("Browser-only invite");
        var joined = await browser.PostAsync(path, DesktopFlow.Form(invitePage));
        await Assert.That(joined.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await joined.Content.ReadAsStringAsync()).Contains("Open Chaos on your computer");
    }

    [Test]
    public async Task V2_reports_every_routing_rejection_and_v1_remains_personal()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var alice = await world.SignInAsync("reject-alice@example.com");
        var outsider = await world.SignInAsync("reject-outside@example.com");
        var space = await world.CreateSpaceAsync(alice);
        var missingParent = await world.PushAsync(alice, SharingHarness.Change("subtask", Guid.NewGuid().ToString("D"),
            "create", new { TaskId = Guid.NewGuid().ToString("D"), CreatedAtUtc = Now() }));
        await Assert.That(missingParent.Rejections()).Contains(SyncRejection.ParentUnknown);
        var invalidServer = await world.PushAsync(alice, SharingHarness.Change("space_member", Guid.NewGuid().ToString("D"),
            "role", new { Role = 2L }));
        await Assert.That(invalidServer.Rejections()).Contains(SyncRejection.ServerOnly);
        var oldClient = await world.PushV1Async(alice, SharingHarness.Change("task", Guid.NewGuid().ToString("D"), "create",
            new { SpaceId = space.ToString("D"), CreatedAtUtc = Now() }));
        await Assert.That(oldClient.Rejections()).Contains(SyncRejection.SchemaTooOld);
        var personal = await world.PushV1Async(alice, SharingHarness.Change("task", Guid.NewGuid().ToString("D"), "create",
            new { SpaceId = alice.PersonalSpaceId.ToString("D"), CreatedAtUtc = Now() }));
        await Assert.That(personal.Rejections()).IsEmpty();
        var wrongSpace = await world.PushAsync(outsider, SharingHarness.Change("task", Guid.NewGuid().ToString("D"), "create",
            new { SpaceId = space.ToString("D"), CreatedAtUtc = Now() }));
        await Assert.That(wrongSpace.Rejections()).Contains(SyncRejection.NotMember);
        var sharedTask = Guid.NewGuid().ToString("D");
        var shared = await world.PushAsync(alice, SharingHarness.Change("task", sharedTask, "create",
            new { SpaceId = space.ToString("D"), CreatedAtUtc = Now() }));
        await Assert.That(shared.Rejections()).IsEmpty();
        await Assert.That((await world.CallAsync(alice, HttpMethod.Get, "/v1/sync/pull?after=0",
            version: 1)).Body).DoesNotContain(sharedTask);
        await Assert.That((await world.CallAsync(alice, HttpMethod.Get, "/v1/sync/snapshot?kind=task",
            version: 1)).Body).DoesNotContain(sharedTask);
    }

    [Test]
    public async Task V2_REST_requests_require_protocol_two_and_space_id_is_immutable()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("protocol-two-owner@example.com");
        var space = await world.CreateSpaceAsync(owner);
        using var http = server.Factory.CreateClient();
        using var missing = new HttpRequestMessage(HttpMethod.Get, "/v2/streams");
        missing.Headers.Authorization = new("Bearer", owner.Token);
        missing.Headers.Add(SyncProtocol.DeviceHeader, owner.DeviceId.ToString());
        using var missingResponse = await http.SendAsync(missing);
        await Assert.That(missingResponse.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);

        var task = Guid.NewGuid().ToString("D");
        await Assert.That((await world.PushAsync(owner, SharingHarness.Change("task", task, "create",
            new { SpaceId = space.ToString("D"), CreatedAtUtc = Now() }))).Rejections()).IsEmpty();
        var movedSpace = Guid.NewGuid();
        var change = new RawChange(Guid.NewGuid().ToString("D"), "task", task, "upsert",
            new Dictionary<string, RawGroup>
            {
                ["create"] = new(Hlc.Create(DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), 0,
                    Guid.NewGuid()).Encode(), new
                    {
                        SpaceId = movedSpace.ToString("D"), CreatedAtUtc = Now(),
                    }),
            });
        await Assert.That((await world.PushAsync(owner, change)).Rejections()).Contains(SyncRejection.NotOwner);
    }

    [Test]
    public async Task Existing_diary_authorship_cannot_be_replaced_by_a_forged_create_group()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("author-owner@example.com");
        var author = await world.SignInAsync("author-member@example.com");
        var attacker = await world.SignInAsync("author-attacker@example.com");
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(author, await world.InviteLinkAsync(owner, space, SpaceMember.ViewerRole));
        await world.JoinAsync(attacker, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        var task = Guid.NewGuid().ToString("D");
        await world.PushAsync(owner, SharingHarness.Change("task", task, "create",
            new { SpaceId = space.ToString("D"), CreatedAtUtc = Now() }));
        var comment = Guid.NewGuid().ToString("D");
        await world.PushAsync(author, SharingHarness.Change("diary_entry", comment, "create",
            new { TaskId = task, AuthorProfileId = author.ProfileId.ToString("D"), EntryAtUtc = Now(), CreatedAtUtc = Now() }));
        var forged = new RawChange(Guid.NewGuid().ToString("D"), "diary_entry", comment, "upsert",
            new Dictionary<string, RawGroup>
            {
                ["create"] = new(Hlc.Create(DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), 0,
                    Guid.NewGuid()).Encode(), new
                    {
                        TaskId = task, AuthorProfileId = attacker.ProfileId.ToString("D"),
                        EntryAtUtc = Now(), CreatedAtUtc = Now(),
                    }),
                ["entry"] = new(Hlc.Create(DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds(), 1,
                    Guid.NewGuid()).Encode(), new { RawMarkdown = "forged", EditedAtUtc = Now() }),
            });
        await Assert.That((await world.PushAsync(attacker, forged)).Rejections()).Contains(SyncRejection.Forbidden);
    }

    [Test]
    public async Task Invites_expire_revoke_match_email_limit_uses_and_rate_limit_accepts()
    {
        await using var server = await TestServer.StartAsync(postgres, new()
        {
            ["Sharing:InviteAcceptsPerAccountPerHour"] = "2",
            ["Sharing:InviteAcceptsPerIpPerHour"] = "20",
        });
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("invite-owner@example.com");
        var bob = await world.SignInAsync("invite-bob@example.com");
        var recipient = await world.SignInAsync("invite-recipient@example.com");
        var wrong = await world.SignInAsync("invite-wrong@example.com");
        var space = await world.CreateSpaceAsync(owner);

        var expiringLink = await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole, server.Clock.GetUtcNow().AddMinutes(1), 1);
        server.Clock.Advance(TimeSpan.FromMinutes(2));
        var expired = await world.CallAsync(bob, HttpMethod.Post, "/v2/invites/accept",
            new AcceptInviteRequest(new Uri(expiringLink).Segments.Last()));
        await Assert.That(expired.Body).Contains("invite-expired");

        var revocable = await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole, uses: 1);
        using (var json = (await world.CallAsync(owner, HttpMethod.Get, $"/v2/spaces/{space:D}/invites")).Json())
        {
            var inviteId = json.RootElement.EnumerateArray().First(i => i.GetProperty("revokedAt").ValueKind == JsonValueKind.Null
                && i.GetProperty("maxUses").GetInt32() == 1).GetProperty("id").GetGuid();
            await Assert.That((await world.CallAsync(owner, HttpMethod.Delete,
                $"/v2/spaces/{space:D}/invites/{inviteId:D}")).Status).IsEqualTo(HttpStatusCode.OK);
        }
        var revoked = await world.CallAsync(bob, HttpMethod.Post, "/v2/invites/accept",
            new AcceptInviteRequest(new Uri(revocable).Segments.Last()));
        await Assert.That(revoked.Body).Contains("invite-revoked");

        var email = await world.CallAsync(owner, HttpMethod.Post, $"/v2/spaces/{space:D}/invites",
            new CreateInviteRequest(recipient.Email, SpaceMember.ViewerRole, null, null));
        await Assert.That(email.Status).IsEqualTo(HttpStatusCode.OK);
        var sent = server.Mail.Sent.Last(m => m.To == recipient.Email);
        var emailToken = Regex.Match(sent.Body, @"/invite/([A-Za-z0-9_-]+)").Groups[1].Value;
        await Assert.That(sent.Body).DoesNotContain(owner.Email);
        await Assert.That(sent.Body).Contains("Shared work");
        var mismatch = await world.CallAsync(wrong, HttpMethod.Post, "/v2/invites/accept", new AcceptInviteRequest(emailToken));
        await Assert.That(mismatch.Body).Contains("invite-email-mismatch");
        await Assert.That((await world.CallAsync(recipient, HttpMethod.Post, "/v2/invites/accept",
            new AcceptInviteRequest(emailToken))).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await world.CallAsync(recipient, HttpMethod.Post, "/v2/invites/accept",
            new AcceptInviteRequest(emailToken))).Body).Contains("invite-used");
        await Assert.That((await world.CallAsync(recipient, HttpMethod.Post, "/v2/invites/accept",
            new AcceptInviteRequest(emailToken))).Body).Contains("rate-limited");

        await using (var db = ServerDatabase.Open(server.ConnectionString))
        {
            var hashes = await db.SpaceInvites.Select(i => i.TokenHash).ToListAsync();
            await Assert.That(hashes.Any(hash => hash.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(emailToken)))).IsFalse();
        }
    }

    [Test]
    public async Task Invites_cover_idempotency_higher_role_rejoin_limits_email_limits_and_send_failure()
    {
        await using var server = await TestServer.StartAsync(postgres, new()
        {
            ["Sharing:MaxPeoplePerSpace"] = "2",
            ["Sharing:MaxSpacesPerAccount"] = "1",
            ["Sharing:MaxOpenInvitesPerSpace"] = "2",
            ["Sharing:MaxInviteEmailsPerAccountPerDay"] = "1",
            ["Sharing:InviteAcceptsPerAccountPerHour"] = "10",
            ["Sharing:InviteAcceptsPerIpPerHour"] = "20",
        });
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("invite-edge-owner@example.com");
        var first = await world.SignInAsync("invite-edge-first@example.com");
        var second = await world.SignInAsync("invite-edge-second@example.com");
        var spaceId = Guid.NewGuid();
        var firstCreate = await world.CallAsync(owner, HttpMethod.Post, "/v2/spaces",
            new CreateSpaceRequest(spaceId, "Idempotent", null, null));
        await Assert.That(firstCreate.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await world.CallAsync(owner, HttpMethod.Post, "/v2/spaces",
            new CreateSpaceRequest(spaceId, "Idempotent repeat", null, null))).Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await world.CallAsync(first, HttpMethod.Post, "/v2/spaces",
            new CreateSpaceRequest(spaceId, "Collision", null, null))).Status).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await world.CallAsync(owner, HttpMethod.Post, "/v2/spaces",
            new CreateSpaceRequest(Guid.NewGuid(), "Too many spaces", null, null))).Body).Contains("space-limit");

        await world.InviteLinkAsync(owner, spaceId, SpaceMember.ViewerRole, uses: 1);
        await world.InviteLinkAsync(owner, spaceId, SpaceMember.ViewerRole, uses: 1);
        var inviteLimit = await world.CallAsync(owner, HttpMethod.Post, $"/v2/spaces/{spaceId:D}/invites",
            new CreateInviteRequest(null, SpaceMember.ViewerRole, null, 1));
        await Assert.That(inviteLimit.Body).Contains("invite-limit");
        using (var initial = (await world.CallAsync(owner, HttpMethod.Get, $"/v2/spaces/{spaceId:D}/invites")).Json())
        {
            foreach (var invite in initial.RootElement.EnumerateArray())
            {
                await world.CallAsync(owner, HttpMethod.Delete,
                    $"/v2/spaces/{spaceId:D}/invites/{invite.GetProperty("id").GetGuid():D}");
            }
        }

        var editorLink = await world.InviteLinkAsync(owner, spaceId, SpaceMember.EditorRole, uses: 2);
        await world.JoinAsync(first, editorLink);
        await world.JoinAsync(first, editorLink);
        await world.CallAsync(owner, HttpMethod.Patch, $"/v2/spaces/{spaceId:D}/members/{first.ProfileId:D}",
            new MemberRoleRequest(SpaceMember.ViewerRole));
        await world.JoinAsync(first, await world.InviteLinkAsync(owner, spaceId, SpaceMember.EditorRole, uses: 1));
        await using (var db = ServerDatabase.Open(server.ConnectionString))
        {
            await Assert.That(db.SpaceMembers.Single(m => m.SpaceId == spaceId && m.MemberId == first.ProfileId).Role)
                .IsEqualTo(SpaceMember.EditorRole);
            await Assert.That(db.AuditEvents.Count(a => a.AccountId == first.AccountId && a.Kind == AuditKinds.InviteUsed))
                .IsEqualTo(3);
        }

        await world.CallAsync(owner, HttpMethod.Delete,
            $"/v2/spaces/{spaceId:D}/members/{first.ProfileId:D}");
        var rejoin = await world.InviteLinkAsync(owner, spaceId, SpaceMember.ViewerRole, uses: 1);
        await world.JoinAsync(first, rejoin);
        await using (var db = ServerDatabase.Open(server.ConnectionString))
        {
            await Assert.That(db.SpaceMembers.Single(m => m.SpaceId == spaceId && m.MemberId == first.ProfileId).Role)
                .IsEqualTo(SpaceMember.ViewerRole);
        }
        await world.CallAsync(owner, HttpMethod.Delete,
            $"/v2/spaces/{spaceId:D}/members/{first.ProfileId:D}");
        await world.JoinAsync(second, await world.InviteLinkAsync(owner, spaceId, SpaceMember.EditorRole, uses: 1));
        var overCapacity = await world.CallAsync(first, HttpMethod.Post, "/v2/invites/accept",
            new AcceptInviteRequest(new Uri(await world.InviteLinkAsync(owner, spaceId, SpaceMember.ViewerRole, uses: 1)).Segments.Last()));
        await Assert.That(overCapacity.Body).Contains("member-limit");
        using (var active = (await world.CallAsync(owner, HttpMethod.Get, $"/v2/spaces/{spaceId:D}/invites")).Json())
        {
            foreach (var invite in active.RootElement.EnumerateArray()
                .Where(invite => invite.GetProperty("email").ValueKind == JsonValueKind.Null
                    && invite.GetProperty("uses").GetInt32() == 0
                    && invite.GetProperty("revokedAt").ValueKind == JsonValueKind.Null))
            {
                await world.CallAsync(owner, HttpMethod.Delete,
                    $"/v2/spaces/{spaceId:D}/invites/{invite.GetProperty("id").GetGuid():D}");
            }
        }

        var email = await world.CallAsync(owner, HttpMethod.Post, $"/v2/spaces/{spaceId:D}/invites",
            new CreateInviteRequest("one@example.com", SpaceMember.ViewerRole, null, null));
        await Assert.That(email.Status).IsEqualTo(HttpStatusCode.OK);
        var emailLimit = await world.CallAsync(owner, HttpMethod.Post, $"/v2/spaces/{spaceId:D}/invites",
            new CreateInviteRequest("two@example.com", SpaceMember.ViewerRole, null, null));
        await Assert.That(emailLimit.Body).Contains("invite-email-limit");

        using (var listed = (await world.CallAsync(owner, HttpMethod.Get, $"/v2/spaces/{spaceId:D}/invites")).Json())
        {
            foreach (var invite in listed.RootElement.EnumerateArray())
            {
                if (invite.GetProperty("revokedAt").ValueKind == JsonValueKind.Null)
                {
                    await world.CallAsync(owner, HttpMethod.Delete,
                        $"/v2/spaces/{spaceId:D}/invites/{invite.GetProperty("id").GetGuid():D}");
                }
            }
        }
        server.Clock.Advance(TimeSpan.FromHours(25));
        owner = await world.SignInAsync(owner.Email);
        server.Mail.Fail = true;
        var sendFailure = await world.CallAsync(owner, HttpMethod.Post, $"/v2/spaces/{spaceId:D}/invites",
            new CreateInviteRequest("failure@example.com", SpaceMember.ViewerRole, null, null));
        await Assert.That(sendFailure.Status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        server.Mail.Fail = false;
        string[] logs;
        lock (server.Logs.Lines)
        {
            logs = [.. server.Logs.Lines];
        }
        await Assert.That(logs.Any(line => line.Contains("failure@example.com", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Wrong_invite_tokens_count_against_the_ip_limit()
    {
        await using var server = await TestServer.StartAsync(postgres, new()
        {
            ["Sharing:InviteAcceptsPerAccountPerHour"] = "20",
            ["Sharing:InviteAcceptsPerIpPerHour"] = "2",
        });
        var world = new SharingHarness(server);
        var first = await world.SignInAsync("wrong-token-a@example.com");
        var second = await world.SignInAsync("wrong-token-b@example.com");
        await world.CallAsync(first, HttpMethod.Post, "/v2/invites/accept", new AcceptInviteRequest("wrong-one"));
        await world.CallAsync(second, HttpMethod.Post, "/v2/invites/accept", new AcceptInviteRequest("wrong-two"));
        var limited = await world.CallAsync(first, HttpMethod.Post, "/v2/invites/accept", new AcceptInviteRequest("wrong-three"));
        await Assert.That(limited.Status).IsEqualTo(HttpStatusCode.TooManyRequests);
        await Assert.That(limited.Body).Contains(SyncRejection.RateLimited);
    }

    [Test]
    public async Task Email_invites_accept_a_verified_linked_login_email()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("linked-invite-owner@example.com");
        var member = await world.SignInAsync("linked-invite-primary@example.com");
        var linkedEmail = "linked-invite-provider@example.com";
        await using (var db = ServerDatabase.Open(server.ConnectionString))
        {
            db.AccountLogins.Add(new AccountLogin
            {
                AccountId = member.AccountId,
                Provider = LoginProviders.Google,
                Subject = "linked-subject",
                Email = linkedEmail,
                LinkedAt = server.Clock.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }
        var space = await world.CreateSpaceAsync(owner);
        var response = await world.CallAsync(owner, HttpMethod.Post, $"/v2/spaces/{space:D}/invites",
            new CreateInviteRequest(linkedEmail, SpaceMember.ViewerRole, null, null));
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK);
        var token = Regex.Match(server.Mail.Sent.Last(m => m.To == linkedEmail).Body,
            @"/invite/([A-Za-z0-9_-]+)").Groups[1].Value;
        await Assert.That((await world.CallAsync(member, HttpMethod.Post, "/v2/invites/accept",
            new AcceptInviteRequest(token))).Status).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Role_changes_leave_and_last_owner_are_server_authoritative()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var alice = await world.SignInAsync("role-alice@example.com");
        var bob = await world.SignInAsync("role-bob@example.com");
        var space = await world.CreateSpaceAsync(alice);
        await world.JoinAsync(bob, await world.InviteLinkAsync(alice, space, SpaceMember.ViewerRole));

        await world.CallAsync(alice, HttpMethod.Patch, $"/v2/spaces/{space:D}/members/{bob.ProfileId:D}",
            new MemberRoleRequest(SpaceMember.OwnerRole));
        await Assert.That((await world.CallAsync(alice, HttpMethod.Post, $"/v2/spaces/{space:D}/leave")).Status)
            .IsEqualTo(HttpStatusCode.OK);
        var last = await world.CallAsync(bob, HttpMethod.Post, $"/v2/spaces/{space:D}/leave");
        await Assert.That(last.Body).Contains("last-owner");
        await using var db = ServerDatabase.Open(server.ConnectionString);
        var aliceMember = db.SpaceMembers.Single(m => m.SpaceId == space && m.MemberId == alice.ProfileId);
        await Assert.That(aliceMember.RemovedAt).IsNotNull();
        var bobMember = db.SpaceMembers.Single(m => m.SpaceId == space && m.MemberId == bob.ProfileId);
        await Assert.That(bobMember.Role).IsEqualTo(SpaceMember.OwnerRole);
        await world.CallAsync(bob, HttpMethod.Delete, $"/v2/spaces/{space:D}");
        var removedRestore = await world.CallAsync(alice, HttpMethod.Post, $"/v2/spaces/{space:D}/restore");
        await Assert.That(removedRestore.Status).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Concurrent_owner_changes_cannot_leave_a_space_without_an_owner()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var alice = await world.SignInAsync("concurrent-owner-a@example.com");
        var bob = await world.SignInAsync("concurrent-owner-b@example.com");
        var space = await world.CreateSpaceAsync(alice);
        await world.JoinAsync(bob, await world.InviteLinkAsync(alice, space, SpaceMember.EditorRole));
        await world.CallAsync(alice, HttpMethod.Patch, $"/v2/spaces/{space:D}/members/{bob.ProfileId:D}",
            new MemberRoleRequest(SpaceMember.OwnerRole));

        var demoteAlice = world.CallAsync(bob, HttpMethod.Patch,
            $"/v2/spaces/{space:D}/members/{alice.ProfileId:D}", new MemberRoleRequest(SpaceMember.EditorRole));
        var demoteBob = world.CallAsync(alice, HttpMethod.Patch,
            $"/v2/spaces/{space:D}/members/{bob.ProfileId:D}", new MemberRoleRequest(SpaceMember.EditorRole));
        var results = await Task.WhenAll(demoteAlice, demoteBob);
        await Assert.That(results.Count(r => r.Status == HttpStatusCode.OK)).IsEqualTo(1);
        await using var db = ServerDatabase.Open(server.ConnectionString);
        await Assert.That(db.SpaceMembers.Count(m => m.SpaceId == space && m.Role == SpaceMember.OwnerRole && m.RemovedAt == null))
            .IsEqualTo(1);

        var secondSpace = await world.CreateSpaceAsync(alice);
        await world.JoinAsync(bob, await world.InviteLinkAsync(alice, secondSpace, SpaceMember.EditorRole));
        await world.CallAsync(alice, HttpMethod.Patch, $"/v2/spaces/{secondSpace:D}/members/{bob.ProfileId:D}",
            new MemberRoleRequest(SpaceMember.OwnerRole));
        var removeAlice = world.CallAsync(bob, HttpMethod.Delete,
            $"/v2/spaces/{secondSpace:D}/members/{alice.ProfileId:D}");
        var removeBob = world.CallAsync(alice, HttpMethod.Delete,
            $"/v2/spaces/{secondSpace:D}/members/{bob.ProfileId:D}");
        var removals = await Task.WhenAll(removeAlice, removeBob);
        await Assert.That(removals.Count(r => r.Status == HttpStatusCode.OK)).IsEqualTo(1);
        await using var check = ServerDatabase.Open(server.ConnectionString);
        await Assert.That(check.SpaceMembers.Count(m =>
            m.SpaceId == secondSpace && m.Role == SpaceMember.OwnerRole && m.RemovedAt == null)).IsEqualTo(1);
    }

    [Test]
    public async Task Only_the_deleting_owner_can_restore_a_space()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var alice = await world.SignInAsync("restore-owner-a@example.com");
        var bob = await world.SignInAsync("restore-owner-b@example.com");
        var space = await world.CreateSpaceAsync(alice);
        await world.JoinAsync(bob, await world.InviteLinkAsync(alice, space, SpaceMember.EditorRole));
        await world.CallAsync(alice, HttpMethod.Patch, $"/v2/spaces/{space:D}/members/{bob.ProfileId:D}",
            new MemberRoleRequest(SpaceMember.OwnerRole));
        await world.CallAsync(bob, HttpMethod.Delete, $"/v2/spaces/{space:D}");
        await Assert.That((await world.CallAsync(alice, HttpMethod.Post, $"/v2/spaces/{space:D}/restore")).Status)
            .IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That((await world.CallAsync(bob, HttpMethod.Post, $"/v2/spaces/{space:D}/restore")).Status)
            .IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task Move_carries_closure_but_keeps_personal_tags_and_overlays()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var alice = await world.SignInAsync("move-alice@example.com");
        var bob = await world.SignInAsync("move-bob@example.com");
        var space = await world.CreateSpaceAsync(alice);
        await world.JoinAsync(bob, await world.InviteLinkAsync(alice, space, SpaceMember.ViewerRole));
        var container = Guid.NewGuid().ToString("D");
        var task = Guid.NewGuid().ToString("D");
        var subtask = Guid.NewGuid().ToString("D");
        var diary = Guid.NewGuid().ToString("D");
        var tag = Guid.NewGuid().ToString("D");
        var link = task + "|" + tag;
        var dayItem = Guid.NewGuid().ToString("D");
        var attachment = Guid.NewGuid().ToString("D");
        var section = Guid.NewGuid().ToString("D");
        var history = Guid.NewGuid().ToString("D");
        var board = Guid.NewGuid().ToString("D");
        var series = Guid.NewGuid().ToString("D");
        var occurrence = Guid.NewGuid().ToString("D");
        var issue = Guid.NewGuid().ToString("D");
        var changes = new[]
        {
            SharingHarness.Change("container", container, "create",
                new { SpaceId = alice.PersonalSpaceId.ToString("D"), Kind = 2L, IsInbox = 0L, CreatedAtUtc = Now() }),
            SharingHarness.Change("task", task, "create",
                new { SpaceId = alice.PersonalSpaceId.ToString("D"), CreatedAtUtc = Now() }),
            SharingHarness.Change("task", task, "place", new { ContainerId = container, SectionId = (string?)null }),
            SharingHarness.Change("subtask", subtask, "create", new { TaskId = task, CreatedAtUtc = Now() }),
            SharingHarness.Change("diary_entry", diary, "create",
                new { TaskId = task, AuthorProfileId = alice.ProfileId.ToString("D"), EntryAtUtc = Now(), CreatedAtUtc = Now() }),
            SharingHarness.Change("tag", tag, "create", new { OwnerProfileId = alice.ProfileId.ToString("D"), CreatedAtUtc = Now() }),
            SharingHarness.Change("task_tag", link, "deleted", new { DeletedAtUtc = (string?)null }),
            SharingHarness.Change("day_plan_item", dayItem, "create",
                new { DayPlanId = Guid.NewGuid().ToString("D"), TaskId = task, Kind = 0L, Origin = 0L, AddedAtUtc = Now(), CreatedAtUtc = Now() }),
            SharingHarness.Change("attachment", attachment, "create",
                new { SpaceId = alice.PersonalSpaceId.ToString("D"), OwnerKind = "task", OwnerId = task, CreatedAtUtc = Now() }),
            SharingHarness.Change("section", section, "create",
                new { ContainerId = container, Title = "Ready", CreatedAtUtc = Now() }),
            SharingHarness.Change("task_history", history, "record",
                new { TaskId = task, RecordedAtUtc = Now(), EventKind = "test" }),
            SharingHarness.Change("board_setting", board, "create",
                new { TargetKind = "container", TargetId = container, CreatedAtUtc = Now() }),
            SharingHarness.Change("recurrence_series", series, "create",
                new { SpaceId = alice.PersonalSpaceId.ToString("D"), ContainerId = container, CreatedAtUtc = Now() }),
            SharingHarness.Change("recurrence_occurrence", occurrence, "create",
                new { SeriesId = series, NominalKey = "2026-11-20", ScheduleKey = "2026-11-20", EffectiveLocal = "2026-11-20T00:00:00", CreatedAtUtc = Now() }),
            SharingHarness.Change("github_issue_link", issue, "state",
                new { TaskId = task, Number = 1L }),
        };
        var initial = await world.PushAsync(alice, changes);
        await Assert.That(initial.Rejections()).IsEmpty();
        var moveId = Guid.NewGuid();
        var viewerRefusal = await world.CallAsync(bob, HttpMethod.Post, $"/v2/spaces/{space:D}/moves",
            new MoveRequest(moveId, Guid.Parse(container)));
        await Assert.That(viewerRefusal.Status).IsEqualTo(HttpStatusCode.Forbidden);
        var moved = await world.CallAsync(alice, HttpMethod.Post, $"/v2/spaces/{space:D}/moves",
            new MoveRequest(moveId, Guid.Parse(container)));
        await Assert.That(moved.Status).IsEqualTo(HttpStatusCode.OK);
        var repeat = await world.CallAsync(alice, HttpMethod.Post, $"/v2/spaces/{space:D}/moves",
            new MoveRequest(moveId, Guid.Parse(container)));
        await Assert.That(repeat.Status).IsEqualTo(HttpStatusCode.OK);

        var inbox = Guid.NewGuid();
        await world.PushAsync(alice, SharingHarness.Change("container", inbox.ToString("D"), "create",
            new { SpaceId = alice.PersonalSpaceId.ToString("D"), Kind = 2L, IsInbox = 1L, CreatedAtUtc = Now() }));
        var inboxMove = await world.CallAsync(alice, HttpMethod.Post, $"/v2/spaces/{space:D}/moves",
            new MoveRequest(Guid.NewGuid(), inbox));
        await Assert.That(inboxMove.Body).Contains("inbox-cannot-move");
        await using var db = ServerDatabase.Open(server.ConnectionString);
        foreach (var key in new[] { container, task, subtask, diary, attachment, section, history, board, series, occurrence, issue })
        {
            await Assert.That(db.SyncEntities.Any(e => e.AccountId == alice.AccountId && e.EntityKey == key && e.MovedTo == space))
                .IsTrue();
            await Assert.That(db.SyncEntities.Any(e => e.AccountId == space && e.EntityKey == key)).IsTrue();
        }
        await Assert.That(db.SyncEntities.Any(e => e.AccountId == alice.AccountId && e.Kind == "task_tag" && e.EntityKey == link))
            .IsTrue();
        await Assert.That(db.SyncEntities.Any(e => e.AccountId == alice.AccountId && e.Kind == "day_plan_item" && e.EntityKey == dayItem))
            .IsTrue();
        await Assert.That(db.SyncChanges.Count(c => c.AccountId == alice.AccountId && c.ChangeId == moveId)).IsEqualTo(1);
        var late = await world.PushAsync(alice, SharingHarness.Change("task", task, "title", new { Title = "routes to moved entity" }));
        await Assert.That(late.Rejections()).IsEmpty();
        await using (var deleteTarget = ServerDatabase.Open(server.ConnectionString))
        {
            deleteTarget.SyncEntities.Remove(deleteTarget.SyncEntities.Single(e => e.AccountId == space && e.EntityKey == task));
            await deleteTarget.SaveChangesAsync();
        }
        late = await world.PushAsync(alice, new RawChange(Guid.NewGuid().ToString("D"), "task", task, "upsert",
            new Dictionary<string, RawGroup>
            {
                ["create"] = new(Hlc.Create(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0,
                    Guid.Parse("11111111-1111-1111-1111-111111111111")).Encode(),
                    new { SpaceId = alice.PersonalSpaceId.ToString("D"), CreatedAtUtc = Now() }),
            }));
        await Assert.That(late.Rejections()).Contains(SyncRejection.Moved);
    }

    [Test]
    public async Task Delete_restore_and_purge_evict_members_after_the_restore_window()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("delete-owner@example.com");
        var member = await world.SignInAsync("delete-member@example.com");
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(member, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        await Assert.That((await world.CallAsync(owner, HttpMethod.Delete, $"/v2/spaces/{space:D}")).Status)
            .IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await world.CallAsync(member, HttpMethod.Get, "/v2/streams")).Body).DoesNotContain(space.ToString("D"));
        await Assert.That((await world.CallAsync(owner, HttpMethod.Post, $"/v2/spaces/{space:D}/restore")).Status)
            .IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await world.CallAsync(member, HttpMethod.Get, "/v2/streams")).Body).Contains(space.ToString("D"));

        await world.CallAsync(owner, HttpMethod.Delete, $"/v2/spaces/{space:D}");
        server.Clock.Advance(TimeSpan.FromDays(31));
        await using (var scope = server.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChaosDbContext>();
            await Assert.That(await SpacePurgeService.PurgeDueAsync(db, server.Clock.GetUtcNow(), CancellationToken.None)).IsEqualTo(1);
        }
        await using (var db = ServerDatabase.Open(server.ConnectionString))
        {
            await Assert.That(await db.SyncStreams.AnyAsync(s => s.Id == space)).IsFalse();
            await Assert.That(await db.SpaceMembers.AnyAsync(m => m.SpaceId == space)).IsFalse();
            await Assert.That(await db.SpaceInvites.AnyAsync(i => i.SpaceId == space)).IsFalse();
        }
    }

    [Test]
    public async Task Removing_a_member_tombstones_only_their_space_overlays()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("overlay-owner@example.com");
        var member = await world.SignInAsync("overlay-member@example.com");
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(member, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        var task = Guid.NewGuid().ToString("D");
        var shared = await world.PushAsync(owner, SharingHarness.Change("task", task, "create",
            new { SpaceId = space.ToString("D"), CreatedAtUtc = Now() }));
        await Assert.That(shared.Rejections()).IsEmpty();
        var overlay = Guid.NewGuid().ToString("D");
        var put = await world.PushAsync(member, SharingHarness.Change("day_plan_item", overlay, "create",
            new { DayPlanId = Guid.NewGuid().ToString("D"), TaskId = task, Kind = 0L, Origin = 0L,
                AddedAtUtc = Now(), CreatedAtUtc = Now() }));
        await Assert.That(put.Rejections()).IsEmpty();
        await world.CallAsync(owner, HttpMethod.Delete, $"/v2/spaces/{space:D}/members/{member.ProfileId:D}");
        await using var db = ServerDatabase.Open(server.ConnectionString);
        var tombstone = await db.SyncChanges.Where(c => c.AccountId == member.AccountId && c.Kind == "day_plan_item"
            && c.EntityKey == overlay).OrderByDescending(c => c.Seq).FirstAsync();
        var groups = SyncJson.ReadGroups(tombstone.Groups);
        await Assert.That(groups["removed"].Values.Values["RemovedAtUtc"]).IsNotNull();
    }

    [Test]
    public async Task Membership_and_stream_events_arrive_without_polling_within_three_seconds()
    {
        await using var server = await TestServer.StartAsync(postgres, new() { ["Sync:EventKeepAlive"] = "00:01:00" });
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("events-owner@example.com");
        var member = await world.SignInAsync("events-member@example.com");
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(member, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        using var http = world.CreateCallerClient(member);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var response = await http.GetAsync($"/v2/sync/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token), Encoding.UTF8);
        string? line;
        do
        {
            line = await reader.ReadLineAsync(timeout.Token);
        }
        while (line is not null && !line.Contains("\"membership\"", StringComparison.Ordinal));
        await Assert.That(line).IsNotNull();

        await world.CallAsync(owner, HttpMethod.Patch, $"/v2/spaces/{space:D}/members/{member.ProfileId:D}",
            new MemberRoleRequest(SpaceMember.ViewerRole));
        do
        {
            line = await reader.ReadLineAsync(timeout.Token);
        }
        while (line is not null && !line.Contains("\"membership\"", StringComparison.Ordinal));
        await Assert.That(line).IsNotNull();
        await Assert.That(line!).Contains("\"membership\"");

        var task = SharingHarness.Change("task", Guid.NewGuid().ToString("D"), "create",
            new { SpaceId = space.ToString("D"), CreatedAtUtc = Now() });
        await Assert.That((await world.PushAsync(owner, task)).Rejections()).IsEmpty();
        do
        {
            line = await reader.ReadLineAsync(timeout.Token);
        }
        while (line is not null && !line.Contains($"\"stream\":\"{space:D}\"", StringComparison.Ordinal));
        await Assert.That(line).IsNotNull();
        await Assert.That(line!).Contains($"\"stream\":\"{space:D}\"");
    }

    [Test]
    public async Task PostgreSQL_notifications_expand_space_streams_to_member_accounts()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("notify-space-owner@example.com");
        var member = await world.SignInAsync("notify-space-member@example.com");
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(member, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        var heard = new TaskCompletionSource<(Guid Stream, long Seq)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var broadcaster = server.Factory.Services.GetRequiredService<SyncBroadcaster>();
        using var subscription = broadcaster.SubscribeStreams(member.AccountId,
            (stream, seq) =>
            {
                // Membership notifications can still be in flight; wait for the notification sent below.
                if (stream == space && seq == 77) heard.TrySetResult((stream, seq));
            });
        await using (var db = ServerDatabase.Open(server.ConnectionString))
        {
            var payload = SyncNotificationListener.Payload(space, 77);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_notify({SyncNotificationListener.Channel}, {payload})");
        }
        var result = await heard.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.That(result.Stream).IsEqualTo(space);
        await Assert.That(result.Seq).IsEqualTo(77);
    }

    [Test]
    public async Task Account_deletion_refuses_last_ownership_and_preserves_former_members()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("account-owner@example.com");
        var member = await world.SignInAsync("account-member@example.com");
        var space = await world.CreateSpaceAsync(owner, "Former work");
        await world.JoinAsync(member, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        await using var scope = server.Factory.Services.CreateAsyncScope();
        var deletion = scope.ServiceProvider.GetRequiredService<AccountDeletion>();
        var refused = await deletion.DeleteAsync(owner.AccountId, owner.DeviceId, CancellationToken.None);
        await Assert.That(refused.Deleted).IsFalse();
        await Assert.That(refused.TransferFirst).Contains("Former work");

        await world.CallAsync(owner, HttpMethod.Patch, $"/v2/spaces/{space:D}/members/{member.ProfileId:D}",
            new MemberRoleRequest(SpaceMember.OwnerRole));
        await world.CallAsync(owner, HttpMethod.Patch, $"/v2/spaces/{space:D}/members/me",
            new MemberProfileRequest(null, "octo-owner"));
        await world.CallAsync(member, HttpMethod.Delete, $"/v2/spaces/{space:D}/members/{owner.ProfileId:D}");
        var ownerDeleted = await deletion.DeleteAsync(owner.AccountId, owner.DeviceId, CancellationToken.None);
        await Assert.That(ownerDeleted.Deleted).IsTrue();
        var former = await scope.ServiceProvider.GetRequiredService<ChaosDbContext>().SpaceMembers
            .SingleAsync(m => m.SpaceId == space && m.MemberId == owner.ProfileId);
        await Assert.That(former.DisplayName).IsEqualTo("Former member");
        await Assert.That(former.AccountId).IsNull();
        await Assert.That(former.RemovedAt).IsNotNull();
        await Assert.That(former.GitHubLogin).IsNull();
    }

    [Test]
    public async Task Invite_and_sharing_logs_never_contain_names_emails_or_tokens()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("private-owner@example.com");
        var member = await world.SignInAsync("private-member@example.com");
        var space = await world.CreateSpaceAsync(owner, "Secret project title");
        var link = await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole);
        var token = new Uri(link).Segments.Last();
        await world.JoinAsync(member, link);
        string[] lines;
        lock (server.Logs.Lines)
        {
            lines = [.. server.Logs.Lines];
        }
        await Assert.That(lines.Any(line => line.Contains("Secret project title", StringComparison.Ordinal))).IsFalse();
        await Assert.That(lines.Any(line => line.Contains(owner.Email, StringComparison.Ordinal))).IsFalse();
        await Assert.That(lines.Any(line => line.Contains(member.Email, StringComparison.Ordinal))).IsFalse();
        await Assert.That(lines.Any(line => line.Contains(token, StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task Invite_links_use_the_configured_public_address_not_the_request_host()
    {
        await using var server = await TestServer.StartAsync(postgres, new Dictionary<string, string?>
        {
            ["Sharing:PublicBaseUrl"] = "https://chaos.example.test",
        });
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("public-url-owner@example.com");
        var space = await world.CreateSpaceAsync(owner);

        // The test client talks to http://localhost; the links must not come from that request.
        await Assert.That(await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole))
            .StartsWith("https://chaos.example.test/invite/");
        var email = await world.CallAsync(owner, HttpMethod.Post, $"/v2/spaces/{space:D}/invites",
            new CreateInviteRequest("public-url-guest@example.com", SpaceMember.ViewerRole, null, null));
        await Assert.That(email.Status).IsEqualTo(HttpStatusCode.OK);
        var sent = server.Mail.Sent.Last(m => m.To == "public-url-guest@example.com");
        await Assert.That(sent.Body).Contains("https://chaos.example.test/invite/");
        await Assert.That(sent.Body).DoesNotContain("localhost");

        var forged = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        forged.Request.Scheme = "https";
        forged.Request.Host = new Microsoft.AspNetCore.Http.HostString("attacker.example");
        var configured = new Chaos.Server.Auth.SharingOptions { PublicBaseUrl = "https://chaos.example.test" };
        var unset = new Chaos.Server.Auth.SharingOptions();
        await Assert.That(SpaceEndpoints.PublicBaseUrl(forged, configured, new HostEnvironment(Environments.Production)))
            .IsEqualTo("https://chaos.example.test");
        await Assert.That(SpaceEndpoints.PublicBaseUrl(forged, unset, new HostEnvironment(Environments.Production))).IsNull();
        await Assert.That(SpaceEndpoints.PublicBaseUrl(forged, unset, new HostEnvironment(Environments.Development)))
            .IsEqualTo("https://attacker.example");
        foreach (var bad in new[] { "chaos.example.test", "ftp://chaos.example.test", "https://user:pw@chaos.example.test",
            "https://chaos.example.test/?next=x" })
        {
            await Assert.That(Chaos.Server.Auth.SharingOptions.IsValidPublicBaseUrl(bad)).IsFalse();
        }
    }

    private sealed class HostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Chaos.Server";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Test]
    public async Task Shared_task_history_names_its_writer_and_cannot_be_rewritten()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("history-owner@example.com");
        var editor = await world.SignInAsync("history-editor@example.com");
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(editor, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        var task = Guid.NewGuid().ToString("D");
        await Assert.That((await world.PushAsync(owner, SharingHarness.Change("task", task, "create",
            new { SpaceId = space.ToString("D"), CreatedAtUtc = Now() }))).Rejections()).IsEmpty();

        object Record(string actor, string kind = "task.updated") => new
        {
            TaskId = task, OperationId = "op", EffectiveAtUtc = "2026-11-20T00:00:00Z", Actor = actor, EventKind = kind,
            ChangedFields = "Title", SnapshotBefore = "{}", SnapshotAfter = "{}", RecordedAtUtc = "2026-11-20T00:00:00Z",
        };
        RawChange History(string key, object record) => SharingHarness.Change("task_history", key, "record", record);

        // Another member's name, the legacy local actor and an unparsable actor are all refused.
        foreach (var forged in new[] { $"person:{owner.ProfileId:D}", "local", "sync", $"agent:{Guid.NewGuid():D}", "github:octocat" })
        {
            await Assert.That((await world.PushAsync(editor, History(Guid.NewGuid().ToString("D"), Record(forged))))
                .Rejections()).Contains(SyncRejection.Forbidden);
        }

        var own = Guid.NewGuid().ToString("D");
        var mine = $"person:{editor.ProfileId:D}";
        await Assert.That((await world.PushAsync(editor, History(own, Record(mine)))).Rejections()).IsEmpty();
        // An identical re-send is fine; changing a stored record is not, for its writer or for an owner.
        await Assert.That((await world.PushAsync(editor, History(own, Record(mine)))).Rejections()).IsEmpty();
        await Assert.That((await world.PushAsync(editor, History(own, Record(mine, "task.rewritten"))))
            .Rejections()).Contains(SyncRejection.Forbidden);
        await Assert.That((await world.PushAsync(owner, History(own, Record($"person:{owner.ProfileId:D}", "task.rewritten"))))
            .Rejections()).Contains(SyncRejection.Forbidden);

        // Only an owner, who manages the GitHub connection, records GitHub as the actor.
        await Assert.That((await world.PushAsync(owner, History(Guid.NewGuid().ToString("D"), Record("github:octocat"))))
            .Rejections()).IsEmpty();
        await Assert.That((await world.PushAsync(owner, History(Guid.NewGuid().ToString("D"), Record($"person:{owner.ProfileId:D}"))))
            .Rejections()).IsEmpty();

        // The editor's own Personal stream keeps its existing rules.
        var personalTask = Guid.NewGuid().ToString("D");
        await Assert.That((await world.PushAsync(editor,
            SharingHarness.Change("task", personalTask, "create", new { SpaceId = editor.PersonalSpaceId.ToString("D"), CreatedAtUtc = Now() }),
            SharingHarness.Change("task_history", Guid.NewGuid().ToString("D"), "record",
                new { TaskId = personalTask, Actor = "local", EventKind = "test", RecordedAtUtc = Now() }))).Rejections()).IsEmpty();
    }
}
