using Chaos.Infrastructure.Migrations;
using Chaos.Infrastructure.Tests.Testing;
using Chaos.Infrastructure.Transfer;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Migrations;

public sealed class GitHubSchemaTests
{
    private static async Task<SqliteConnection> OpenAsync(long? targetVersion = null)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(TestPaths.NewDataRoot(), "github.db"),
            ForeignKeys = true,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, 13);
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None, targetVersion);
        await Execute(connection, """
            INSERT INTO Profiles(Id,DisplayName,ZoneId,Locale,DateFormat,FirstWeekday,CreatedAtUtc,UpdatedAtUtc) VALUES('p','Person','UTC','en','YMD',1,'now','now');
            INSERT INTO Spaces(Id,OwnerProfileId,Kind,Name,CreatedAtUtc,UpdatedAtUtc) VALUES('s','p',1,'Personal','now','now');
            INSERT INTO Containers(Id,SpaceId,Kind,Name,NormalizedName,Position,CreatedAtUtc,UpdatedAtUtc) VALUES('c','s',0,'Inbox','inbox',0,'now','now');
            INSERT INTO Tasks(Id,SpaceId,ContainerId,Title,Status,Position,CreatedAtUtc,UpdatedAtUtc) VALUES('t','s','c','Task',0,0,'now','now');
            INSERT INTO GitHubConnections(Id,SpaceId,ProfileId,WebBaseUrl,ApiBaseUrl,AccountId,AccountLogin,CreatedAtUtc,UpdatedAtUtc) VALUES('g','s','p','https://github.com','https://api.github.com',1,'person','now','now');
            INSERT INTO GitHubRepositories(Id,ConnectionId,RepositoryId,Owner,Name,FullName,HtmlUrl,IsPrivate,TargetContainerId,FilterJson,CreatedAtUtc,UpdatedAtUtc) VALUES('r','g',1,'person','repo','person/repo','https://github.com/person/repo',1,'c','{}','now','now');
            INSERT INTO GitHubIssueLinks(Id,RepositorySubscriptionId,ConnectionId,TaskId,IssueId,IssueNodeId,IssueNumber,HtmlUrl,BaseJson,BaseRemoteUpdatedUtc,CreatedAtUtc,UpdatedAtUtc) VALUES('l','r','g','t',1,'node',1,'https://github.com/person/repo/issues/1','{}','now','now','now');
            INSERT INTO Subtasks(Id,TaskId,Title,Status,Position,CreatedAtUtc,UpdatedAtUtc) VALUES('st','t','Subtask',0,0,'now','now');
            INSERT INTO Tags(Id,OwnerProfileId,DisplayName,NormalizedName,CreatedAtUtc,UpdatedAtUtc) VALUES('tag','p','bug','bug','now','now');
            INSERT INTO TaskTags(TaskId,TagId) VALUES('t','tag');
            INSERT INTO DiaryEntries(Id,TaskId,AuthorProfileId,EntryAtUtc,RawMarkdown,CreatedAtUtc,UpdatedAtUtc) VALUES('d','t','p','now','body','now','now');
            INSERT INTO GitHubCommentLinks(DiaryEntryId,IssueLinkId,Origin,State) VALUES('d','l',1,0);
            UPDATE GitHubIssueLinks SET LocalDirty=0,LocalDirtySinceUtc=NULL;
            """);
        return connection;
    }

    private static async Task Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    [Test]
    public async Task CategoryMigration_DefaultsExistingRepositoryToPersonal()
    {
        using var connection = await OpenAsync(20);
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        await Assert.That(await Scalar(connection, "SELECT TaskCategory FROM GitHubRepositories WHERE Id='r';")).IsEqualTo(0L);
        await Execute(connection, "UPDATE GitHubRepositories SET TaskCategory=1 WHERE Id='r';");
        await Assert.That(await Scalar(connection, "SELECT TaskCategory FROM GitHubRepositories WHERE Id='r';")).IsEqualTo(1L);
    }

    [Test]
    [Arguments("UPDATE Tasks SET Title='Changed' WHERE Id='t';")]
    [Arguments("UPDATE Tasks SET Notes='Changed' WHERE Id='t';")]
    [Arguments("UPDATE Subtasks SET Title='Changed' WHERE Id='st';")]
    [Arguments("DELETE FROM TaskTags WHERE TaskId='t';")]
    [Arguments("UPDATE DiaryEntries SET RawMarkdown='Changed' WHERE Id='d';")]
    [Arguments("UPDATE GitHubCommentLinks SET State=1 WHERE DiaryEntryId='d';")]
    public async Task DirtyTriggers_LocalChange_OnlyMarksLinkedIssues(string mutation)
    {
        using var connection = await OpenAsync();
        await Execute(connection, mutation);
        await Assert.That(await Scalar(connection, "SELECT LocalDirty FROM GitHubIssueLinks;")).IsEqualTo(1L);
        await Execute(connection, "UPDATE GitHubIssueLinks SET State=1,LocalDirty=0;" + mutation);
        await Assert.That(await Scalar(connection, "SELECT LocalDirty FROM GitHubIssueLinks;")).IsEqualTo(0L);
    }

    [Test]
    public async Task LifecycleTriggers_DeleteAndRestore_IgnoreThenRequeueLink()
    {
        using var connection = await OpenAsync();
        await Execute(connection, "UPDATE Tasks SET DeletedAtUtc='later' WHERE Id='t';");
        await Assert.That(await Scalar(connection, "SELECT State FROM GitHubIssueLinks;")).IsEqualTo(3L);
        await Assert.That(await Scalar(connection, "SELECT LocalDirty FROM GitHubIssueLinks;")).IsEqualTo(0L);
        await Execute(connection, "UPDATE Tasks SET DeletedAtUtc=NULL WHERE Id='t';");
        await Assert.That(await Scalar(connection, "SELECT State FROM GitHubIssueLinks;")).IsEqualTo(0L);
        await Assert.That(await Scalar(connection, "SELECT LocalDirty FROM GitHubIssueLinks;")).IsEqualTo(1L);
        await Execute(connection, "UPDATE GitHubIssueLinks SET State=4, LocalDirty=0; UPDATE Tasks SET DeletedAtUtc='later' WHERE Id='t'; UPDATE Tasks SET DeletedAtUtc=NULL WHERE Id='t';");
        await Assert.That(await Scalar(connection, "SELECT State FROM GitHubIssueLinks;")).IsEqualTo(4L);
        await Assert.That(await Scalar(connection, "SELECT LocalDirty FROM GitHubIssueLinks;")).IsEqualTo(0L);
    }

    [Test]
    public async Task DirtyTriggers_EngineWrite_CanClearWithinSameTransaction()
    {
        using var connection = await OpenAsync();
        using var transaction = connection.BeginTransaction();
        await Execute(connection, "UPDATE Tasks SET Title='Remote' WHERE Id='t'; UPDATE GitHubIssueLinks SET LocalDirty=0,LocalDirtySinceUtc=NULL;");
        transaction.Commit();
        await Assert.That(await Scalar(connection, "SELECT LocalDirty FROM GitHubIssueLinks;")).IsEqualTo(0L);
    }

    [Test]
    [Arguments("UPDATE GitHubConnections SET State=3;")]
    [Arguments("UPDATE GitHubConnections SET WebBaseUrl='http://github.com';")]
    [Arguments("UPDATE GitHubRepositories SET IsPrivate=2;")]
    [Arguments("UPDATE GitHubRepositories SET TaskCategory=2;")]
    [Arguments("UPDATE GitHubIssueLinks SET LocalAssigneeIntent=2;")]
    [Arguments("UPDATE GitHubIssueLinks SET ErrorAttempts=-1;")]
    [Arguments("UPDATE GitHubIssueLinks SET ConnectionId='missing';")]
    [Arguments("UPDATE GitHubRepositories SET TargetSectionId='missing';")]
    public async Task Constraints_InvalidRow_IsRejected(string mutation)
    {
        using var connection = await OpenAsync();
        await Assert.That(async () => await Execute(connection, mutation)).Throws<SqliteException>();
    }

    [Test]
    public async Task Connection_OneActivePerSpace_AllowsReplacementAfterSoftDelete()
    {
        using var connection = await OpenAsync();
        const string insert = "INSERT INTO GitHubConnections(Id,SpaceId,ProfileId,WebBaseUrl,ApiBaseUrl,AccountId,AccountLogin,CreatedAtUtc,UpdatedAtUtc) VALUES('other','s','p','https://github.com','https://api.github.com',2,'other','now','now');";
        await Assert.That(async () => await Execute(connection, insert)).Throws<SqliteException>();
        await Execute(connection, "UPDATE GitHubConnections SET DeletedAtUtc='later';" + insert);
        await Assert.That(await Scalar(connection, "SELECT count(*) FROM GitHubConnections;")).IsEqualTo(2L);
    }

    [Test]
    public async Task IssueLinks_UniqueTaskAndIssue_KeepPurgedIssueIdentity()
    {
        using var connection = await OpenAsync();
        const string insert = "INSERT INTO GitHubIssueLinks(Id,RepositorySubscriptionId,ConnectionId,TaskId,IssueId,IssueNodeId,IssueNumber,HtmlUrl,BaseJson,BaseRemoteUpdatedUtc,CreatedAtUtc,UpdatedAtUtc) VALUES('other','r','g','t',2,'node2',2,'url','{}','now','now','now');";
        await Assert.That(async () => await Execute(connection, insert)).Throws<SqliteException>();
        await Execute(connection, "UPDATE GitHubIssueLinks SET TaskId=NULL,State=3;" + insert);
        await Assert.That(async () => await Execute(connection, "UPDATE GitHubIssueLinks SET IssueId=1 WHERE Id='other';")).Throws<SqliteException>();
    }

    [Test]
    public async Task TextExports_LinkedTask_IncludesOriginUrl()
    {
        using var connection = await OpenAsync();
        var snapshot = await ExportSnapshot.ReadAsync(connection, CancellationToken.None);
        var markdown = PlainTextExport.TaskMarkdown(snapshot.Tasks.Single(), snapshot.Containers.Single());
        await Assert.That(markdown).Contains("Source: https://github.com/person/repo/issues/1");
        await Assert.That(PlainTextExport.WriteOrg(snapshot, DateTimeOffset.UtcNow)).Contains(":GITHUB_URL: https://github.com/person/repo/issues/1");
    }

    [Test]
    public async Task DumpAsync_GitHubMachineState_IsExcludedOrReset()
    {
        using var connection = await OpenAsync();
        await Execute(connection, "UPDATE GitHubIssueLinks SET IssueEtag='etag',ErrorAttempts=3,NextAttemptUtc='later'; INSERT INTO GitHubSyncRuns(Id,ConnectionId,StartedAtUtc) VALUES('run','g','now');");
        var tables = await JsonArchive.DumpAsync(connection, CancellationToken.None);
        await Assert.That(tables.Any(table => table.Name == "GitHubSyncRuns")).IsFalse();
        var links = tables.Single(table => table.Name == "GitHubIssueLinks");
        var row = links.Rows.Single();
        await Assert.That(row[links.Columns.ToList().IndexOf("IssueEtag")]).IsNull();
        await Assert.That(row[links.Columns.ToList().IndexOf("NextAttemptUtc")]).IsNull();
        await Assert.That(row[links.Columns.ToList().IndexOf("ErrorAttempts")]).IsEqualTo((object)0L);
    }
}
