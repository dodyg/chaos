using Chaos.Application.Abstractions;
using Chaos.Application.Containers;
using Chaos.Application.Contracts;
using Chaos.Application.Summaries;
using Chaos.Application.Tasks;
using Chaos.Domain.Summaries;
using Chaos.Domain.Tasks;

namespace Chaos.Infrastructure.Tests;

/// <summary>
/// The spaces section of summaries and standups (PLAN3 9.12) on real SQLite: what other members did in the
/// person's shared spaces, and the standup the person reviews, edits and posts by hand.
/// </summary>
public sealed class SpaceSummaryTests
{
    private static readonly DateOnly Today = SummaryTests.Today;
    private static readonly string Team = "11111111-1111-4111-8111-111111111111";
    private static readonly string Club = "22222222-2222-4222-8222-222222222222";
    private static readonly string Omar = "33333333-3333-4333-8333-333333333333";
    private static string Instant(DateOnly date, int hour) => SummaryTests.At(date, hour).ToString("O");

    /// <summary>A shared space "Team" where Omar is the owner and the person an editor, and "Club" where the person is not a member. Returns the person's profile ID.</summary>
    private static string SeedSpaces(SummaryTests.Host host)
    {
        var me = host.Storage.Store.QueryAsync((session, ct) => session.Profiles.GetPrimaryAsync(ct)).GetAwaiter().GetResult()!.Id;
        var joined = Instant(Today.AddDays(-9), 9);
        host.Run($"""
            INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ('{Team}','{Omar}',2,'Team','{joined}','{joined}');
            INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ('{Club}','{Omar}',2,'Club','{joined}','{joined}');
            INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES
                ('member-omar','{Team}','{Omar}',1,'Omar',1,'{joined}'),
                ('member-me','{Team}','{me}',1,'Me',2,'{joined}'),
                ('member-bo','{Club}','{Omar}',1,'Bo',1,'{joined}');
            """);
        return me;
    }

    /// <summary>
    /// The day's work in Team: Omar completed "Write the plan", created "Call the venue" (due in three days, assigned
    /// to him) and moved "Book the hall" (assigned to the person) to Waiting, then declined a delegation of it; the
    /// person completed "Mine" and captured "Prepare demo" for themself. The commands record everything under the
    /// person's identity, so the rows that were Omar's are relabelled afterwards, as his device would have written them.
    /// </summary>
    private static async Task<(string Plan, string Venue, string Hall, string Mine)> SeedDayAsync(SummaryTests.Host host, string me)
    {
        host.Clock.Now = SummaryTests.At(Today, 9);
        var list = (await host.Containers.CreateAsync(new(SummaryTests.Op(), "Launch", ContainerKind.List, Team))).Container;
        var plan = (await host.Tasks.CaptureAsync(new(SummaryTests.Op(), list.Id, "Write the plan", null))).Task;
        var venue = (await host.Tasks.CaptureAsync(new(SummaryTests.Op(), list.Id, "Call the venue", null))).Task;
        var hall = (await host.Tasks.CaptureAsync(new(SummaryTests.Op(), list.Id, "Book the hall", null))).Task;
        var mine = (await host.Tasks.CaptureAsync(new(SummaryTests.Op(), list.Id, "Mine", null))).Task;
        var demo = (await host.Tasks.CaptureAsync(new(SummaryTests.Op(), list.Id, "Prepare demo", null))).Task;
        var withDue = await host.Tasks.UpdateDetailsAsync(new(SummaryTests.Op(), venue.Id, venue.Version, venue.Title, venue.Notes, null, null, null, Today.AddDays(3), null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
        await host.Tasks.ChangeAssigneeAsync(new(SummaryTests.Op(), venue.Id, withDue.Task.Version, Omar, true));
        await host.Tasks.ChangeAssigneeAsync(new(SummaryTests.Op(), hall.Id, hall.Version, me, true));
        await host.Tasks.ChangeAssigneeAsync(new(SummaryTests.Op(), demo.Id, demo.Version, me, true));

        host.Clock.Now = SummaryTests.At(Today, 10);
        await host.SetStatusAsync(plan.Id, TaskItemStatus.Done);
        await host.SetStatusAsync(mine.Id, TaskItemStatus.Done);
        host.Clock.Now = SummaryTests.At(Today, 11);
        await host.SetStatusAsync(hall.Id, TaskItemStatus.WaitingOnSomeone);

        host.Run($"""
            UPDATE TaskHistory SET Actor = 'person:{Omar}'
                WHERE (TaskId = '{plan.Id}' AND EventKind = 'task.completed')
                   OR (TaskId = '{venue.Id}' AND EventKind = 'task.created')
                   OR (TaskId = '{hall.Id}' AND EventKind = 'task.status-changed');
            INSERT INTO TaskHistory (Id,TaskId,OperationId,EffectiveAtUtc,Actor,EventKind,ChangedFields,SnapshotBefore,SnapshotAfter,AggregateVersion,RecordedAtUtc)
                SELECT '{SummaryTests.Op()}', '{hall.Id}', '{SummaryTests.Op()}', '{Instant(Today, 12)}', 'person:{Omar}', 'task.delegation-decline', '["Assignees"]',
                       SnapshotAfter, SnapshotAfter, AggregateVersion + 1, '{Instant(Today, 12)}'
                FROM TaskHistory WHERE TaskId = '{hall.Id}' ORDER BY EffectiveAtUtc DESC, Id DESC LIMIT 1;
            """);
        return (plan.Id, venue.Id, hall.Id, mine.Id);
    }

    [Test]
    public async Task Spaces_ListOnlyOtherMembersDoings_InSpacesWhereThePersonIsAMember()
    {
        using var host = await SummaryTests.Host.StartAsync(SummaryTests.At(Today.AddDays(-1), 9));
        var me = SeedSpaces(host);
        var (_, venue, _, mine) = await SeedDayAsync(host, me);
        var personal = await host.TaskAsync("Personal thing");
        host.Clock.Now = SummaryTests.At(Today, 10, 30);
        await host.SetStatusAsync(personal.Id, TaskItemStatus.Done);
        host.Clock.Now = SummaryTests.At(Today, 19);

        var summary = await host.GenerateAsync(SummaryKind.Daily, Today);

        var team = summary.Facts.Spaces.Single();
        await Assert.That((team.SpaceId, team.Name)).IsEqualTo((Team, "Team"));
        await Assert.That(team.CompletedByOthers.Select(item => (item.Task.Title, item.Actor))).IsEquivalentTo(new[] { ("Write the plan", "Omar") });
        await Assert.That(team.NewByOthers.Select(item => item.Task.Title)).IsEquivalentTo(new[] { "Call the venue" });
        await Assert.That(team.NewlyBlocked.Select(item => item.Task.Title)).IsEquivalentTo(new[] { "Book the hall", "Book the hall" });
        await Assert.That(team.NewlyBlocked.Select(item => item.BlockedBy)).IsEquivalentTo(new SpaceBlockReason?[] { SpaceBlockReason.Waiting, SpaceBlockReason.Declined });
        await Assert.That(team.Deadlines.Single().Task.TaskId).IsEqualTo(venue);
        await Assert.That(team.Deadlines.Single().Assignees).IsEquivalentTo(new[] { "Omar" });
        await Assert.That(team.Projects).IsEmpty();
        // The person's own completion in the space is not team news; the Personal one stays in the personal section.
        await Assert.That(summary.Facts.Spaces.SelectMany(space => space.CompletedByOthers).Any(item => item.Task.TaskId == mine)).IsFalse();
        await Assert.That(summary.Facts.Completions.Single().Task.Title).IsEqualTo("Personal thing");
        // A space the person does not belong to never appears.
        await Assert.That(summary.Facts.Spaces.Any(space => space.SpaceId == Club)).IsFalse();
        await Assert.That(summary.Counts.SpaceActivity).IsEqualTo(4);
        await Assert.That(summary.Summary.ResourceVersion).IsEqualTo(SummaryCommands.ResourceVersion);
    }

    [Test]
    public async Task Standup_IsPrefilledFromThePersonsOwnWorkInOneSpace_AndPostedOnlyByTheExplicitCommand()
    {
        using var host = await SummaryTests.Host.StartAsync(SummaryTests.At(Today.AddDays(-1), 9));
        var me = SeedSpaces(host);
        _ = await SeedDayAsync(host, me);
        // A meeting tomorrow that the summary shows when meetings are included: it is the person's calendar, never the team's news.
        host.Run("UPDATE Profiles SET IncludeMeetingsInSummaries = 1;");
        var now = host.Clock.Now;
        await host.Storage.Store.ExecuteAsync(async (session, ct) =>
        {
            var sourceId = Guid.NewGuid().ToString("D");
            await session.Calendars.AddSourceAsync(new CalendarSourceRecord
            {
                Id = sourceId, Version = 1, CreatedAtUtc = now, UpdatedAtUtc = now, Provider = CalendarProvider.Google,
                ExternalId = "primary", Name = "Calendar", Color = "#123456", CountsAsBusy = true, Enabled = true, Position = 0,
            }, ct);
            await session.Calendars.ReplaceEventsAsync(sourceId,
                [new CalendarEventRecord(sourceId, "board", SummaryTests.At(Today.AddDays(1), 9), SummaryTests.At(Today.AddDays(1), 10), false, null, null,
                    "Board meeting", null, null, null, true, now)], ct);
            return 0;
        }, CancellationToken.None);
        host.Clock.Now = SummaryTests.At(Today, 19);
        var summary = await host.GenerateAsync(SummaryKind.Daily, Today);
        await Assert.That(summary.Facts.Meetings.Single().Title).IsEqualTo("Board meeting");
        await Assert.That(summary.Facts.Spaces.Single().Deadlines.Single().Task.DueDate).IsEqualTo(Today.AddDays(3));

        // The draft is the person's own work in Team only, and nothing is posted by making it.
        var draft = await host.Summaries.StandupDraftAsync(summary.Summary.Id, Team);
        await Assert.That((draft.SpaceId, draft.SpaceName, draft.ForDate)).IsEqualTo((Team, "Team", Today));
        foreach (var line in new[] { "## Done", "- Mine", "## Planned", "- Prepare demo", "## Blocked", "- Book the hall" })
        {
            await Assert.That(draft.Markdown.Contains(line, StringComparison.Ordinal)).IsTrue();
        }

        foreach (var text in new[] { "Write the plan", "Call the venue", "Board meeting" })
        {
            await Assert.That(draft.Markdown.Contains(text, StringComparison.Ordinal)).IsFalse();
        }

        await Assert.That(host.Scalar("SELECT count(*) FROM SpaceUpdates;")).IsEqualTo(0L);

        // Posting is the person's own command with the text they chose; the author may edit and delete.
        var posted = await host.SpaceUpdates.PostAsync(new(SummaryTests.Op(), Team, draft.ForDate, "## Done\n- Mine\n- Also shipped the demo"));
        await Assert.That(posted.Item.AuthorName).IsEqualTo("Me");
        await Assert.That(posted.Item.IsMine).IsTrue();
        await Assert.That(posted.Item.Update.AuthorMemberId).IsEqualTo(me);
        var edited = await host.SpaceUpdates.EditAsync(new(SummaryTests.Op(), posted.Item.Update.Id, 1, "## Done\n- Mine\n- Also shipped the demo\n- Prepare demo"));
        await Assert.That(edited.Item.Update.RawMarkdown).Contains("Prepare demo");
        await Assert.That(edited.Item.Update.EditedAtUtc).IsEqualTo(SummaryTests.At(Today, 19));
        await Assert.That((await host.SpaceUpdates.ListAsync(Team)).Single().Update.Id).IsEqualTo(posted.Item.Update.Id);
        await Assert.That(host.Scalar("SELECT count(*) FROM SpaceUpdates;")).IsEqualTo(1L);
        await Assert.That(async () => await host.SpaceUpdates.EditAsync(new(SummaryTests.Op(), posted.Item.Update.Id, 1, "stale"))).Throws<AppException>();

        // A standup of another member: an editor cannot delete or edit it; an owner can delete it.
        var omarId = Guid.NewGuid().ToString("D");
        host.Run($"""
            INSERT INTO SpaceUpdates (Id,SpaceId,AuthorMemberId,ForDate,RawMarkdown,Version,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('{omarId}','{Team}','{Omar}','{Today:yyyy-MM-dd}','Omar was here',1,'{Instant(Today, 8)}','{Instant(Today, 8)}');
            """);
        await Assert.That((await host.SpaceUpdates.ListAsync(Team)).Select(item => item.AuthorName)).IsEquivalentTo(new[] { "Me", "Omar" });
        await Assert.That(async () => await host.SpaceUpdates.DeleteAsync(new(SummaryTests.Op(), omarId, 1))).Throws<AppException>();
        await Assert.That(async () => await host.SpaceUpdates.EditAsync(new(SummaryTests.Op(), omarId, 1, "forged"))).Throws<AppException>();
        host.Run("UPDATE SpaceMembers SET Role = 1 WHERE Id = 'member-me';");
        var removed = await host.SpaceUpdates.DeleteAsync(new(SummaryTests.Op(), omarId, 1));
        await Assert.That(removed.Item.Update.DeletedAtUtc).IsEqualTo(SummaryTests.At(Today, 19));
        await Assert.That((await host.SpaceUpdates.ListAsync(Team)).Select(item => item.Update.Id)).IsEquivalentTo(new[] { posted.Item.Update.Id });
        var own = await host.SpaceUpdates.DeleteAsync(new(SummaryTests.Op(), edited.Item.Update.Id, edited.Item.Update.Version));
        await Assert.That(own.Item.Update.DeletedAtUtc).IsNotNull();
        await Assert.That(await host.SpaceUpdates.ListAsync(Team)).IsEmpty();
        await Assert.That(host.Scalar("SELECT count(*) FROM SpaceUpdates;")).IsEqualTo(2L);

        // Not a member of Club; a Personal space is no place for a standup; blank text is refused; a morning summary looks ahead.
        var personal = await host.Storage.Store.QueryAsync((session, ct) => session.Spaces.GetPersonalByOwnerAsync(me, ct));
        await Assert.That(async () => await host.SpaceUpdates.PostAsync(new(SummaryTests.Op(), Club, Today, "hi"))).Throws<AppException>();
        await Assert.That(async () => await host.SpaceUpdates.PostAsync(new(SummaryTests.Op(), personal!.Id, Today, "hi"))).Throws<AppException>();
        await Assert.That(async () => await host.Summaries.StandupDraftAsync(summary.Summary.Id, Club)).Throws<AppException>();
        await Assert.That(async () => await host.SpaceUpdates.PostAsync(new(SummaryTests.Op(), Team, Today, "   "))).Throws<AppException>();
        host.Clock.Now = SummaryTests.At(Today.AddDays(1), 8);
        var morning = (await host.Summaries.GenerateDueAsync()).Single(row => row.Kind == SummaryKind.Morning);
        await Assert.That(async () => await host.Summaries.StandupDraftAsync(morning.Id, Team)).Throws<AppException>();

        // Replaying the same post operation adds nothing.
        var operation = SummaryTests.Op();
        var first = await host.SpaceUpdates.PostAsync(new(operation, Team, Today, "Once"));
        var again = await host.SpaceUpdates.PostAsync(new(operation, Team, Today, "Once"));
        await Assert.That(again.Replayed).IsTrue();
        await Assert.That(again.Item.Update.Id).IsEqualTo(first.Item.Update.Id);
        await Assert.That(host.Scalar("SELECT count(*) FROM SpaceUpdates;")).IsEqualTo(3L);
    }

    [Test]
    public async Task Daily_WithoutSharedSpaces_HasNoSpacesSection()
    {
        using var host = await SummaryTests.Host.StartAsync(SummaryTests.At(Today.AddDays(-1), 9));
        await host.TaskAsync("Only me");
        host.Clock.Now = SummaryTests.At(Today, 19);

        var daily = await host.GenerateAsync(SummaryKind.Daily, Today);

        await Assert.That(daily.Facts.Spaces).IsEmpty();
        await Assert.That(daily.Counts.SpaceActivity).IsEqualTo(0);
        await Assert.That(daily.Facts.IsQuiet).IsTrue();
    }
}
