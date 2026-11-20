using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Chaos.Server.Sync;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Tests.Sharing;

internal sealed record SharingCaller(
    string Email, string Token, Guid AccountId, Guid DeviceId, Guid ProfileId, Guid PersonalSpaceId, Guid InboxId);

internal sealed record RawResponse(HttpStatusCode Status, string Body)
{
    public JsonDocument Json() => JsonDocument.Parse(Body);
    public IReadOnlyList<string> Rejections() =>
        Json().RootElement.GetProperty("rejected").EnumerateArray()
            .Select(item => item.GetProperty("reason").GetString()!).ToList();
}

internal sealed class SharingHarness(TestServer server) : IAsyncDisposable
{
    private readonly List<DesktopFlow> _flows = [];

    public async Task<SharingCaller> SignInAsync(string email, Guid? profile = null, bool bind = true)
    {
        var flow = new DesktopFlow(server);
        _flows.Add(flow);
        var tokens = await flow.SignInAsync(email);
        using var http = server.Factory.CreateClient();
        using var register = DesktopFlow.Api(HttpMethod.Post, "/v1/devices", tokens.AccessToken, new
        {
            deviceId = flow.DeviceId, name = "Q3 raw client", platform = "windows", clientVersion = "test", clientSchema = 4,
        });
        (await http.SendAsync(register)).EnsureSuccessStatusCode();
        await using var db = ServerDatabase.Open(server.ConnectionString);
        var account = await db.Accounts.SingleAsync(a => a.PrimaryEmail == email);
        var ids = new SharingCaller(email, tokens.AccessToken, account.Id, flow.DeviceId,
            profile ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        if (bind)
        {
            using var binding = DesktopFlow.Api(HttpMethod.Post, "/v1/account/bind", tokens.AccessToken, new
            {
                profileId = ids.ProfileId, personalSpaceId = ids.PersonalSpaceId, inboxId = ids.InboxId,
            });
            (await http.SendAsync(binding)).EnsureSuccessStatusCode();
        }
        return ids;
    }

    public async Task<Guid> CreateSpaceAsync(SharingCaller owner, string name = "Shared work")
    {
        var id = Guid.NewGuid();
        var response = await CallAsync(owner, HttpMethod.Post, "/v2/spaces", new { spaceId = id, name });
        if (response.Status != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Space creation failed {(int)response.Status}: {response.Body}");
        }
        return id;
    }

    public async Task<string> InviteLinkAsync(SharingCaller owner, Guid spaceId, short role, DateTimeOffset? expires = null,
        int? uses = 10)
    {
        var response = await CallAsync(owner, HttpMethod.Post, $"/v2/spaces/{spaceId:D}/invites",
            new CreateInviteRequest(null, role, expires, uses));
        response.Status.Ensure(HttpStatusCode.OK);
        using var json = response.Json();
        return json.RootElement.GetProperty("link").GetString()!;
    }

    public async Task JoinAsync(SharingCaller member, string link)
    {
        var token = new Uri(link).Segments.Last();
        var response = await CallAsync(member, HttpMethod.Post, "/v2/invites/accept", new AcceptInviteRequest(token));
        response.Status.Ensure(HttpStatusCode.OK);
    }

    public async Task<RawResponse> PushAsync(SharingCaller caller, params RawChange[] changes) =>
        await CallAsync(caller, HttpMethod.Post, "/v2/sync/push", Push(changes), version: 2);

    public async Task<RawResponse> PushV1Async(SharingCaller caller, params RawChange[] changes) =>
        await CallAsync(caller, HttpMethod.Post, "/v1/sync/push", Push(changes), version: 1);

    public async Task<RawResponse> CallAsync(SharingCaller caller, HttpMethod method, string path, object? body = null,
        int version = 2, int schema = 4)
    {
        using var http = server.Factory.CreateClient();
        return await SendAsync(http, caller, method, path, body, version, schema);
    }

    public HttpClient CreateCallerClient(SharingCaller caller, int version = 2)
    {
        var http = server.Factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
        http.DefaultRequestHeaders.Add(SyncProtocol.ProtocolHeader, version.ToString());
        http.DefaultRequestHeaders.Add(SyncProtocol.SchemaHeader, "4");
        http.DefaultRequestHeaders.Add(SyncProtocol.DeviceHeader, caller.DeviceId.ToString());
        return http;
    }

    private static async Task<RawResponse> SendAsync(HttpClient http, SharingCaller caller, HttpMethod method, string path,
        object? body, int version, int schema)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
        request.Headers.Add(SyncProtocol.ProtocolHeader, version.ToString());
        request.Headers.Add(SyncProtocol.SchemaHeader, schema.ToString());
        request.Headers.Add(SyncProtocol.DeviceHeader, caller.DeviceId.ToString());
        if (body is not null)
        {
            request.Content = body is string text
                ? new StringContent(text, Encoding.UTF8, "application/json")
                : JsonContent.Create(body);
        }
        using var response = await http.SendAsync(request);
        return new(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public static RawChange Change(string kind, string key, string group, object values, string? changeId = null) =>
        new(changeId ?? Guid.NewGuid().ToString("D"), kind, key, "upsert",
            new Dictionary<string, RawGroup>
            {
                [group] = new(Hlc.Create(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0,
                    Guid.Parse("11111111-1111-1111-1111-111111111111")).Encode(), values),
            });

    public static RawChange Purge(string kind, string key) =>
        new(Guid.NewGuid().ToString("D"), kind, key, "purge", new Dictionary<string, RawGroup>());

    private static string Push(RawChange[] changes) => JsonSerializer.Serialize(new { changes });

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed record RawGroup(
    [property: JsonPropertyName("hlc")] string Hlc,
    [property: JsonPropertyName("values")] object Values);
internal sealed record RawChange(
    [property: JsonPropertyName("changeId")] string ChangeId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("groups")] IReadOnlyDictionary<string, RawGroup> Groups);

file static class HttpStatusExtensions
{
    public static void Ensure(this HttpStatusCode actual, HttpStatusCode expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Expected {(int)expected}, got {(int)actual}.");
        }
    }
}
