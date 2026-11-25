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
    public async Task Migrate049_AddsTheTimeBlockSettings_TheEventTableAndTheDeviceState()
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 48);
        Execute(connection, """
            INSERT INTO Profiles (Id,DisplayName,ZoneId,Locale,DateFormat,FirstWeekday,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('person','Person','UTC','en','YMD',1,'now','now');
            INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ('personal','person',1,'Personal','now','now');
            INSERT INTO Containers (Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ('inbox','personal',0,'Inbox','inbox',0,'now','now');
            INSERT INTO Tasks (Id,SpaceId,ContainerId,Title,Status,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ('task','personal','inbox','Task',0,0,'now','now');
            """);
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        SyncTriggerInstaller.Install(connection);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);

        // The settings are profile columns with off/busy-free defaults: time blocking is off and shows titles until turned on.
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_table_info('Profiles') WHERE name LIKE 'TimeBlock%';"))).IsEqualTo(6L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT TimeBlockEnabled FROM Profiles WHERE Id='person';"))).IsEqualTo(0L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT TimeBlockShowTitles FROM Profiles WHERE Id='person';"))).IsEqualTo(1L);
        await Assert.That(Scalar(connection, "SELECT TimeBlockAccountId FROM Profiles WHERE Id='person';") is DBNull).IsTrue();

        // The event rows sync (three catalog triggers, inserts pend both groups); the device state does not.
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Sync_TimeBlockEvents_%';"))).IsEqualTo(3L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Sync_TimeBlockState_%';"))).IsEqualTo(0L);
        Execute(connection, "INSERT INTO TimeBlockEvents (TaskId,EventId,ETag,PushedHash,LastSyncedAtUtc,Version,CreatedAtUtc,UpdatedAtUtc) VALUES ('task','cb0123456789abcdefghijklmnopqr','\"e1\"','abc','now',1,'now','now');");
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending WHERE Kind='time_block_event' AND EntityKey='task' AND GroupName IN ('create','state');"))).IsEqualTo(2L);

        // Shapes Google needs are enforced here too: the event id length and the single state row.
        var shortId = false;
        try { Execute(connection, "UPDATE TimeBlockEvents SET EventId='cb';"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { shortId = true; }
        await Assert.That(shortId).IsTrue();
        var secondState = false;
        try { Execute(connection, "INSERT INTO TimeBlockState (Id,CalendarId) VALUES (2,'x');"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { secondState = true; }
        await Assert.That(secondState).IsTrue();
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);
    }

    [Test]
    public async Task Migrate048_AddsPullRequestLinks_AttributionAndAssigneeDirtyTriggers()
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 47);
        Execute(connection, """
            INSERT INTO Profiles (Id,DisplayName,ZoneId,Locale,DateFormat,FirstWeekday,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('person','Person','UTC','en','YMD',1,'now','now');
            INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ('team','person',2,'Team','now','now');
            INSERT INTO SpaceMembers (Id,SpaceId,MemberId,MemberKind,DisplayName,GitHubLogin,Role,JoinedAtUtc)
                VALUES ('member','team','person',1,'Person','person',1,'now');
            INSERT INTO Containers (Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ('inbox','team',0,'Inbox','inbox',0,'now','now');
            INSERT INTO Tasks (Id,SpaceId,ContainerId,Title,Status,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ('task','team','inbox','Task',0,0,'now','now');
            INSERT INTO TaskAssignees (TaskId,MemberId,AddedAtUtc) VALUES ('task','person','now');
            INSERT INTO GitHubConnections (Id,SpaceId,ProfileId,WebBaseUrl,ApiBaseUrl,AccountId,AccountLogin,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('connection','team','person','https://github.com','https://api.github.com',1,'person','now','now');
            INSERT INTO GitHubRepositories (Id,ConnectionId,RepositoryId,Owner,Name,FullName,HtmlUrl,IsPrivate,TargetContainerId,FilterJson,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('repo','connection',10,'person','repo','person/repo','https://github.com/person/repo',0,'inbox','{"v":1}','now','now');
            INSERT INTO GitHubIssueLinks (Id,RepositorySubscriptionId,ConnectionId,TaskId,IssueId,IssueNodeId,IssueNumber,HtmlUrl,BaseJson,BaseRemoteUpdatedUtc,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('link','repo','connection','task',1,'I_1',1,'https://github.com/person/repo/issues/1','{"v":1,"payload":{}}','now','now','now');
            """);
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        SyncTriggerInstaller.Install(connection);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Sync_GitHubPullRequestLinks_%';"))).IsEqualTo(3L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_table_info('GitHubCommentLinks') WHERE name='Attribution';"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'GitHubDirtyAssignee%';"))).IsEqualTo(3L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM GitHubConnections;"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE name='UX_GitHubConnections_ActivePerSpace';"))).IsEqualTo(1L);
        // The connection's ProfileId names an owner who may have no Profiles row on this device (a member's).
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_list('GitHubConnections') WHERE \"from\"='ProfileId';"))).IsEqualTo(0L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_list('GitHubConnections') WHERE \"from\"='SpaceId';"))).IsEqualTo(1L);
        Execute(connection, "INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) SELECT 'team2',Id,2,'Team 2','2026-11-10T09:00:00Z','2026-11-10T09:00:00Z' FROM Profiles LIMIT 1; INSERT INTO GitHubConnections (Id,SpaceId,ProfileId,WebBaseUrl,ApiBaseUrl,AccountId,AccountLogin,RunnerDeviceId,CreatedAtUtc,UpdatedAtUtc) VALUES ('other','team2','bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb','https://github.com','https://api.github.com',2,'other','device','now','now');");
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);

        // Assigning or unassigning a member marks the linked issue dirty on the runner, as a tag change does.
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT LocalDirty FROM GitHubIssueLinks WHERE Id='link';"))).IsEqualTo(0L);
        Execute(connection, "UPDATE TaskAssignees SET DeletedAtUtc='now' WHERE TaskId='task';");
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT LocalDirty FROM GitHubIssueLinks WHERE Id='link';"))).IsEqualTo(1L);
        Execute(connection, "UPDATE TaskAssignees SET DeletedAtUtc=NULL WHERE TaskId='task';");
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending WHERE Kind='github_pull_request_link';"))).IsEqualTo(0L);

        Execute(connection, """
            INSERT INTO GitHubPullRequestLinks (IssueLinkId,PullRequestId,RepositoryFullName,Number,Title,State,ReviewState,Url,RefreshedAtUtc)
                VALUES ('link','PR_1','person/repo',1,'Fix #1',2,1,'https://github.com/person/repo/pull/1','now');
            """);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending WHERE Kind='github_pull_request_link' AND GroupName IN ('create','state');"))).IsEqualTo(2L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);
        var refused = false;
        try { Execute(connection, "UPDATE GitHubPullRequestLinks SET State=4;"); }
        catch (Microsoft.Data.Sqlite.SqliteException) { refused = true; }
        await Assert.That(refused).IsTrue();
    }

    [Test]
    public async Task Migrate050_AddsAgentsAndClaims_AndRebuildsTheActivityLogKeepingItsSequence()
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 49);
        Execute(connection, """
            INSERT INTO Profiles (Id,DisplayName,ZoneId,Locale,DateFormat,FirstWeekday,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('person','Person','UTC','en','YMD',1,'now','now');
            INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ('space','person',1,'Personal','now','now');
            INSERT INTO Containers (Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ('inbox','space',0,'Inbox','inbox',0,'now','now');
            INSERT INTO Tasks (Id,SpaceId,ContainerId,Title,Status,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ('task','space','inbox','Task',0,0,'now','now');
            INSERT INTO UndoEntries (Id,ProfileId,OperationId,Label,InversePayload,ExpectedVersions,AppliedAtUtc) VALUES ('undo','person','op','Edit task','{}','{}','now');
            INSERT INTO ActivityLog (Seq,OperationId,OccurredAtUtc,Source,Action,Outcome) VALUES (1,'op1','now','user','tasks.create','ok');
            INSERT INTO ActivityLog (Seq,OperationId,OccurredAtUtc,Source,Action,Outcome) VALUES (7,'op7','now','sync','sync.apply','ok');
            INSERT INTO ActivityLogEntities (Seq,Kind,EntityKey,Op,Version) VALUES (7,'task','task','upsert',3);
            DELETE FROM ActivityLog WHERE Seq = 1;
            """);
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        SyncTriggerInstaller.Install(connection);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT Seq FROM ActivityLog;"))).IsEqualTo(7L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM ActivityLogEntities WHERE Seq = 7 AND EntityKey = 'task';"))).IsEqualTo(1L);
        // Rows 2 to 6 were pruned before the migration: the counter still stands past them.
        Execute(connection, "INSERT INTO ActivityLog (OperationId,OccurredAtUtc,Source,Action,Outcome,ActorId) VALUES ('op8','now','agent','agent.claim_task','ok','agent:x');");
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT Seq FROM ActivityLog WHERE OperationId = 'op8';"))).IsEqualTo(8L);
        await Assert.That((string)Scalar(connection, "SELECT ActorId FROM ActivityLog WHERE OperationId = 'op8';")!).IsEqualTo("agent:x");
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM Tasks WHERE Id='task' AND ClaimedById IS NULL AND ClaimExpiresAtUtc IS NULL AND AgentReady = 0;"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_table_info('UndoEntries') WHERE name = 'ActorId';"))).IsEqualTo(1L);
        foreach (var table in new[] { "LocalAgents", "LocalAgentAccess", "LocalAgentTokens", "TaskProposals" })
            await Assert.That(Convert.ToInt64(Scalar(connection, $"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='{table}';"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending;"))).IsEqualTo(0L);
        Execute(connection, """
            INSERT INTO LocalAgents (Id,Name,Version,CreatedAtUtc,UpdatedAtUtc) VALUES ('agent','Build bot',1,'now','now');
            INSERT INTO LocalAgentAccess (AgentId,ContainerId,GrantedAtUtc,Version,CreatedAtUtc,UpdatedAtUtc) VALUES ('agent','inbox','now',1,'now','now');
            """);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending WHERE Kind='local_agent' AND EntityKey='agent' AND GroupName IN ('create','name');"))).IsEqualTo(2L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending WHERE Kind='local_agent_access' AND EntityKey='agent|inbox' AND GroupName='create';"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Sync_TaskProposals_%';"))).IsEqualTo(3L);
    }

    [Test]
    [Arguments("DELETE FROM ActivityLog;")]
    [Arguments("DELETE FROM ActivityLog WHERE Seq > 4;")]
    public async Task Migrate050_KeepsTheActivityCounter_WhenTheNewestRowsOrTheWholeLogWerePruned(string prune)
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 49);
        Execute(connection, "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 9) "
            + "INSERT INTO ActivityLog (OperationId,OccurredAtUtc,Source,Action,Outcome) SELECT 'op' || i,'now','user','tasks.create','ok' FROM n;");
        Execute(connection, prune);
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);

        // A reader's cursor may stand at 9: the next row must still come after it.
        Execute(connection, "INSERT INTO ActivityLog (OperationId,OccurredAtUtc,Source,Action,Outcome) VALUES ('next','now','user','tasks.create','ok');");
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT Seq FROM ActivityLog WHERE OperationId = 'next';"))).IsEqualTo(10L);
    }

    [Test]
    public async Task Migrate050_AgentGrantsAndTokens_DoNotHoldTheirListOrAgentFromBeingPurged()
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        Execute(connection, """
            INSERT INTO LocalAgentAccess (AgentId,ContainerId,GrantedAtUtc,DeletedAtUtc,Version,CreatedAtUtc,UpdatedAtUtc) VALUES ('gone','purged-list','now','now',2,'now','now');
            INSERT INTO LocalAgentTokens (Id,AgentId,Name,TokenHash,CreatedAtUtc,RevokedAtUtc) VALUES ('token','gone','Claude',printf('%064d',0),'now','now');
            """);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_list('LocalAgentAccess');"))).IsEqualTo(0L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_list('LocalAgentTokens');"))).IsEqualTo(0L);
    }

    [Test]
    public async Task Migrate047_AddsStandupsAsASyncedTableOfSharedSpaces()
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 46);
        Execute(connection, """
            INSERT INTO Profiles (Id,DisplayName,ZoneId,Locale,DateFormat,FirstWeekday,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('person','Person','UTC','en','YMD',1,'now','now');
            INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ('space','person',1,'Personal','now','now');
            INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ('team','other',2,'Team','now','now');
            """);
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        SyncTriggerInstaller.Install(connection);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Sync_SpaceUpdates_%';"))).IsEqualTo(3L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending;"))).IsEqualTo(0L);
        Execute(connection, """
            INSERT INTO SpaceUpdates (Id,SpaceId,AuthorMemberId,ForDate,RawMarkdown,Version,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('standup','team','person','2026-11-23','## Done',1,'now','now');
            UPDATE SpaceUpdates SET RawMarkdown='## Done\n- Ship', EditedAtUtc='now', Version=2 WHERE Id='standup';
            """);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending WHERE Kind='space_update' AND GroupName IN ('create','entry');"))).IsEqualTo(2L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);
    }

    [Test]
    public async Task Migrate046_AddsDelegationTablesAndLinksExistingFollowUps()
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 45);
        Execute(connection, """
            INSERT INTO Profiles (Id,DisplayName,ZoneId,Locale,DateFormat,FirstWeekday,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('person','Person','UTC','en','YMD',1,'now','now');
            INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES ('space','person',1,'Personal','now','now');
            INSERT INTO Containers (Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ('inbox','space',0,'Inbox','inbox',0,'now','now');
            INSERT INTO Tasks (Id,SpaceId,ContainerId,Title,Status,Position,CreatedAtUtc,UpdatedAtUtc) VALUES ('task','space','inbox','Task',0,0,'now','now');
            INSERT INTO FollowUps (Id,TaskId,ProfileId,Kind,FixedAtUtc,Version,CreatedAtUtc,UpdatedAtUtc) VALUES ('follow','task','person',0,'2026-11-24T09:00:00.0000000+00:00',1,'now','now');
            """);
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        SyncTriggerInstaller.Install(connection);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM FollowUps WHERE Id='follow' AND DelegationId IS NULL;"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_Sync_TaskDelegations_%';"))).IsEqualTo(3L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending;"))).IsEqualTo(0L);
        Execute(connection, """
            INSERT INTO TaskDelegations (Id,TaskId,FromMemberId,ToMemberId,Message,State,StateChangedAtUtc,Version,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('delegation','task','person','other','Please',0,'now',1,'now','now');
            INSERT INTO Nudges (Id,TaskId,DelegationId,FromMemberId,ToMemberId,Text,SentAtUtc,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('nudge','task','delegation','person','other','Any news?','now','now','now');
            UPDATE FollowUps SET DelegationId='delegation' WHERE Id='follow';
            """);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending WHERE Kind='task_delegation' AND GroupName IN ('create','brief','state');"))).IsEqualTo(3L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending WHERE Kind='nudge' AND GroupName='record';"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM SyncPending WHERE Kind='follow_up' AND GroupName='delegation';"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pragma_foreign_key_check;"))).IsEqualTo(0L);
    }

    [Test]
    public async Task Migrate043_PreservesPersonalAlertPreferencesAndQueuesThemForSync()
    {
        using var connection = Open();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 42);
        Execute(connection, """
            INSERT INTO Profiles (Id,DisplayName,ZoneId,Locale,DateFormat,FirstWeekday,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('person','Person','UTC','en','YMD',1,'now','now');
            INSERT INTO Spaces (Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('personal','person',1,'Personal','now','now'),('team','person',2,'Team','now','now');
            INSERT INTO SavedViews (Id,SpaceId,Name,DefinitionJson,LayoutJson,AlertsEnabled,CreatedAtUtc,UpdatedAtUtc)
                VALUES ('on','personal','On','{}','{}',1,'now','now'),('off','personal','Off','{}','{}',0,'now','now'),
                    ('shared','team','Shared','{}','{}',1,'now','now');
            """);
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM ViewAlertSettings WHERE Id='viewalert:person:on' AND Enabled=1;"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM ViewAlertSettings WHERE Id='viewalert:person:off' AND Enabled=0;"))).IsEqualTo(1L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM ViewAlertSettings WHERE ViewId='shared';"))).IsEqualTo(0L);
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT COUNT(*) FROM SyncPending WHERE Kind='view_alert_setting';"))).IsEqualTo(4L);
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
        await Assert.That(Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='trigger' AND name GLOB 'TR_Sync_Spaces_*';"))).IsEqualTo(3L);
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
