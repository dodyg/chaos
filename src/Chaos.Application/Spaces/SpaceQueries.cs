using Chaos.Application.Abstractions;
using Chaos.Application.People;
using Chaos.Domain.Tasks;

namespace Chaos.Application.Spaces;

/// <summary>A space for the sidebar. Role is this person's role in a shared space (1 owner, 2 editor, 3 viewer), null for Personal.</summary>
public sealed record SpaceSummary(string SpaceId, string Name, SpaceKind Kind, int? Role, int MemberCount);

/// <summary>Spaces and their people as this device holds them (PLAN3 Q4 step 4). A shared space appears only once its snapshot applied.</summary>
public sealed class SpaceQueries(IApplicationStore store)
{
    public Task<IReadOnlyList<SpaceSummary>> ListAsync(CancellationToken ct = default) =>
        store.QueryAsync(async (session, token) =>
        {
            var me = await session.Profiles.GetPrimaryAsync(token);
            var result = new List<SpaceSummary>();
            foreach (var space in await session.Spaces.ListAsync(token))
            {
                if (space.Kind == SpaceKind.Personal)
                {
                    result.Add(new SpaceSummary(space.Id, space.Name, space.Kind, null, 1));
                    continue;
                }

                var members = await session.People.ListAsync(space.Id, token);
                var mine = me is null ? null : members.FirstOrDefault(member => !member.Former
                    && string.Equals(member.ActorId, $"person:{me.Id}", StringComparison.OrdinalIgnoreCase));
                result.Add(new SpaceSummary(space.Id, space.Name, space.Kind, mine?.Role, members.Count(member => !member.Former)));
            }

            return (IReadOnlyList<SpaceSummary>)result;
        }, ct);

    /// <summary>The space's current members, without those who left (their names still resolve through <see cref="PeopleQueries"/>).</summary>
    public async Task<IReadOnlyList<PersonDisplay>> MembersAsync(string spaceId, CancellationToken ct = default) =>
        [.. (await new PeopleQueries(store).ListAsync(spaceId, ct)).Where(person => !person.Former)];
}
