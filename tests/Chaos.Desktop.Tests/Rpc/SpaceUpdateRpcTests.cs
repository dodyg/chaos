using Chaos.Application.Abstractions;
using Chaos.Application.Summaries;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Domain.Summaries;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>
/// Standups over RPC (PLAN3 Q14 step 8): the suggested standup of an evening summary round-trips as text, posting
/// is a separate call the person makes, the Overview list is newest first, and edit and delete carry their versions.
/// </summary>
public sealed class SpaceUpdateRpcTests
{
    private const string Omar = "22222222-2222-4222-8222-222222222222";
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 19, 0, 0, TimeSpan.Zero);

    private static async Task<(ChaosStorageHandle Storage, string SpaceId)> TeamAsync(string root, FixedTimeProvider clock, SequentialIdGenerator ids)
    {
        var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
        var profile = (await storage.Store.QueryAsync((session, ct) => session.Profiles.GetPrimaryAsync(ct)))!;
        var space = Guid.NewGuid().ToString("D");
        await storage.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ($space,$owner,2,'Team',$now,$now);
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES ($row,$space,$owner,1,'Sara',1,$now);
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES ($row2,$space,$omar,1,'Omar',2,$now);
                """;
            command.Parameters.AddWithValue("$space", space);
            command.Parameters.AddWithValue("$owner", profile.Id);
            command.Parameters.AddWithValue("$omar", Omar);
            command.Parameters.AddWithValue("$row", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$row2", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(clock.GetUtcNow()));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        return (storage, space);
    }

    [Test]
    public async Task StandupDraft_ThenPostEditAndDelete_RoundTrip_AndTheListIsNewestFirst()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(Now);
        var ids = new SequentialIdGenerator();
        var (storage, space) = await TeamAsync(root, clock, ids);
        using (storage)
        {
            var summaries = new SummaryCommands(storage.Store, ids, clock);
            var updates = new SpaceUpdateRpcService(new Chaos.Application.Spaces.SpaceUpdateCommands(storage.Store, ids, clock));
            var summary = await new SummaryRpcService(summaries).GenerateAsync(new(Guid.NewGuid().ToString(), "Daily", "2026-11-23"), CancellationToken.None);
            await Assert.That(summary.Ok).IsTrue();

            // Making the summary and its suggested standup posts nothing.
            var draft = await new SummaryRpcService(summaries).StandupDraftAsync(new(summary.Summary!.Header.Id, space), CancellationToken.None);
            await Assert.That(draft.Ok).IsTrue();
            await Assert.That((draft.SpaceName, draft.ForDate)).IsEqualTo(("Team", "2026-11-23"));
            foreach (var heading in new[] { "## Done", "## Planned", "## Blocked" })
            {
                await Assert.That(draft.Markdown!.Contains(heading, StringComparison.Ordinal)).IsTrue();
            }

            var empty = await updates.ListAsync(new(space), CancellationToken.None);
            await Assert.That((empty.Ok, empty.Updates.Count)).IsEqualTo((true, 0));

            var posted = await updates.PostAsync(new(Guid.NewGuid().ToString(), space, draft.ForDate!, draft.Markdown!), CancellationToken.None);
            await Assert.That(posted.Ok).IsTrue();
            await Assert.That((posted.Update!.AuthorName, posted.Update.IsMine, posted.Update.Version, posted.Update.ForDate)).IsEqualTo(("Sara", true, "1", "2026-11-23"));
            var second = await updates.PostAsync(new(Guid.NewGuid().ToString(), space, "2026-11-22", "Yesterday's standup"), CancellationToken.None);
            await Assert.That(second.Ok).IsTrue();

            var edited = await updates.EditAsync(new(Guid.NewGuid().ToString(), posted.Update.Id, "1", "## Done\n- Mine\n- Also shipped"), CancellationToken.None);
            await Assert.That((edited.Ok, edited.Update!.RawMarkdown, edited.Update.Version, edited.Update.EditedAtUtc)).IsEqualTo((true, "## Done\n- Mine\n- Also shipped", "2", StorageFormats.Instant(Now)));

            var list = await updates.ListAsync(new(space), CancellationToken.None);
            await Assert.That(list.Ok).IsTrue();
            await Assert.That(list.Updates.Select(item => (item.Id, item.AuthorName, item.IsMine))).IsEquivalentTo(new[] { (posted.Update.Id, "Sara", true), (second.Update!.Id, "Sara", true) });

            var refused = await new SpaceUpdateRpcService(new Chaos.Application.Spaces.SpaceUpdateCommands(storage.Store, ids, clock))
                .PostAsync(new(Guid.NewGuid().ToString(), space, "23/11/2026", "x"), CancellationToken.None);
            await Assert.That(refused.Ok).IsFalse();
            await Assert.That(refused.Error!.Message).Contains("yyyy-MM-dd");

            var stale = await updates.DeleteAsync(new(Guid.NewGuid().ToString(), second.Update.Id, "9"), CancellationToken.None);
            await Assert.That((stale.Ok, stale.Error!.Kind)).IsEqualTo((false, "versionConflict"));
            var removed = await updates.DeleteAsync(new(Guid.NewGuid().ToString(), second.Update.Id, "1"), CancellationToken.None);
            await Assert.That(removed.Ok).IsTrue();
            var after = await updates.ListAsync(new(space), CancellationToken.None);
            await Assert.That(after.Updates.Select(item => item.Id)).IsEquivalentTo(new[] { posted.Update.Id });
        }
    }
}
