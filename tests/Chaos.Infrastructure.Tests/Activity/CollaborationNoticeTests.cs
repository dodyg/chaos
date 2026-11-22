using System.Text.Json;
using Chaos.Application.Abstractions;
using Chaos.Application.History;
using Chaos.Application.Reminders;
using Chaos.Infrastructure.Activity;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Transfer;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Activity;

public sealed class CollaborationNoticeTests
{
    private sealed class Presenter : INotificationAdapter
    {
        public NotificationCapabilities Capabilities { get; } = new("test", false, false, NotificationPermission.Granted, "test");
        public Task<NotificationScheduleResult> ScheduleAsync(NotificationRequest request, CancellationToken ct) => Task.FromResult(new NotificationScheduleResult(false, null, null));
        public Task CancelAsync(string osNotificationId, CancellationToken ct) => Task.CompletedTask;
        public Task<NotificationShowOutcome> ShowAsync(NotificationRequest request, CancellationToken ct) => Task.FromResult(NotificationShowOutcome.InApp);
    }

    [Test]
    public async Task DefaultDeadline_RemindsOnlyTheAssigneeAndCanBeDisabled()
    {
        using var host = await DataHost.StartAsync();
        var profile = (await host.Store.QueryAsync((s, ct) => s.Profiles.GetPrimaryAsync(ct)))!;
        var task = await CaptureTaskAsync(host);
        var space = Guid.NewGuid().ToString("D");
        var other = Guid.NewGuid().ToString("D");
        await host.Store.ExecuteAsync((s, _) =>
        {
            using var command = s.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ($space,$profile,2,'Team',$now,$now);
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES ($row,$space,$profile,1,'Me',1,$now);
                UPDATE Tasks SET SpaceId=$space,DueDate='2026-11-10',DueTime='11:00:00',DueZoneId='UTC' WHERE Id=$task;
                INSERT INTO TaskAssignees (TaskId,MemberId,AddedAtUtc) VALUES ($task,$other,$now);
                """;
            command.Parameters.AddWithValue("$space", space);
            command.Parameters.AddWithValue("$profile", profile.Id);
            command.Parameters.AddWithValue("$other", other);
            command.Parameters.AddWithValue("$task", task.Id);
            command.Parameters.AddWithValue("$row", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(host.Clock.Now));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        var preferences = new SpaceNotificationPreferences(host.Store, host.Clock);
        await preferences.SetDefaultDeadlineAsync(space, -60);
        await Assert.That(await preferences.GetDefaultDeadlineAsync(space)).IsEqualTo(-60);
        var reminders = new ReminderCommands(host.Store, host.Ids, host.Clock, host.Tasks, new Presenter());
        await reminders.ResyncAllAsync();
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM NotificationJobs WHERE SourceKind=2 AND State=0;")).IsEqualTo(0L);
        await host.Store.ExecuteAsync((s, _) =>
        {
            using var command = s.Connection().CreateCommand();
            command.CommandText = "INSERT INTO TaskAssignees (TaskId,MemberId,AddedAtUtc) VALUES ($task,$profile,$now);";
            command.Parameters.AddWithValue("$task", task.Id);
            command.Parameters.AddWithValue("$profile", profile.Id);
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(host.Clock.Now));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        await reminders.ResyncAllAsync();
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM NotificationJobs WHERE SourceKind=2 AND State=0;")).IsEqualTo(1L);
        await preferences.SetAsync(space, new SpaceNotificationPreference(DueSoon: true));
        await Assert.That(await new CollaborationNoticeDeriver(host.Store, host.Clock).DeriveAsync()).IsEqualTo(1);
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM CollaborationNotices WHERE Kind='due-soon';")).IsEqualTo(1L);
        await preferences.SetAsync(space, new SpaceNotificationPreference(OwnerDefaultReminder: false));
        await reminders.ResyncAllAsync();
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM NotificationJobs WHERE SourceKind=2 AND State=0;")).IsEqualTo(0L);
        await preferences.SetAsync(space, new SpaceNotificationPreference());
        await reminders.ResyncAllAsync();
        host.Clock.Now = host.Clock.Now.AddHours(1);
        await Assert.That((await reminders.DeliverDueAsync()).Count).IsEqualTo(1);
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM ReminderRules;")).IsEqualTo(0L);
    }
    [Test]
    public async Task MarkdownExport_RewritesMentionsButLeavesCodeUntouched()
    {
        var id = Guid.NewGuid().ToString("D");
        var link = $"[@Bo](chaos:member/{id})";
        var source = $"Hello {link}\n`{link}`\n```md\n{link}\n```\n";
        var result = PlainTextExport.RewriteMemberLinks(source);
        await Assert.That(result).IsEqualTo($"Hello @Bo\n`{link}`\n```md\n{link}\n```\n");
    }
    [Test]
    public async Task Diary_ViewerCanCommentAndOnlyAuthorOrOwnerCanChangeEntry()
    {
        using var host = await DataHost.StartAsync();
        var profile = (await host.Store.QueryAsync((s, ct) => s.Profiles.GetPrimaryAsync(ct)))!;
        var task = await CaptureTaskAsync(host);
        var space = Guid.NewGuid().ToString("D");
        var other = Guid.NewGuid().ToString("D");
        await host.Store.ExecuteAsync((s, _) =>
        {
            using var command = s.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ($space,$other,2,'Team',$now,$now);
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES ($row,$space,$profile,1,'Me',3,$now);
                UPDATE Tasks SET SpaceId=$space WHERE Id=$task;
                """;
            command.Parameters.AddWithValue("$space", space);
            command.Parameters.AddWithValue("$profile", profile.Id);
            command.Parameters.AddWithValue("$other", other);
            command.Parameters.AddWithValue("$task", task.Id);
            command.Parameters.AddWithValue("$row", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$now", StorageFormats.Instant(host.Clock.Now));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        var diary = new DiaryCommands(host.Store, host.Ids, host.Clock);
        var entry = (await diary.AddAsync(new(DataHost.Op(), task.Id, "Viewer comment", null))).Entry;
        await host.Store.ExecuteAsync((s, _) =>
        {
            using var command = s.Connection().CreateCommand();
            command.CommandText = "UPDATE DiaryEntries SET AuthorProfileId=$other WHERE Id=$id;";
            command.Parameters.AddWithValue("$other", other);
            command.Parameters.AddWithValue("$id", entry.Id);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        await Assert.That(async () => await diary.EditAsync(new(DataHost.Op(), entry.Id, entry.Version, "Changed"))).Throws<AppException>();
        await Assert.That(async () => await diary.DeleteAsync(new(DataHost.Op(), entry.Id, entry.Version))).Throws<AppException>();
        await host.Store.ExecuteAsync((s, _) =>
        {
            using var command = s.Connection().CreateCommand();
            command.CommandText = "UPDATE SpaceMembers SET Role=1 WHERE SpaceId=$space AND MemberId=$profile;";
            command.Parameters.AddWithValue("$space", space);
            command.Parameters.AddWithValue("$profile", profile.Id);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        await Assert.That(async () => await diary.EditAsync(new(DataHost.Op(), entry.Id, entry.Version, "Changed"))).Throws<AppException>();
        await Assert.That((await diary.DeleteAsync(new(DataHost.Op(), entry.Id, entry.Version))).Entry.DeletedAtUtc).IsNotNull();
        var removed = (await diary.TimelineAsync(task.Id)).Single(item => item.Diary?.Id == entry.Id).Diary!;
        await Assert.That(removed.RemovedByOwner).IsTrue();
    }

    [Test]
    public async Task DeriveAsync_MentionsAreIdempotentAndIgnoreOwnOrUnchangedText()
    {
        using var host = await DataHost.StartAsync();
        var profile = (await host.Store.QueryAsync((s, ct) => s.Profiles.GetPrimaryAsync(ct)))!;
        var space = Guid.NewGuid().ToString("D");
        var actor = Guid.NewGuid().ToString("D");
        var mention = $"[@Me](chaos:member/{profile.Id})";
        await host.Store.ExecuteAsync((s, _) =>
        {
            using var command = s.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ($space,$profile,2,'Team','now','now');
                INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,Role,JoinedAtUtc) VALUES ($row,$space,$profile,1,'Me',2,'now');
                UPDATE Profiles SET ZoneId='UTC', QuietHoursEnabled=1, QuietStart='08:00:00', QuietEnd='10:00:00' WHERE Id=$profile;
                INSERT INTO ActivityLog (OperationId,OccurredAtUtc,Source,Action,Outcome,DetailJson) VALUES ('op','now','sync','sync.apply','ok',$detail);
                """;
            command.Parameters.AddWithValue("$space", space);
            command.Parameters.AddWithValue("$profile", profile.Id);
            command.Parameters.AddWithValue("$row", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(new[] {
                new RemoteTextChange("1", "diary_entry", "entry", space, actor, "before", mention),
                new RemoteTextChange("2", "diary_entry", "entry", space, actor, mention, mention + " updated"),
                new RemoteTextChange("3", "diary_entry", "own", space, profile.Id, "", mention),
            }));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        var deriver = new CollaborationNoticeDeriver(host.Store, host.Clock);
        await Assert.That(await deriver.DeriveAsync()).IsEqualTo(1);
        await Assert.That(await deriver.DeriveAsync()).IsEqualTo(0);
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM CollaborationNotices;")).IsEqualTo(1L);
        await Assert.That(await TextAsync(host, "SELECT NotifyAtUtc FROM CollaborationNotices LIMIT 1;"))
            .IsEqualTo(StorageFormats.Instant(host.Clock.Now.AddHours(1)));
        var inbox = new CollaborationInbox(host.Store, host.Clock);
        await Assert.That((await inbox.DueAsync()).Count).IsEqualTo(0);
        host.Clock.Now = host.Clock.Now.AddHours(1);
        await Assert.That((await inbox.DueAsync()).Count).IsEqualTo(1);

        await host.Store.ExecuteAsync((s, _) =>
        {
            using var command = s.Connection().CreateCommand();
            command.CommandText = """
                INSERT INTO SpaceNotificationSettings (Id,ProfileId,SpaceId,"Nothing",CreatedAtUtc,UpdatedAtUtc) VALUES ($id,$profile,$space,1,'now','now');
                INSERT INTO ActivityLog (OperationId,OccurredAtUtc,Source,Action,Outcome,DetailJson) VALUES ('op2','now','sync','sync.apply','ok',$detail);
                """;
            command.Parameters.AddWithValue("$id", $"spacenotify:{profile.Id}:{space}");
            command.Parameters.AddWithValue("$profile", profile.Id);
            command.Parameters.AddWithValue("$space", space);
            command.Parameters.AddWithValue("$detail", JsonSerializer.Serialize(new[] { new RemoteTextChange("4", "diary_entry", "other", space, actor, "", mention) }));
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });
        await Assert.That(await deriver.DeriveAsync()).IsEqualTo(0);
        await Assert.That(await CountAsync(host, "SELECT count(*) FROM CollaborationNotices;")).IsEqualTo(1L);
        await Assert.That((await inbox.DueAsync()).Count).IsEqualTo(0);
    }
}
