using Chaos.Application.Abstractions;
using Chaos.Application.Spaces;

namespace Chaos.Application.Tests;

public sealed class SpaceAdminCommandsTests
{
    private const string Space = "11111111-1111-4111-8111-111111111111";
    private const string Member = "22222222-2222-4222-8222-222222222222";

    [Test]
    public async Task Members_AreReadWithRolesAndNoEmail()
    {
        var service = new RecordingService(new ServiceResponse(200, $$"""[{"memberId":"{{Member}}","name":"Ana","role":2,"kind":1,"joinedAt":"2026-05-01T09:00:00+00:00","removedAt":null}]"""));
        var members = await Commands(service).MembersAsync(Space);
        await Assert.That(members.Count).IsEqualTo(1);
        await Assert.That(members[0].Name).IsEqualTo("Ana");
        await Assert.That(members[0].Role).IsEqualTo(2);
        await Assert.That(service.Calls[0]).IsEqualTo($"GET v2/spaces/{Space}/members");
    }

    [Test]
    public async Task LinkInvite_SendsRoleExpiryAndUses_AndReturnsTheLinkOnce()
    {
        var service = new RecordingService(new ServiceResponse(201,
            $$"""{"invite":{"id":"{{Member}}","email":null,"role":3,"createdAt":"2026-05-01T09:00:00+00:00","expiresAt":"2026-05-08T09:00:00+00:00","maxUses":10,"uses":0,"revokedAt":null},"link":"https://chaos.example/invite/abc"}"""));
        var created = await Commands(service).InviteAsync(Space, null, 3, 7, 10);
        await Assert.That(created.Link).IsEqualTo("https://chaos.example/invite/abc");
        await Assert.That(created.Invite.MaxUses).IsEqualTo(10);
        await Assert.That(service.Calls[0]).IsEqualTo($"POST v2/spaces/{Space}/invites");
        await Assert.That(service.Bodies[0]!).Contains("\"role\":3").And.Contains("\"maxUses\":10").And.Contains("\"expiresAt\":\"");
        await Assert.That(service.Bodies[0]!).DoesNotContain("email");
    }

    [Test]
    public async Task EmailInvite_SendsTheAddress_AndNoLinkComesBack()
    {
        var service = new RecordingService(new ServiceResponse(201,
            $$"""{"invite":{"id":"{{Member}}","email":"sam@example.com","role":2,"createdAt":"2026-05-01T09:00:00+00:00","expiresAt":null,"maxUses":1,"uses":0,"revokedAt":null},"link":null}"""));
        var created = await Commands(service).InviteAsync(Space, " sam@example.com ", 2, null, null);
        await Assert.That(created.Link).IsNull();
        await Assert.That(service.Bodies[0]!).Contains("\"email\":\"sam@example.com\"");
    }

    [Test]
    public async Task Invite_RejectsAnOwnerRoleAndABadAddress_WithoutCallingTheService()
    {
        var service = new RecordingService(new ServiceResponse(200, "{}"));
        var commands = Commands(service);
        await Assert.That(async () => await commands.InviteAsync(Space, null, 1, null, null)).Throws<AppException>();
        await Assert.That(async () => await commands.InviteAsync(Space, "not-an-address", 2, null, null)).Throws<AppException>();
        await Assert.That(service.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Offline_SaysConnect()
    {
        var service = new RecordingService(null);
        var error = await Assert.That(async () => await Commands(service).InviteAsync(Space, null, 2, null, null)).Throws<AppException>();
        await Assert.That(error!.Message).IsEqualTo("Connect to invite people.");
    }

    [Test]
    public async Task ChangeRole_AndRemove_UseTheMemberEndpoints_ThenSync()
    {
        var service = new RecordingService(new ServiceResponse(200, "{}"));
        var streams = new CountingStreams();
        var commands = new SpaceCommands(null!, service, streams, new FixedIds());
        await commands.ChangeRoleAsync(Space, Member, 3);
        await commands.RemoveMemberAsync(Space, Member);
        await Assert.That(service.Calls).IsEquivalentTo([$"PATCH v2/spaces/{Space}/members/{Member}", $"DELETE v2/spaces/{Space}/members/{Member}"]);
        await Assert.That(service.Bodies[0]!).IsEqualTo("{\"role\":3}");
        await Assert.That(streams.Syncs).IsEqualTo(2);
        await Assert.That(async () => await commands.ChangeRoleAsync(Space, Member, 9)).Throws<AppException>();
    }

    [Test]
    public async Task ServiceRefusal_ShowsItsDetail()
    {
        var service = new RecordingService(new ServiceResponse(409, """{"detail":"A space needs at least one owner."}"""));
        var error = await Assert.That(async () => await Commands(service).RemoveMemberAsync(Space, Member)).Throws<AppException>();
        await Assert.That(error!.Message).IsEqualTo("A space needs at least one owner.");
    }

    private static SpaceCommands Commands(RecordingService service) => new(null!, service, new CountingStreams(), new FixedIds());

    private sealed class FixedIds : IIdGenerator
    {
        public Guid NewId() => Guid.Parse("33333333-3333-4333-8333-333333333333");
    }

    private sealed class CountingStreams : ISpaceStreams
    {
        public int Syncs { get; private set; }
        public Task JoinAsync(Guid spaceId, int role, CancellationToken ct) => Task.CompletedTask;
        public Task LeaveAsync(Guid spaceId, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> SyncAsync(CancellationToken ct)
        {
            Syncs++;
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>Answers every call with one canned response (null: the service cannot be reached) and records what was asked.</summary>
    private sealed class RecordingService(ServiceResponse? response) : IChaosServiceClient
    {
        public List<string> Calls { get; } = [];
        public List<string?> Bodies { get; } = [];

        public Task<ServiceResponse> CallAsync(HttpMethod method, string path, string? json, CancellationToken ct)
        {
            if (response is null)
            {
                throw new ChaosServiceException(ServiceFailure.Offline, "offline");
            }

            Calls.Add($"{method.Method} {path}");
            Bodies.Add(json);
            return Task.FromResult(response);
        }

        public bool IsConfigured => true;
        public Task<ServiceSession> GetSessionAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceAccount> SignInAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceAccount> ReauthenticateAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task SignOutAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Bound, AccountCanonicalIds Canonical)> BindAccountAsync(Guid profileId, Guid personalSpaceId, Guid inboxId, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAccountAsync(string confirmation, CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceAccount> GetAccountAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string> GetAccessTokenAsync(CancellationToken ct) => throw new NotSupportedException();
        public IAsyncEnumerable<string> StreamLinesAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    }
}
