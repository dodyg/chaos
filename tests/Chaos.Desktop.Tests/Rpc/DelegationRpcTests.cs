using Chaos.Application.Abstractions;
using Chaos.Application.Reminders;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>Delegation over RPC (PLAN3 Q13 step 8): the delegator's moves round-trip, and acting as the recipient answers and reports done.</summary>
public sealed class DelegationRpcTests
{
    private static async Task<(ChaosStorageHandle Storage, string TaskId)> SharedTaskAsync(string root, FixedTimeProvider clock, SequentialIdGenerator ids)
    {
        var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
        var profile = (await storage.Store.QueryAsync((session, ct) => session.Profiles.GetPrimaryAsync(ct)))!;
        var space = Guid.NewGuid().ToString("D");
        var container = Guid.NewGuid().ToString("D");
        await storage.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ($space,$owner,2,'Team',$now,$now);
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES ($row,$space,$owner,1,'Sara',1,$now);
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES ($row2,$space,$omar,1,'Omar',2,$now);
                INSERT INTO Containers (Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ($container,$space,1,'Launch','launch',0,$now,$now);
                INSERT INTO Profiles (Id,DisplayName,ZoneId,Locale,DateFormat,FirstWeekday,CreatedAtUtc,UpdatedAtUtc) VALUES ('22222222-2222-4222-8222-222222222222','Omar','UTC','en-US','MDY',0,'2027-01-01T00:00:00Z','2027-01-01T00:00:00Z');
                """;
            command.Parameters.AddWithValue("$space", space);
            command.Parameters.AddWithValue("$owner", profile.Id);
            command.Parameters.AddWithValue("$omar", "22222222-2222-4222-8222-222222222222");
            command.Parameters.AddWithValue("$row", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$row2", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$container", container);
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(clock.GetUtcNow()));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        var task = (await new Chaos.Application.Tasks.TaskCommands(storage.Store, ids, clock).CaptureAsync(new(Guid.NewGuid().ToString(), container, "Send the quote", null))).Task;
        return (storage, task.Id);
    }

    [Test]
    public async Task DelegateNudgeWithdraw_AndAsRecipientAcceptDone_RoundTrip()
    {
        var root = TestPaths.NewDataRoot();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
        var ids = new SequentialIdGenerator();
        var (storage, taskId) = await SharedTaskAsync(root, clock, ids);
        using (storage)
        {
            var rpc = new DelegationRpcService(new Chaos.Application.Tasks.DelegationCommands(storage.Store, ids, clock));
            var before = await rpc.GetAsync(new(taskId), CancellationToken.None);
            await Assert.That(before.Ok).IsTrue();
            await Assert.That(before.Live).IsEmpty();

            var task = (await storage.Store.QueryAsync((session, ct) => session.Tasks.GetAsync(taskId, ct)))!;
            var delegated = await rpc.DelegateAsync(new(Guid.NewGuid().ToString(), taskId, task.Version.ToString(), "22222222-2222-4222-8222-222222222222", "Please handle the venue", "Quote sent", "2026-11-30", true,
                new FollowUpDto("Fixed", clock.GetUtcNow().AddHours(2).ToString("O"), null, null)), CancellationToken.None);
            await Assert.That(delegated.Ok).IsTrue();
            await Assert.That(delegated.Delegation!.State).IsEqualTo("Pending");
            await Assert.That(delegated.Delegation.ToMemberId).IsEqualTo("22222222-2222-4222-8222-222222222222");
            await Assert.That(delegated.Delegation.DueDate).IsEqualTo("2026-11-30");
            var assignees = await storage.Store.QueryAsync((session, ct) => session.Tasks.ListAssigneesAsync(taskId, ct));
            await Assert.That(assignees.Select(row => row.MemberId)).IsEquivalentTo(["22222222-2222-4222-8222-222222222222"]);

            var template = await rpc.NudgeTemplateAsync(new(delegated.Delegation.Id), CancellationToken.None);
            await Assert.That(template.Text).IsEqualTo("Hi Omar, any news on \"Send the quote\"?");
            var nudged = await rpc.NudgeAsync(new(Guid.NewGuid().ToString(), delegated.Delegation.Id, delegated.Delegation.Version, null), CancellationToken.None);
            await Assert.That(nudged.Ok).IsTrue();
            var afterNudge = await rpc.GetAsync(new(taskId), CancellationToken.None);
            await Assert.That(afterNudge.Live.Single().Nudges.Single().Text).IsEqualTo(template.Text);
            await Assert.That(afterNudge.Live.Single().FollowUpDueAtUtc).IsNotNull();

            var due = await new WaitingRpcService(new WaitingCommands(storage.Store, ids, clock)).DueAsync(new((string?)null), CancellationToken.None);
            await Assert.That(due.Items.Single().DelegationId).IsEqualTo(delegated.Delegation.Id);
            await Assert.That(due.Items.Single().DelegatedTo).IsEqualTo("Omar");

            // Act as Omar: the oldest profile is the primary one, so give him the earlier creation date.
            await storage.Store.ExecuteAsync((session, _) =>
            {
                using var command = session.Connection().CreateCommand();
                command.CommandText = "UPDATE Profiles SET CreatedAtUtc='2020-01-01T00:00:00Z' WHERE Id='22222222-2222-4222-8222-222222222222';";
                command.ExecuteNonQuery();
                return Task.FromResult(0);
            });
            var asOmar = new Chaos.Application.Tasks.DelegationCommands(storage.Store, ids, clock);
            var omarView = await rpc.GetAsync(new(taskId), CancellationToken.None);
            var side = omarView.Live.Single();
            await Assert.That(side.ToName).IsEqualTo("Omar");
            var accepted = await rpc.RespondAsync(new(Guid.NewGuid().ToString(), side.Delegation.Id, side.Delegation.Version, "Accept", null), CancellationToken.None);
            await Assert.That(accepted.Ok ? "ok" : accepted.Error?.Message).IsEqualTo("ok");
            await Assert.That(accepted.Delegation!.State).IsEqualTo("Accepted");
            var done = await rpc.DoneAsync(new(Guid.NewGuid().ToString(), side.Delegation.Id, accepted.Delegation.Version, "Quote sent", null), CancellationToken.None);
            await Assert.That(done.Ok).IsTrue();
            await Assert.That(done.Task!.Status).IsEqualTo("NeedsReview");

            // Back as Sara: close the reviewed result.
            await storage.Store.ExecuteAsync((session, _) =>
            {
                using var command = session.Connection().CreateCommand();
                command.CommandText = "UPDATE Profiles SET CreatedAtUtc='2027-01-01T00:00:00Z' WHERE Id='22222222-2222-4222-8222-222222222222';";
                command.ExecuteNonQuery();
                return Task.FromResult(0);
            });
            var review = await rpc.GetAsync(new(taskId), CancellationToken.None);
            await Assert.That(review.Live.Single().FromName).IsEqualTo("Sara");
            var closed = await rpc.CloseAsync(new(Guid.NewGuid().ToString(), done.Delegation!.Id, done.Delegation.Version), CancellationToken.None);
            await Assert.That(closed.Ok).IsTrue();
            await Assert.That(closed.Task!.Status).IsEqualTo("Done");
            var history = await rpc.GetAsync(new(taskId), CancellationToken.None);
            await Assert.That(history.Live).IsEmpty();
            await Assert.That(history.History.Single().Delegation.State).IsEqualTo("Closed");
            var refused = await rpc.CloseAsync(new(Guid.NewGuid().ToString(), done.Delegation!.Id, closed.Delegation!.Version), CancellationToken.None);
            await Assert.That(refused.Ok).IsFalse();
            await Assert.That(refused.Error).IsNotNull();
        }
    }
}
