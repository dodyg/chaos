using Chaos.Infrastructure.Migrations;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.Testing;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Migrations;

public sealed class SharingMigrationTests
{
    private static SqliteConnection Open()
    {
        var path = Path.Combine(TestPaths.NewDataRoot(), "chaos.db");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static List<string> HandwrittenTriggers(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name || ':' || sql FROM sqlite_master WHERE type='trigger' AND name NOT LIKE 'TR_Sync_%' AND tbl_name IN ('Spaces','DiaryEntries','TaskTags','SubtaskTags','ScratchPadTags','PageTags','CalendarEventLinks','ReminderRules','WaitingDetails','FollowUps','DayPlanItems','WeekGoalTasks','WeekAssignments','ImportedItems','WeekGoals') ORDER BY name;";
        using var reader = command.ExecuteReader();
        var triggers = new List<string>();
        while (reader.Read()) triggers.Add(reader.GetString(0));
        return triggers;
    }

    [Test]
    public async Task Migrate040_PreservesBoardSettingsAndCapturesAssignments()
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 39);
        Execute(connection, """
            INSERT INTO Spaces (Id, OwnerProfileId, Kind, Name, CreatedAtUtc, UpdatedAtUtc) VALUES ('space','owner',2,'Shared','now','now');
            INSERT INTO Containers (Id, SpaceId, Kind, Name, NormalizedName, Position, CreatedAtUtc, UpdatedAtUtc) VALUES ('container','space',1,'List','list',0,'now','now');
            INSERT INTO Tasks (Id, SpaceId, ContainerId, Title, Status, Position, CreatedAtUtc, UpdatedAtUtc) VALUES ('task','space','container','Task',0,0,'now','now');
            INSERT INTO BoardSettings (Id,TargetKind,TargetId,GroupBy,SwimlaneBy,CreatedAtUtc,UpdatedAtUtc) VALUES ('board',1,'container',2,4,'now','now');
            """);
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        SyncTriggerInstaller.Install(connection);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT AssignMode FROM Tasks WHERE Id='task';"))).IsEqualTo(0L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM BoardSettings WHERE Id='board' AND GroupBy=2 AND SwimlaneBy=4;"))).IsEqualTo(1L);
        Execute(connection, "UPDATE BoardSettings SET GroupBy=4, SwimlaneBy=5 WHERE Id='board'; INSERT INTO TaskAssignees (TaskId,MemberId,AddedAtUtc) VALUES ('task','person','now'); UPDATE Tasks SET AssignMode=1 WHERE Id='task';");
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending WHERE Kind='task_assignee';"))).IsGreaterThan(0L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending WHERE Kind='task' AND GroupName='assignment';"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);
    }
    [Test]
    public async Task Migrate039_ExistingRemoteState_SeedsOnlyAccountStreamAndRetainsBytes()
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 38);
        Execute(connection, """
            UPDATE SyncState SET AccountId='account', Enabled=1 WHERE Id=1;
            INSERT INTO Spaces (Id, OwnerProfileId, Kind, Name, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('personal','owner',1,'Personal','now','now'), ('shared','owner',2,'Shared','now','now');
            INSERT INTO Attachments (Id,SpaceId,OwnerKind,OwnerId,FileName,OriginalFileName,MediaType,ByteSize,Sha256,CreatedAtUtc,UpdatedAtUtc)
            VALUES ('personal-file','personal',1,'task','a','a','text/plain',1,printf('%064d',1),'now','now'),
                   ('shared-file','shared',1,'task','b','b','text/plain',1,printf('%064d',2),'now','now');
            INSERT INTO AttachmentLocalState (Sha256,State,UpdatedAtUtc,Remote,NextTryUtc,Failures)
            VALUES (printf('%064d',1),0,'now',1,'later',2), (printf('%064d',2),0,'now',1,NULL,0);
            """);
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM AttachmentStreamState;"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM AttachmentStreamState WHERE StreamId='account' AND Sha256=printf('%064d',1) AND Remote=0 AND NextTryUtc IS NULL AND Failures=0;"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM AttachmentLocalState WHERE State=0;"))).IsEqualTo(2L);
        Execute(connection, "DELETE FROM AttachmentLocalState WHERE Sha256=printf('%064d',1);");
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM AttachmentStreamState;"))).IsEqualTo(0L);
    }

    [Test]
    public async Task Migrate038_PopulatedParents_PreservesDependentsAndTrigger()
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 37);
        Execute(connection, """
            INSERT INTO Profiles (Id, DisplayName, ZoneId, Locale, DateFormat, FirstWeekday, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','Person','UTC','en','YMD',1,'now','now');
            INSERT INTO Spaces (Id, OwnerProfileId, Kind, Name, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('space','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',1,'Personal','now','now');
            INSERT INTO Containers (Id, SpaceId, Kind, Name, NormalizedName, Position, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('container','space',0,'Inbox','inbox',0,'now','now');
            INSERT INTO Tasks (Id, SpaceId, ContainerId, Title, Status, Position, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('task','space','container','Task',0,0,'now','now');
            INSERT INTO Tasks (Id, SpaceId, ContainerId, Title, Status, Position, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('overlay-target','space','container','Other',0,1,'now','now');
            INSERT INTO Tags (Id,OwnerProfileId,DisplayName,NormalizedName,CreatedAtUtc,UpdatedAtUtc)
            VALUES ('tag','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','Tag','tag','now','now');
            INSERT INTO TaskTags (TaskId,TagId,DeletedAtUtc) VALUES ('overlay-target','tag','now');
            INSERT INTO DiaryEntries (Id, TaskId, AuthorProfileId, EntryAtUtc, RawMarkdown, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('entry','task','aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa','now','text','now','now');
            INSERT INTO DiaryRevisions (EntryId, Revision, RawMarkdown, RecordedAtUtc) VALUES ('entry',1,'text','now');
            """);
        var trigger = Scalar(connection, "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='GitHubDirtyDiary';");
        var triggers = HandwrittenTriggers(connection);
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        SyncTriggerInstaller.Install(connection);
        await Assert.That(HandwrittenTriggers(connection)).IsEquivalentTo(triggers);
        await Assert.That(Scalar(connection, "SELECT sql FROM sqlite_master WHERE type='trigger' AND name='GitHubDirtyDiary';")).IsEqualTo(trigger);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM DiaryRevisions WHERE EntryId='entry';"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "PRAGMA foreign_keys;"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Sync_Spaces_%';"))).IsEqualTo(3L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Sync_SpaceMembers_%';"))).IsEqualTo(0L);
        Execute(connection, "UPDATE Spaces SET ColorKey='blue' WHERE Id='space';");
        // Client schema 4 captures the space's sidebar look; schema 3 left the group to newer clients.
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending WHERE Kind='space' AND GroupName='look';"))).IsEqualTo(1L);
        Execute(connection, "DELETE FROM Tasks WHERE Id='overlay-target';");
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM TaskTags WHERE TaskId='overlay-target' AND DeletedAtUtc='now';"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);
        Execute(connection, "INSERT INTO Spaces (Id, OwnerProfileId, Kind, Name, CreatedAtUtc, UpdatedAtUtc) VALUES ('shared','bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',2,'Shared','now','now');");
    }

    [Test]
    public async Task Migrate038_InvalidLegacyAuthor_RollsBackAndRestoresForeignKeys()
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 37);
        Execute(connection, """
            INSERT INTO Profiles (Id, DisplayName, ZoneId, Locale, DateFormat, FirstWeekday, CreatedAtUtc, UpdatedAtUtc)
            VALUES ('legacy','Person','UTC','en','YMD',1,'now','now');
            INSERT INTO Spaces (Id, OwnerProfileId, Kind, Name, CreatedAtUtc, UpdatedAtUtc) VALUES ('space','legacy',1,'Personal','now','now');
            INSERT INTO Containers (Id, SpaceId, Kind, Name, NormalizedName, Position, CreatedAtUtc, UpdatedAtUtc) VALUES ('container','space',0,'Inbox','inbox',0,'now','now');
            INSERT INTO Tasks (Id, SpaceId, ContainerId, Title, Status, Position, CreatedAtUtc, UpdatedAtUtc) VALUES ('task','space','container','Task',0,0,'now','now');
            INSERT INTO DiaryEntries (Id, TaskId, AuthorProfileId, EntryAtUtc, RawMarkdown, CreatedAtUtc, UpdatedAtUtc) VALUES ('entry','task','legacy','now','text','now','now');
            """);
        await Assert.That(async () => await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None)).Throws<SqliteException>();
        await Assert.That(Convert.ToInt64(Scalar(connection, "PRAGMA foreign_keys;"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT MAX(Version) FROM SchemaMigrations;"))).IsEqualTo(37L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM DiaryEntries WHERE Id='entry';"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);
    }

    [Test]
    public async Task Migrate038_ExistingForeignKeyViolation_RollsBack()
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 37);
        Execute(connection, "PRAGMA foreign_keys=OFF; INSERT INTO Containers (Id, SpaceId, Kind, Name, NormalizedName, Position, CreatedAtUtc, UpdatedAtUtc) VALUES ('orphan','missing',0,'Inbox','inbox',0,'now','now'); PRAGMA foreign_keys=ON;");
        await Assert.That(async () => await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None)).Throws<InvalidOperationException>();
        await Assert.That(Convert.ToInt64(Scalar(connection, "PRAGMA foreign_keys;"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT MAX(Version) FROM SchemaMigrations;"))).IsEqualTo(37L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM Containers WHERE Id='orphan';"))).IsEqualTo(1L);
    }
}
