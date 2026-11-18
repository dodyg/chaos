using System.Net;
using System.Text;
using System.Text.Json;
using Chaos.Server.Sync;
using Chaos.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace Chaos.Server.Tests.Sync;

/// <summary>The sync endpoints as raw HTTP (PLAN2 §7.12, §8.3, §8.5): headers, validation, isolation, paging, limits.</summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class SyncProtocolTests(PostgresServer postgres)
{
    private sealed record Caller(string Token, Guid Device, Guid Account);

    private static async Task<Caller> SignInAsync(TestServer server, string email)
    {
        var flow = new DesktopFlow(server);
        var tokens = await flow.SignInAsync(email);
        using var http = server.Factory.CreateClient();
        using var register = DesktopFlow.Api(HttpMethod.Post, "/v1/devices", tokens.AccessToken, new { deviceId = flow.DeviceId, name = "Test", platform = "windows", clientVersion = "1", clientSchema = 1 });
        (await http.SendAsync(register)).EnsureSuccessStatusCode();
        await using var db = ServerDatabase.Open(server.ConnectionString);
        var account = db.Accounts.Single(a => a.PrimaryEmail == email).Id;
        return new Caller(tokens.AccessToken, flow.DeviceId, account);
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(TestServer server, Caller caller, HttpMethod method, string path, string? body = null, int? schema = 1, bool headers = true)
    {
        using var http = server.Factory.CreateClient();
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", caller.Token);
        if (headers)
        {
            request.Headers.Add(SyncProtocol.ProtocolHeader, "1");
            if (schema is { } s)
            {
                request.Headers.Add(SyncProtocol.SchemaHeader, s.ToString());
            }

            request.Headers.Add(SyncProtocol.DeviceHeader, caller.Device.ToString());
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Change(string kind, string key, string group, string column, object? value, long wall = 1_800_000_000_000, string? id = null, Guid? device = null)
    {
        var hlc = Hlc.Create(wall, 0, device ?? Guid.Parse("11111111-1111-1111-1111-111111111111")).Encode();
        return JsonSerializer.Serialize(new
        {
            changeId = id ?? Guid.NewGuid().ToString(), kind, key, op = "upsert",
            groups = new Dictionary<string, object> { [group] = new { hlc, values = new Dictionary<string, object?> { [column] = value } } },
        });
    }

    private static string Push(params string[] changes) => "{\"changes\":[" + string.Join(",", changes) + "]}";

    [Test]
    public async Task Calendar_cache_is_account_scoped_replaced_and_not_pulled()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var alice = await SignInAsync(server, "calendar-owner@example.com");
        var bob = await SignInAsync(server, "calendar-other@example.com");
        var source = Guid.NewGuid().ToString("D");
        var pushed = await SendAsync(server, alice, HttpMethod.Post, "/v1/sync/push",
            Push(Change("calendar_source", source, "enabled", "Enabled", 1)));
        await Assert.That(pushed.Status).IsEqualTo(HttpStatusCode.OK);
        var now = server.Clock.GetUtcNow();
        var item = new CalendarCacheEvent("event", now, now.AddHours(1), false, null, null, "Private meeting", null, null, null, true, now);
        var snapshot = JsonSerializer.Serialize(new CalendarCacheSnapshot(source, now, [item]));
        await Assert.That((await SendAsync(server, bob, HttpMethod.Post, "/v1/sync/calendar-cache", snapshot)).Status).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await SendAsync(server, alice, HttpMethod.Post, "/v1/sync/calendar-cache", snapshot)).Status).IsEqualTo(HttpStatusCode.OK);
        var stale = JsonSerializer.Serialize(new CalendarCacheSnapshot(source, now.AddMinutes(-1), []));
        await Assert.That((await SendAsync(server, alice, HttpMethod.Post, "/v1/sync/calendar-cache", stale)).Status).IsEqualTo(HttpStatusCode.OK);
        await using (var db = ServerDatabase.Open(server.ConnectionString))
        {
            var cached = db.SyncEntities.Single(row => row.AccountId == alice.Account && row.Kind == "calendar_cache");
            await Assert.That(cached.Groups).Contains("Private meeting");
        }
        var pull = await SendAsync(server, alice, HttpMethod.Get, "/v1/sync/pull?after=0");
        await Assert.That(pull.Body.Contains("Private meeting", StringComparison.Ordinal)).IsFalse();
        var empty = JsonSerializer.Serialize(new CalendarCacheSnapshot(source, now.AddSeconds(1), []));
        await Assert.That((await SendAsync(server, alice, HttpMethod.Post, "/v1/sync/calendar-cache", empty)).Status).IsEqualTo(HttpStatusCode.OK);
        await using (var db = ServerDatabase.Open(server.ConnectionString))
        {
            var cached = db.SyncEntities.Single(row => row.AccountId == alice.Account && row.Kind == "calendar_cache");
            await Assert.That(cached.Groups.Contains("Private meeting", StringComparison.Ordinal)).IsFalse();
        }
        await SendAsync(server, alice, HttpMethod.Post, "/v1/sync/push", Push(Change("calendar_source", source, "enabled", "Enabled", 0, wall: 1_800_000_000_001)));
        await using (var db = ServerDatabase.Open(server.ConnectionString))
            await Assert.That(db.SyncEntities.Any(row => row.AccountId == alice.Account && row.Kind == "calendar_cache")).IsFalse();
    }

    [Test]
    public async Task Sync_needs_a_token_and_the_three_protocol_headers()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var caller = await SignInAsync(server, "a@example.com");

        using (var http = server.Factory.CreateClient())
        {
            await Assert.That((await http.GetAsync("/v1/sync/pull")).StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        }

        await Assert.That((await SendAsync(server, caller, HttpMethod.Get, "/v1/sync/pull", headers: false)).Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await SendAsync(server, caller, HttpMethod.Get, "/v1/sync/pull", schema: null)).Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await SendAsync(server, caller, HttpMethod.Get, "/v1/sync/pull")).Status).IsEqualTo(HttpStatusCode.OK);

        var other = caller with { Device = Guid.NewGuid() };
        await Assert.That((await SendAsync(server, other, HttpMethod.Get, "/v1/sync/pull")).Status).IsEqualTo(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task One_account_never_sees_or_touches_anothers_entities()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var alice = await SignInAsync(server, "alice@example.com");
        var bob = await SignInAsync(server, "bob@example.com");
        var key = Guid.NewGuid().ToString();

        await SendAsync(server, alice, HttpMethod.Post, "/v1/sync/push", Push(Change("tag", key, "name", "DisplayName", "alice tag")));
        await SendAsync(server, bob, HttpMethod.Post, "/v1/sync/push", Push(Change("tag", key, "name", "DisplayName", "bob tag", wall: 1_800_000_000_001)));

        var alicePull = (await SendAsync(server, alice, HttpMethod.Get, "/v1/sync/pull?after=0")).Body;
        var bobPull = (await SendAsync(server, bob, HttpMethod.Get, "/v1/sync/pull?after=0")).Body;
        await Assert.That(alicePull).Contains("alice tag");
        await Assert.That(alicePull.Contains("bob tag", StringComparison.Ordinal)).IsFalse();
        await Assert.That(bobPull).Contains("bob tag");
        await Assert.That(bobPull.Contains("alice tag", StringComparison.Ordinal)).IsFalse();

        var snapshot = (await SendAsync(server, alice, HttpMethod.Get, "/v1/sync/snapshot?kind=tag")).Body;
        await Assert.That(snapshot.Contains("bob tag", StringComparison.Ordinal)).IsFalse();
    }

    [Test]
    public async Task Changes_are_refused_with_the_reasons_of_the_protocol()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var caller = await SignInAsync(server, "a@example.com");
        await using (var db = ServerDatabase.Open(server.ConnectionString))
        {
            var space = Guid.NewGuid();
            db.Accounts.Single(a => a.Id == caller.Account).PersonalSpaceId = space;
            await db.SaveChangesAsync();
        }

        var unknown = Change("nonsense", "k", "name", "X", "v");
        var badKey = Change("tag", "two|parts", "name", "DisplayName", "v");
        var badGroup = Change("tag", Guid.NewGuid().ToString(), "bogus", "X", "v");
        var huge = Change("tag", Guid.NewGuid().ToString(), "name", "DisplayName", new string('x', SyncProtocol.MaxTextBytes + 1));
        var foreignSpace = Change("container", Guid.NewGuid().ToString(), "create", "SpaceId", Guid.NewGuid().ToString());
        var ok = Change("tag", Guid.NewGuid().ToString(), "name", "DisplayName", "fine");
        var (status, body) = await SendAsync(server, caller, HttpMethod.Post, "/v1/sync/push", Push(unknown, badKey, badGroup, huge, foreignSpace, ok, "{\"nonsense\":1}"));

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(body);
        var reasons = document.RootElement.GetProperty("rejected").EnumerateArray().Select(r => r.GetProperty("reason").GetString()!).ToList();
        await Assert.That(reasons).IsEquivalentTo(new[]
        {
            SyncRejection.UnknownKind, SyncRejection.Invalid, SyncRejection.Invalid, SyncRejection.TooLarge, SyncRejection.NotOwner, SyncRejection.Invalid,
        });
        await Assert.That(document.RootElement.GetProperty("accepted").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task A_client_below_the_minimum_schema_is_told_to_update()
    {
        await using var server = await TestServer.StartAsync(postgres, new() { ["Sync:MinClientSchema"] = "5" });
        var caller = await SignInAsync(server, "a@example.com");
        var (_, body) = await SendAsync(server, caller, HttpMethod.Post, "/v1/sync/push", Push(Change("tag", Guid.NewGuid().ToString(), "name", "DisplayName", "v")));
        await Assert.That(body).Contains(SyncRejection.SchemaTooOld);
    }

    [Test]
    public async Task A_purged_entity_refuses_a_late_edit_and_a_push_is_limited_to_500_changes_and_4_MiB()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var caller = await SignInAsync(server, "a@example.com");
        var key = Guid.NewGuid().ToString();
        var purge = JsonSerializer.Serialize(new { changeId = Guid.NewGuid(), kind = "tag", key, op = "purge", groups = new { } });
        await SendAsync(server, caller, HttpMethod.Post, "/v1/sync/push", Push(purge));
        var (_, late) = await SendAsync(server, caller, HttpMethod.Post, "/v1/sync/push", Push(Change("tag", key, "name", "DisplayName", "too late")));
        await Assert.That(late).Contains(SyncRejection.Purged);

        var many = Enumerable.Range(0, SyncProtocol.MaxPushChanges + 1).Select(_ => Change("tag", Guid.NewGuid().ToString(), "name", "DisplayName", "v")).ToArray();
        await Assert.That((await SendAsync(server, caller, HttpMethod.Post, "/v1/sync/push", Push(many))).Status).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        var big = new string('x', SyncProtocol.MaxPushBytes + 1);
        await Assert.That((await SendAsync(server, caller, HttpMethod.Post, "/v1/sync/push", big)).Status).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
    }

    [Test]
    public async Task Pull_and_snapshot_page_in_order_and_a_cursor_behind_the_horizon_asks_for_the_snapshot()
    {
        await using var server = await TestServer.StartAsync(postgres, new() { ["Sync:ChangeLogHorizon"] = "00:00:01" });
        var caller = await SignInAsync(server, "a@example.com");
        var changes = Enumerable.Range(0, 5).Select(n => Change("tag", $"{n:D2}-{Guid.NewGuid()}", "name", "DisplayName", $"tag {n}")).ToArray();
        await SendAsync(server, caller, HttpMethod.Post, "/v1/sync/push", Push(changes));

        using var first = JsonDocument.Parse((await SendAsync(server, caller, HttpMethod.Get, "/v1/sync/pull?after=0&limit=2")).Body);
        await Assert.That(first.RootElement.GetProperty("changes").GetArrayLength()).IsEqualTo(2);
        await Assert.That(first.RootElement.GetProperty("more").GetBoolean()).IsTrue();
        var next = first.RootElement.GetProperty("next").GetInt64();
        using var rest = JsonDocument.Parse((await SendAsync(server, caller, HttpMethod.Get, $"/v1/sync/pull?after={next}&limit=10")).Body);
        await Assert.That(rest.RootElement.GetProperty("changes").GetArrayLength()).IsEqualTo(3);
        await Assert.That(rest.RootElement.GetProperty("more").GetBoolean()).IsFalse();

        using var page1 = JsonDocument.Parse((await SendAsync(server, caller, HttpMethod.Get, "/v1/sync/snapshot?kind=tag&limit=3")).Body);
        await Assert.That(page1.RootElement.GetProperty("more").GetBoolean()).IsTrue();
        var after = page1.RootElement.GetProperty("next").GetString()!;
        using var page2 = JsonDocument.Parse((await SendAsync(server, caller, HttpMethod.Get, $"/v1/sync/snapshot?kind=tag&limit=3&after={Uri.EscapeDataString(after)}")).Body);
        await Assert.That(page2.RootElement.GetProperty("changes").GetArrayLength()).IsEqualTo(2);
        await Assert.That((await SendAsync(server, caller, HttpMethod.Get, "/v1/sync/snapshot?kind=nonsense")).Status).IsEqualTo(HttpStatusCode.BadRequest);

        server.Clock.Advance(TimeSpan.FromSeconds(5));
        await using (var scope = server.Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SyncService>().PruneAsync(CancellationToken.None);
        }

        using var behind = JsonDocument.Parse((await SendAsync(server, caller, HttpMethod.Get, "/v1/sync/pull?after=1")).Body);
        await Assert.That(behind.RootElement.GetProperty("snapshotRequired").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task A_push_tells_the_account_listeners_and_logs_no_content()
    {
        await using var server = await TestServer.StartAsync(postgres);
        var caller = await SignInAsync(server, "a@example.com");
        long heard = 0;
        using var subscription = server.Factory.Services.GetRequiredService<SyncBroadcaster>().Subscribe(caller.Account, seq => heard = seq);

        await SendAsync(server, caller, HttpMethod.Post, "/v1/sync/push", Push(Change("tag", Guid.NewGuid().ToString(), "name", "DisplayName", "secret tag name")));

        await Assert.That(heard).IsEqualTo(1L);
        string[] lines;
        lock (server.Logs.Lines)
        {
            lines = [.. server.Logs.Lines];
        }

        await Assert.That(lines.Any(line => line.Contains("secret tag name", StringComparison.Ordinal))).IsFalse();
    }
}
