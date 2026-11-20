using Chaos.Application.People;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;

namespace Chaos.Infrastructure.Tests.Sync;

public sealed class PeopleQueriesTests
{
    [Test]
    public async Task ListAsync_SharedSpace_RetainsFormerMemberAndResolvesMe()
    {
        using var host = await DataHost.StartAsync();
        var profile = (await new SyncAccountState(host.Store).ReadIdsAsync(CancellationToken.None)).ProfileId;
        var space = Guid.NewGuid().ToString("D");
        var former = Guid.NewGuid().ToString("D");
        await host.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ($space,$profile,2,'Team','2026-11-20T00:00:00.0000000+00:00','2026-11-20T00:00:00.0000000+00:00');
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES ($one,$space,$profile,1,'Me',1,'now');
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc,RemovedAtUtc) VALUES ($two,$space,$former,1,'Former Member',2,'now','later');
                """;
            command.Parameters.AddWithValue("$space", space);
            command.Parameters.AddWithValue("$profile", profile);
            command.Parameters.AddWithValue("$former", former);
            command.Parameters.AddWithValue("$one", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$two", Guid.NewGuid().ToString("D"));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        var queries = new PeopleQueries(host.Store);
        var people = await queries.ListAsync(space);
        await Assert.That(people.Single(p => p.ActorId == $"person:{profile}").IsMe).IsTrue();
        var retained = await queries.GetAsync(space, $"person:{former}");
        await Assert.That(retained.DisplayName).IsEqualTo("Former Member");
        await Assert.That(retained.Former).IsTrue();
        await Assert.That((await queries.GetAsync(space, $"person:{Guid.NewGuid():D}")).DisplayName).IsEqualTo("Unknown person");
    }
}
