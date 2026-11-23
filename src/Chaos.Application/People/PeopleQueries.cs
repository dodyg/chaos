using Chaos.Application.Abstractions;
using Chaos.Domain.Tasks;

namespace Chaos.Application.People;

/// <summary>Resolves space members and retained former members to display information.</summary>
public sealed class PeopleQueries(IApplicationStore store)
{
    public Task<IReadOnlyList<PersonDisplay>> ListAsync(string spaceId, CancellationToken ct = default) =>
        store.QueryAsync(async (session, token) =>
        {
            if (!Guid.TryParseExact(spaceId, "D", out var id)) throw AppException.Validation("spaceId", "Choose a valid space.");
            var canonical = id.ToString("D");
            var space = await session.Spaces.GetAsync(canonical, token) ?? throw new AppException(AppErrorKind.NotFound, "That space does not exist.");
            var profile = await session.Profiles.GetPrimaryAsync(token);
            var members = await session.People.ListAsync(canonical, token);
            var directory = members.Select(m => new DirectoryMember(m.ActorId, m.DisplayName)).ToList();
            var myActor = profile is null ? null : $"person:{profile.Id}";
            if (space.Kind == SpaceKind.Personal && profile is not null && !directory.Any(m => string.Equals(m.ActorId, myActor, StringComparison.OrdinalIgnoreCase)))
                directory.Add(new DirectoryMember(myActor!, profile.DisplayName));
            return (IReadOnlyList<PersonDisplay>)[.. directory.Select(m =>
            {
                var member = members.FirstOrDefault(entry => string.Equals(entry.ActorId, m.ActorId, StringComparison.OrdinalIgnoreCase));
                return PeopleDirectory.Resolve(m.ActorId, myActor, directory) with { Role = member?.Role, Former = member?.Former ?? false, GitHubLogin = member?.GitHubLogin };
            })];
        }, ct);

    public async Task<PersonDisplay> GetAsync(string spaceId, string? actorId, CancellationToken ct = default)
    {
        var people = await ListAsync(spaceId, ct);
        return people.FirstOrDefault(p => string.Equals(p.ActorId, actorId, StringComparison.OrdinalIgnoreCase))
            ?? PeopleDirectory.Resolve(actorId, null, []);
    }
}
