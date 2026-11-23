using System.Net;
using System.Text;
using Chaos.Server.Data;
using Chaos.Server.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Chaos.Server.Tests.Sharing;

[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class PresenceTests(PostgresServer postgres)
{
    private static async Task<string?> ReadUntilAsync(StreamReader reader, string marker, CancellationToken ct)
    {
        string? line;
        do
        {
            line = await reader.ReadLineAsync(ct);
        }
        while (line is not null && !line.Contains(marker, StringComparison.Ordinal));
        return line;
    }

    [Test]
    public async Task Presence_reaches_other_members_within_three_seconds_and_expires_after_45()
    {
        await using var server = await TestServer.StartAsync(postgres, new() { ["Sync:EventKeepAlive"] = "00:01:00" });
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("presence-owner@example.com");
        var member = await world.SignInAsync("presence-member@example.com");
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(member, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        var page = Guid.NewGuid().ToString("D");
        var presence = server.Factory.Services.GetRequiredService<PresenceService>();

        using var http = world.CreateCallerClient(member);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var response = await http.GetAsync("/v2/sync/events", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token), Encoding.UTF8);
        await Assert.That(await ReadUntilAsync(reader, "\"membership\"", timeout.Token)).IsNotNull();

        var report = await world.CallAsync(owner, HttpMethod.Post, "/v2/presence",
            new { spaceId = space, entityKind = "page", entityId = page, state = "editing" });
        await Assert.That(report.Status).IsEqualTo(HttpStatusCode.NoContent);
        var line = await ReadUntilAsync(reader, "\"presence\"", timeout.Token);
        await Assert.That(line).IsNotNull();
        await Assert.That(line!).Contains(owner.AccountId.ToString("D")).And.Contains(page).And.Contains("\"editing\":[");

        // Nothing renews it: still there at 44 seconds, gone after 45, and the other member is told.
        server.Clock.Advance(TimeSpan.FromSeconds(44));
        presence.Sweep();
        await Assert.That(presence.Members(new PresenceKey(space, "page", page))).Contains(owner.AccountId);
        server.Clock.Advance(TimeSpan.FromSeconds(2));
        presence.Sweep();
        await Assert.That(presence.Members(new PresenceKey(space, "page", page))).IsEmpty();
        var expired = await ReadUntilAsync(reader, "\"presence\"", timeout.Token);
        await Assert.That(expired!).Contains("\"members\":[]");
    }

    [Test]
    public async Task Leaving_clears_presence_at_once()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("presence-leave-owner@example.com");
        var member = await world.SignInAsync("presence-leave-member@example.com");
        var space = await world.CreateSpaceAsync(owner);
        await world.JoinAsync(member, await world.InviteLinkAsync(owner, space, SpaceMember.EditorRole));
        var note = Guid.NewGuid().ToString("D");
        var presence = server.Factory.Services.GetRequiredService<PresenceService>();
        await world.CallAsync(member, HttpMethod.Post, "/v2/presence", new { spaceId = space, entityKind = "pad", entityId = note, state = "viewing" });
        await Assert.That(presence.Members(new PresenceKey(space, "pad", note))).Contains(member.AccountId);
        var left = await world.CallAsync(member, HttpMethod.Delete, $"/v2/presence?spaceId={space:D}&entityKind=pad&entityId={note}");
        await Assert.That(left.Status).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(presence.Members(new PresenceKey(space, "pad", note))).IsEmpty();
    }

    [Test]
    public async Task Presence_is_never_recorded_for_a_personal_entity_and_needs_membership()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var world = new SharingHarness(server);
        var owner = await world.SignInAsync("presence-personal-owner@example.com");
        var stranger = await world.SignInAsync("presence-personal-stranger@example.com");
        var space = await world.CreateSpaceAsync(owner);
        var note = Guid.NewGuid().ToString("D");
        var presence = server.Factory.Services.GetRequiredService<PresenceService>();

        // Personal space: the account's own ID. Accepted, and nothing is kept or sent.
        var personal = await world.CallAsync(owner, HttpMethod.Post, "/v2/presence",
            new { spaceId = owner.AccountId, entityKind = "pad", entityId = note, state = "editing" });
        await Assert.That(personal.Status).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(presence.Members(new PresenceKey(owner.AccountId, "pad", note))).IsEmpty();

        var outsider = await world.CallAsync(stranger, HttpMethod.Post, "/v2/presence",
            new { spaceId = space, entityKind = "pad", entityId = note, state = "viewing" });
        await Assert.That(outsider.Status).IsEqualTo(HttpStatusCode.Forbidden);
        await Assert.That(presence.Members(new PresenceKey(space, "pad", note))).IsEmpty();

        var bad = await world.CallAsync(owner, HttpMethod.Post, "/v2/presence",
            new { spaceId = space, entityKind = "diary", entityId = note, state = "viewing" });
        await Assert.That(bad.Status).IsEqualTo(HttpStatusCode.BadRequest);
    }
}
