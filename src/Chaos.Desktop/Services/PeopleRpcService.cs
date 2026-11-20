using Chaos.Application.Activity;
using Chaos.Application.People;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record PeopleListRequest(string SpaceId);
public sealed record PeopleGetRequest(string SpaceId, string ActorId);
public sealed record PersonResponse(string ActorId, string DisplayName, string Initials, int ColorIndex, string? Kind, bool IsMe, int? Role, bool Former);
public sealed record PeopleListResponse(bool Ok, IReadOnlyList<PersonResponse> People, TaskErrorResponse? Error);
public sealed record PeopleGetResponse(bool Ok, PersonResponse? Person, TaskErrorResponse? Error);

[NeoRpcService("people", Version = 1)]
public sealed class PeopleRpcService(PeopleQueries people)
{
    [NeoRpcMethod("list")]
    public async ValueTask<PeopleListResponse> ListAsync(PeopleListRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("people.list");
        return await Rpc.Run("people.list", async () => new PeopleListResponse(true, [.. (await people.ListAsync(request.SpaceId, ct)).Select(Map)], null),
            error => new PeopleListResponse(false, [], error));
    }

    [NeoRpcMethod("get")]
    public async ValueTask<PeopleGetResponse> GetAsync(PeopleGetRequest request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("people.get");
        return await Rpc.Run("people.get", async () => new PeopleGetResponse(true, Map(await people.GetAsync(request.SpaceId, request.ActorId, ct)), null),
            error => new PeopleGetResponse(false, null, error));
    }

    private static PersonResponse Map(PersonDisplay p) => new(p.ActorId, p.DisplayName, p.Initials, p.ColorIndex, p.Kind?.ToString().ToLowerInvariant(), p.IsMe, p.Role, p.Former);
}
