using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>GitHub bookkeeping on the caller's connection and transaction; no network access.</summary>
public sealed class SqliteGitHubStore(SqliteConnection connection) : IGitHubStore
{
    private static GitHubConnectionRecord ReadConnection(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("Id")),
        SpaceId = reader.GetString(reader.GetOrdinal("SpaceId")),
        ProfileId = reader.GetString(reader.GetOrdinal("ProfileId")),
        WebBaseUrl = reader.GetString(reader.GetOrdinal("WebBaseUrl")),
        ApiBaseUrl = reader.GetString(reader.GetOrdinal("ApiBaseUrl")),
        AccountId = reader.GetInt64(reader.GetOrdinal("AccountId")),
        AccountLogin = reader.GetString(reader.GetOrdinal("AccountLogin")),
        State = reader.GetInt32(reader.GetOrdinal("State")),
        PostDiaryByDefault = reader.GetInt64(reader.GetOrdinal("PostDiaryByDefault")) != 0,
        CreateLabelsForTags = reader.GetInt64(reader.GetOrdinal("CreateLabelsForTags")) != 0,
        RateLimitResetUtc = reader.IsDBNull(reader.GetOrdinal("RateLimitResetUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("RateLimitResetUtc"))),
        LastSyncStartedUtc = reader.IsDBNull(reader.GetOrdinal("LastSyncStartedUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("LastSyncStartedUtc"))),
        LastSyncCompletedUtc = reader.IsDBNull(reader.GetOrdinal("LastSyncCompletedUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("LastSyncCompletedUtc"))),
        LastErrorKind = reader.IsDBNull(reader.GetOrdinal("LastErrorKind")) ? null : reader.GetInt32(reader.GetOrdinal("LastErrorKind")),
        LastErrorMessage = reader.IsDBNull(reader.GetOrdinal("LastErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("LastErrorMessage")),
        Version = reader.GetInt64(reader.GetOrdinal("Version")),
        CreatedAtUtc = StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("CreatedAtUtc")))!.Value,
        UpdatedAtUtc = StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("UpdatedAtUtc")))!.Value,
        DeletedAtUtc = reader.IsDBNull(reader.GetOrdinal("DeletedAtUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("DeletedAtUtc"))),
    };

    public async Task AddConnectionAsync(GitHubConnectionRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO GitHubConnections (Id, SpaceId, ProfileId, WebBaseUrl, ApiBaseUrl, AccountId, AccountLogin, State, PostDiaryByDefault, CreateLabelsForTags, RateLimitResetUtc, LastSyncStartedUtc, LastSyncCompletedUtc, LastErrorKind, LastErrorMessage, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc) VALUES ($Id, $SpaceId, $ProfileId, $WebBaseUrl, $ApiBaseUrl, $AccountId, $AccountLogin, $State, $PostDiaryByDefault, $CreateLabelsForTags, $RateLimitResetUtc, $LastSyncStartedUtc, $LastSyncCompletedUtc, $LastErrorKind, $LastErrorMessage, $Version, $CreatedAtUtc, $UpdatedAtUtc, $DeletedAtUtc);";
        command.Parameters.AddWithValue("$Id", row.Id);
        command.Parameters.AddWithValue("$SpaceId", row.SpaceId);
        command.Parameters.AddWithValue("$ProfileId", row.ProfileId);
        command.Parameters.AddWithValue("$WebBaseUrl", row.WebBaseUrl);
        command.Parameters.AddWithValue("$ApiBaseUrl", row.ApiBaseUrl);
        command.Parameters.AddWithValue("$AccountId", row.AccountId);
        command.Parameters.AddWithValue("$AccountLogin", row.AccountLogin);
        command.Parameters.AddWithValue("$State", row.State);
        command.Parameters.AddWithValue("$PostDiaryByDefault", row.PostDiaryByDefault);
        command.Parameters.AddWithValue("$CreateLabelsForTags", row.CreateLabelsForTags);
        command.Parameters.AddWithValue("$RateLimitResetUtc", row.RateLimitResetUtc is { } RateLimitResetUtc ? StorageFormats.Instant(RateLimitResetUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastSyncStartedUtc", row.LastSyncStartedUtc is { } LastSyncStartedUtc ? StorageFormats.Instant(LastSyncStartedUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastSyncCompletedUtc", row.LastSyncCompletedUtc is { } LastSyncCompletedUtc ? StorageFormats.Instant(LastSyncCompletedUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorKind", (object?)row.LastErrorKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorMessage", (object?)row.LastErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$Version", row.Version);
        command.Parameters.AddWithValue("$CreatedAtUtc", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$UpdatedAtUtc", StorageFormats.Instant(row.UpdatedAtUtc));
        command.Parameters.AddWithValue("$DeletedAtUtc", row.DeletedAtUtc is { } DeletedAtUtc ? StorageFormats.Instant(DeletedAtUtc) : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<GitHubConnectionRecord?> GetConnectionAsync(string id, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT * FROM GitHubConnections WHERE Id=$id;", ReadConnection, ct, ("$id", id));
        return rows.SingleOrDefault();
    }

    public async Task<GitHubConnectionRecord?> UpdateConnectionAsync(GitHubConnectionRecord row, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE GitHubConnections SET SpaceId=$SpaceId, ProfileId=$ProfileId, WebBaseUrl=$WebBaseUrl, ApiBaseUrl=$ApiBaseUrl, AccountId=$AccountId, AccountLogin=$AccountLogin, State=$State, PostDiaryByDefault=$PostDiaryByDefault, CreateLabelsForTags=$CreateLabelsForTags, RateLimitResetUtc=$RateLimitResetUtc, LastSyncStartedUtc=$LastSyncStartedUtc, LastSyncCompletedUtc=$LastSyncCompletedUtc, LastErrorKind=$LastErrorKind, LastErrorMessage=$LastErrorMessage, UpdatedAtUtc=$UpdatedAtUtc, DeletedAtUtc=$DeletedAtUtc, Version=Version+1 WHERE Id=$Id AND Version=$expected;";
        command.Parameters.AddWithValue("$Id", row.Id);
        command.Parameters.AddWithValue("$SpaceId", row.SpaceId);
        command.Parameters.AddWithValue("$ProfileId", row.ProfileId);
        command.Parameters.AddWithValue("$WebBaseUrl", row.WebBaseUrl);
        command.Parameters.AddWithValue("$ApiBaseUrl", row.ApiBaseUrl);
        command.Parameters.AddWithValue("$AccountId", row.AccountId);
        command.Parameters.AddWithValue("$AccountLogin", row.AccountLogin);
        command.Parameters.AddWithValue("$State", row.State);
        command.Parameters.AddWithValue("$PostDiaryByDefault", row.PostDiaryByDefault);
        command.Parameters.AddWithValue("$CreateLabelsForTags", row.CreateLabelsForTags);
        command.Parameters.AddWithValue("$RateLimitResetUtc", row.RateLimitResetUtc is { } RateLimitResetUtc ? StorageFormats.Instant(RateLimitResetUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastSyncStartedUtc", row.LastSyncStartedUtc is { } LastSyncStartedUtc ? StorageFormats.Instant(LastSyncStartedUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastSyncCompletedUtc", row.LastSyncCompletedUtc is { } LastSyncCompletedUtc ? StorageFormats.Instant(LastSyncCompletedUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorKind", (object?)row.LastErrorKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorMessage", (object?)row.LastErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$Version", row.Version);
        command.Parameters.AddWithValue("$CreatedAtUtc", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$UpdatedAtUtc", StorageFormats.Instant(row.UpdatedAtUtc));
        command.Parameters.AddWithValue("$DeletedAtUtc", row.DeletedAtUtc is { } DeletedAtUtc ? StorageFormats.Instant(DeletedAtUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? row with { Version = expectedVersion + 1 } : null;
    }

    private static GitHubRepositoryRecord ReadRepository(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("Id")),
        ConnectionId = reader.GetString(reader.GetOrdinal("ConnectionId")),
        RepositoryId = reader.GetInt64(reader.GetOrdinal("RepositoryId")),
        Owner = reader.GetString(reader.GetOrdinal("Owner")),
        Name = reader.GetString(reader.GetOrdinal("Name")),
        FullName = reader.GetString(reader.GetOrdinal("FullName")),
        HtmlUrl = reader.GetString(reader.GetOrdinal("HtmlUrl")),
        IsPrivate = reader.GetInt64(reader.GetOrdinal("IsPrivate")) != 0,
        TargetContainerId = reader.GetString(reader.GetOrdinal("TargetContainerId")),
        TargetSectionId = reader.IsDBNull(reader.GetOrdinal("TargetSectionId")) ? null : reader.GetString(reader.GetOrdinal("TargetSectionId")),
        FilterJson = reader.GetString(reader.GetOrdinal("FilterJson")),
        MilestonesAsProjects = reader.GetInt64(reader.GetOrdinal("MilestonesAsProjects")) != 0,
        State = reader.GetInt32(reader.GetOrdinal("State")),
        IssuesCursorUtc = reader.IsDBNull(reader.GetOrdinal("IssuesCursorUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("IssuesCursorUtc"))),
        CommentsCursorUtc = reader.IsDBNull(reader.GetOrdinal("CommentsCursorUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("CommentsCursorUtc"))),
        IssuesEtag = reader.IsDBNull(reader.GetOrdinal("IssuesEtag")) ? null : reader.GetString(reader.GetOrdinal("IssuesEtag")),
        CommentsEtag = reader.IsDBNull(reader.GetOrdinal("CommentsEtag")) ? null : reader.GetString(reader.GetOrdinal("CommentsEtag")),
        LabelsEtag = reader.IsDBNull(reader.GetOrdinal("LabelsEtag")) ? null : reader.GetString(reader.GetOrdinal("LabelsEtag")),
        MilestonesEtag = reader.IsDBNull(reader.GetOrdinal("MilestonesEtag")) ? null : reader.GetString(reader.GetOrdinal("MilestonesEtag")),
        InitialImportDoneUtc = reader.IsDBNull(reader.GetOrdinal("InitialImportDoneUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("InitialImportDoneUtc"))),
        LastSyncedUtc = reader.IsDBNull(reader.GetOrdinal("LastSyncedUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("LastSyncedUtc"))),
        LastErrorKind = reader.IsDBNull(reader.GetOrdinal("LastErrorKind")) ? null : reader.GetInt32(reader.GetOrdinal("LastErrorKind")),
        LastErrorMessage = reader.IsDBNull(reader.GetOrdinal("LastErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("LastErrorMessage")),
        Version = reader.GetInt64(reader.GetOrdinal("Version")),
        CreatedAtUtc = StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("CreatedAtUtc")))!.Value,
        UpdatedAtUtc = StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("UpdatedAtUtc")))!.Value,
        DeletedAtUtc = reader.IsDBNull(reader.GetOrdinal("DeletedAtUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("DeletedAtUtc"))),
    };

    public async Task AddRepositoryAsync(GitHubRepositoryRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO GitHubRepositories (Id, ConnectionId, RepositoryId, Owner, Name, FullName, HtmlUrl, IsPrivate, TargetContainerId, TargetSectionId, FilterJson, MilestonesAsProjects, State, IssuesCursorUtc, CommentsCursorUtc, IssuesEtag, CommentsEtag, LabelsEtag, MilestonesEtag, InitialImportDoneUtc, LastSyncedUtc, LastErrorKind, LastErrorMessage, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc) VALUES ($Id, $ConnectionId, $RepositoryId, $Owner, $Name, $FullName, $HtmlUrl, $IsPrivate, $TargetContainerId, $TargetSectionId, $FilterJson, $MilestonesAsProjects, $State, $IssuesCursorUtc, $CommentsCursorUtc, $IssuesEtag, $CommentsEtag, $LabelsEtag, $MilestonesEtag, $InitialImportDoneUtc, $LastSyncedUtc, $LastErrorKind, $LastErrorMessage, $Version, $CreatedAtUtc, $UpdatedAtUtc, $DeletedAtUtc);";
        command.Parameters.AddWithValue("$Id", row.Id);
        command.Parameters.AddWithValue("$ConnectionId", row.ConnectionId);
        command.Parameters.AddWithValue("$RepositoryId", row.RepositoryId);
        command.Parameters.AddWithValue("$Owner", row.Owner);
        command.Parameters.AddWithValue("$Name", row.Name);
        command.Parameters.AddWithValue("$FullName", row.FullName);
        command.Parameters.AddWithValue("$HtmlUrl", row.HtmlUrl);
        command.Parameters.AddWithValue("$IsPrivate", row.IsPrivate);
        command.Parameters.AddWithValue("$TargetContainerId", row.TargetContainerId);
        command.Parameters.AddWithValue("$TargetSectionId", (object?)row.TargetSectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$FilterJson", row.FilterJson);
        command.Parameters.AddWithValue("$MilestonesAsProjects", row.MilestonesAsProjects);
        command.Parameters.AddWithValue("$State", row.State);
        command.Parameters.AddWithValue("$IssuesCursorUtc", row.IssuesCursorUtc is { } IssuesCursorUtc ? StorageFormats.Instant(IssuesCursorUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$CommentsCursorUtc", row.CommentsCursorUtc is { } CommentsCursorUtc ? StorageFormats.Instant(CommentsCursorUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$IssuesEtag", (object?)row.IssuesEtag ?? DBNull.Value);
        command.Parameters.AddWithValue("$CommentsEtag", (object?)row.CommentsEtag ?? DBNull.Value);
        command.Parameters.AddWithValue("$LabelsEtag", (object?)row.LabelsEtag ?? DBNull.Value);
        command.Parameters.AddWithValue("$MilestonesEtag", (object?)row.MilestonesEtag ?? DBNull.Value);
        command.Parameters.AddWithValue("$InitialImportDoneUtc", row.InitialImportDoneUtc is { } InitialImportDoneUtc ? StorageFormats.Instant(InitialImportDoneUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastSyncedUtc", row.LastSyncedUtc is { } LastSyncedUtc ? StorageFormats.Instant(LastSyncedUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorKind", (object?)row.LastErrorKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorMessage", (object?)row.LastErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$Version", row.Version);
        command.Parameters.AddWithValue("$CreatedAtUtc", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$UpdatedAtUtc", StorageFormats.Instant(row.UpdatedAtUtc));
        command.Parameters.AddWithValue("$DeletedAtUtc", row.DeletedAtUtc is { } DeletedAtUtc ? StorageFormats.Instant(DeletedAtUtc) : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<GitHubRepositoryRecord?> GetRepositoryAsync(string id, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT * FROM GitHubRepositories WHERE Id=$id;", ReadRepository, ct, ("$id", id));
        return rows.SingleOrDefault();
    }

    public async Task<GitHubRepositoryRecord?> UpdateRepositoryAsync(GitHubRepositoryRecord row, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE GitHubRepositories SET ConnectionId=$ConnectionId, RepositoryId=$RepositoryId, Owner=$Owner, Name=$Name, FullName=$FullName, HtmlUrl=$HtmlUrl, IsPrivate=$IsPrivate, TargetContainerId=$TargetContainerId, TargetSectionId=$TargetSectionId, FilterJson=$FilterJson, MilestonesAsProjects=$MilestonesAsProjects, State=$State, IssuesCursorUtc=$IssuesCursorUtc, CommentsCursorUtc=$CommentsCursorUtc, IssuesEtag=$IssuesEtag, CommentsEtag=$CommentsEtag, LabelsEtag=$LabelsEtag, MilestonesEtag=$MilestonesEtag, InitialImportDoneUtc=$InitialImportDoneUtc, LastSyncedUtc=$LastSyncedUtc, LastErrorKind=$LastErrorKind, LastErrorMessage=$LastErrorMessage, UpdatedAtUtc=$UpdatedAtUtc, DeletedAtUtc=$DeletedAtUtc, Version=Version+1 WHERE Id=$Id AND Version=$expected;";
        command.Parameters.AddWithValue("$Id", row.Id);
        command.Parameters.AddWithValue("$ConnectionId", row.ConnectionId);
        command.Parameters.AddWithValue("$RepositoryId", row.RepositoryId);
        command.Parameters.AddWithValue("$Owner", row.Owner);
        command.Parameters.AddWithValue("$Name", row.Name);
        command.Parameters.AddWithValue("$FullName", row.FullName);
        command.Parameters.AddWithValue("$HtmlUrl", row.HtmlUrl);
        command.Parameters.AddWithValue("$IsPrivate", row.IsPrivate);
        command.Parameters.AddWithValue("$TargetContainerId", row.TargetContainerId);
        command.Parameters.AddWithValue("$TargetSectionId", (object?)row.TargetSectionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$FilterJson", row.FilterJson);
        command.Parameters.AddWithValue("$MilestonesAsProjects", row.MilestonesAsProjects);
        command.Parameters.AddWithValue("$State", row.State);
        command.Parameters.AddWithValue("$IssuesCursorUtc", row.IssuesCursorUtc is { } IssuesCursorUtc ? StorageFormats.Instant(IssuesCursorUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$CommentsCursorUtc", row.CommentsCursorUtc is { } CommentsCursorUtc ? StorageFormats.Instant(CommentsCursorUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$IssuesEtag", (object?)row.IssuesEtag ?? DBNull.Value);
        command.Parameters.AddWithValue("$CommentsEtag", (object?)row.CommentsEtag ?? DBNull.Value);
        command.Parameters.AddWithValue("$LabelsEtag", (object?)row.LabelsEtag ?? DBNull.Value);
        command.Parameters.AddWithValue("$MilestonesEtag", (object?)row.MilestonesEtag ?? DBNull.Value);
        command.Parameters.AddWithValue("$InitialImportDoneUtc", row.InitialImportDoneUtc is { } InitialImportDoneUtc ? StorageFormats.Instant(InitialImportDoneUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastSyncedUtc", row.LastSyncedUtc is { } LastSyncedUtc ? StorageFormats.Instant(LastSyncedUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorKind", (object?)row.LastErrorKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorMessage", (object?)row.LastErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$Version", row.Version);
        command.Parameters.AddWithValue("$CreatedAtUtc", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$UpdatedAtUtc", StorageFormats.Instant(row.UpdatedAtUtc));
        command.Parameters.AddWithValue("$DeletedAtUtc", row.DeletedAtUtc is { } DeletedAtUtc ? StorageFormats.Instant(DeletedAtUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? row with { Version = expectedVersion + 1 } : null;
    }

    private static GitHubIssueLinkRecord ReadIssueLink(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("Id")),
        RepositorySubscriptionId = reader.GetString(reader.GetOrdinal("RepositorySubscriptionId")),
        ConnectionId = reader.GetString(reader.GetOrdinal("ConnectionId")),
        TaskId = reader.IsDBNull(reader.GetOrdinal("TaskId")) ? null : reader.GetString(reader.GetOrdinal("TaskId")),
        IssueId = reader.GetInt64(reader.GetOrdinal("IssueId")),
        IssueNodeId = reader.GetString(reader.GetOrdinal("IssueNodeId")),
        IssueNumber = reader.GetInt64(reader.GetOrdinal("IssueNumber")),
        HtmlUrl = reader.GetString(reader.GetOrdinal("HtmlUrl")),
        State = reader.GetInt32(reader.GetOrdinal("State")),
        BaseJson = reader.GetString(reader.GetOrdinal("BaseJson")),
        BaseRemoteUpdatedUtc = StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("BaseRemoteUpdatedUtc")))!.Value,
        IssueEtag = reader.IsDBNull(reader.GetOrdinal("IssueEtag")) ? null : reader.GetString(reader.GetOrdinal("IssueEtag")),
        LocalAssigneeIntent = reader.IsDBNull(reader.GetOrdinal("LocalAssigneeIntent")) ? null : reader.GetInt32(reader.GetOrdinal("LocalAssigneeIntent")),
        PlacementManaged = reader.GetInt64(reader.GetOrdinal("PlacementManaged")) != 0,
        LocalDirty = reader.GetInt64(reader.GetOrdinal("LocalDirty")) != 0,
        LocalDirtySinceUtc = reader.IsDBNull(reader.GetOrdinal("LocalDirtySinceUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("LocalDirtySinceUtc"))),
        LastSyncedUtc = reader.IsDBNull(reader.GetOrdinal("LastSyncedUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("LastSyncedUtc"))),
        ConflictNoticeUtc = reader.IsDBNull(reader.GetOrdinal("ConflictNoticeUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("ConflictNoticeUtc"))),
        LastErrorKind = reader.IsDBNull(reader.GetOrdinal("LastErrorKind")) ? null : reader.GetInt32(reader.GetOrdinal("LastErrorKind")),
        LastErrorMessage = reader.IsDBNull(reader.GetOrdinal("LastErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("LastErrorMessage")),
        ErrorAttempts = reader.GetInt32(reader.GetOrdinal("ErrorAttempts")),
        NextAttemptUtc = reader.IsDBNull(reader.GetOrdinal("NextAttemptUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("NextAttemptUtc"))),
        Version = reader.GetInt64(reader.GetOrdinal("Version")),
        CreatedAtUtc = StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("CreatedAtUtc")))!.Value,
        UpdatedAtUtc = StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("UpdatedAtUtc")))!.Value,
    };

    public async Task AddIssueLinkAsync(GitHubIssueLinkRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO GitHubIssueLinks (Id, RepositorySubscriptionId, ConnectionId, TaskId, IssueId, IssueNodeId, IssueNumber, HtmlUrl, State, BaseJson, BaseRemoteUpdatedUtc, IssueEtag, LocalAssigneeIntent, PlacementManaged, LocalDirty, LocalDirtySinceUtc, LastSyncedUtc, ConflictNoticeUtc, LastErrorKind, LastErrorMessage, ErrorAttempts, NextAttemptUtc, Version, CreatedAtUtc, UpdatedAtUtc) VALUES ($Id, $RepositorySubscriptionId, $ConnectionId, $TaskId, $IssueId, $IssueNodeId, $IssueNumber, $HtmlUrl, $State, $BaseJson, $BaseRemoteUpdatedUtc, $IssueEtag, $LocalAssigneeIntent, $PlacementManaged, $LocalDirty, $LocalDirtySinceUtc, $LastSyncedUtc, $ConflictNoticeUtc, $LastErrorKind, $LastErrorMessage, $ErrorAttempts, $NextAttemptUtc, $Version, $CreatedAtUtc, $UpdatedAtUtc);";
        command.Parameters.AddWithValue("$Id", row.Id);
        command.Parameters.AddWithValue("$RepositorySubscriptionId", row.RepositorySubscriptionId);
        command.Parameters.AddWithValue("$ConnectionId", row.ConnectionId);
        command.Parameters.AddWithValue("$TaskId", (object?)row.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$IssueId", row.IssueId);
        command.Parameters.AddWithValue("$IssueNodeId", row.IssueNodeId);
        command.Parameters.AddWithValue("$IssueNumber", row.IssueNumber);
        command.Parameters.AddWithValue("$HtmlUrl", row.HtmlUrl);
        command.Parameters.AddWithValue("$State", row.State);
        command.Parameters.AddWithValue("$BaseJson", row.BaseJson);
        command.Parameters.AddWithValue("$BaseRemoteUpdatedUtc", StorageFormats.Instant(row.BaseRemoteUpdatedUtc));
        command.Parameters.AddWithValue("$IssueEtag", (object?)row.IssueEtag ?? DBNull.Value);
        command.Parameters.AddWithValue("$LocalAssigneeIntent", (object?)row.LocalAssigneeIntent ?? DBNull.Value);
        command.Parameters.AddWithValue("$PlacementManaged", row.PlacementManaged);
        command.Parameters.AddWithValue("$LocalDirty", row.LocalDirty);
        command.Parameters.AddWithValue("$LocalDirtySinceUtc", row.LocalDirtySinceUtc is { } LocalDirtySinceUtc ? StorageFormats.Instant(LocalDirtySinceUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastSyncedUtc", row.LastSyncedUtc is { } LastSyncedUtc ? StorageFormats.Instant(LastSyncedUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$ConflictNoticeUtc", row.ConflictNoticeUtc is { } ConflictNoticeUtc ? StorageFormats.Instant(ConflictNoticeUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorKind", (object?)row.LastErrorKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorMessage", (object?)row.LastErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$ErrorAttempts", row.ErrorAttempts);
        command.Parameters.AddWithValue("$NextAttemptUtc", row.NextAttemptUtc is { } NextAttemptUtc ? StorageFormats.Instant(NextAttemptUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$Version", row.Version);
        command.Parameters.AddWithValue("$CreatedAtUtc", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$UpdatedAtUtc", StorageFormats.Instant(row.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<GitHubIssueLinkRecord?> GetIssueLinkAsync(string id, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT * FROM GitHubIssueLinks WHERE Id=$id;", ReadIssueLink, ct, ("$id", id));
        return rows.SingleOrDefault();
    }

    public async Task<GitHubIssueLinkRecord?> UpdateIssueLinkAsync(GitHubIssueLinkRecord row, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE GitHubIssueLinks SET RepositorySubscriptionId=$RepositorySubscriptionId, ConnectionId=$ConnectionId, TaskId=$TaskId, IssueId=$IssueId, IssueNodeId=$IssueNodeId, IssueNumber=$IssueNumber, HtmlUrl=$HtmlUrl, State=$State, BaseJson=$BaseJson, BaseRemoteUpdatedUtc=$BaseRemoteUpdatedUtc, IssueEtag=$IssueEtag, LocalAssigneeIntent=$LocalAssigneeIntent, PlacementManaged=$PlacementManaged, LocalDirty=$LocalDirty, LocalDirtySinceUtc=$LocalDirtySinceUtc, LastSyncedUtc=$LastSyncedUtc, ConflictNoticeUtc=$ConflictNoticeUtc, LastErrorKind=$LastErrorKind, LastErrorMessage=$LastErrorMessage, ErrorAttempts=$ErrorAttempts, NextAttemptUtc=$NextAttemptUtc, UpdatedAtUtc=$UpdatedAtUtc, Version=Version+1 WHERE Id=$Id AND Version=$expected;";
        command.Parameters.AddWithValue("$Id", row.Id);
        command.Parameters.AddWithValue("$RepositorySubscriptionId", row.RepositorySubscriptionId);
        command.Parameters.AddWithValue("$ConnectionId", row.ConnectionId);
        command.Parameters.AddWithValue("$TaskId", (object?)row.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("$IssueId", row.IssueId);
        command.Parameters.AddWithValue("$IssueNodeId", row.IssueNodeId);
        command.Parameters.AddWithValue("$IssueNumber", row.IssueNumber);
        command.Parameters.AddWithValue("$HtmlUrl", row.HtmlUrl);
        command.Parameters.AddWithValue("$State", row.State);
        command.Parameters.AddWithValue("$BaseJson", row.BaseJson);
        command.Parameters.AddWithValue("$BaseRemoteUpdatedUtc", StorageFormats.Instant(row.BaseRemoteUpdatedUtc));
        command.Parameters.AddWithValue("$IssueEtag", (object?)row.IssueEtag ?? DBNull.Value);
        command.Parameters.AddWithValue("$LocalAssigneeIntent", (object?)row.LocalAssigneeIntent ?? DBNull.Value);
        command.Parameters.AddWithValue("$PlacementManaged", row.PlacementManaged);
        command.Parameters.AddWithValue("$LocalDirty", row.LocalDirty);
        command.Parameters.AddWithValue("$LocalDirtySinceUtc", row.LocalDirtySinceUtc is { } LocalDirtySinceUtc ? StorageFormats.Instant(LocalDirtySinceUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastSyncedUtc", row.LastSyncedUtc is { } LastSyncedUtc ? StorageFormats.Instant(LastSyncedUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$ConflictNoticeUtc", row.ConflictNoticeUtc is { } ConflictNoticeUtc ? StorageFormats.Instant(ConflictNoticeUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorKind", (object?)row.LastErrorKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorMessage", (object?)row.LastErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$ErrorAttempts", row.ErrorAttempts);
        command.Parameters.AddWithValue("$NextAttemptUtc", row.NextAttemptUtc is { } NextAttemptUtc ? StorageFormats.Instant(NextAttemptUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$Version", row.Version);
        command.Parameters.AddWithValue("$CreatedAtUtc", StorageFormats.Instant(row.CreatedAtUtc));
        command.Parameters.AddWithValue("$UpdatedAtUtc", StorageFormats.Instant(row.UpdatedAtUtc));
        command.Parameters.AddWithValue("$expected", expectedVersion);
        return await command.ExecuteNonQueryAsync(ct) == 1 ? row with { Version = expectedVersion + 1 } : null;
    }

    private static GitHubChecklistItemRecord ReadChecklistItem(SqliteDataReader reader) => new()
    {
        SubtaskId = reader.GetString(reader.GetOrdinal("SubtaskId")),
        IssueLinkId = reader.GetString(reader.GetOrdinal("IssueLinkId")),
        BaseOrdinal = reader.GetInt64(reader.GetOrdinal("BaseOrdinal")),
        BaseText = reader.GetString(reader.GetOrdinal("BaseText")),
        BaseChecked = reader.GetInt64(reader.GetOrdinal("BaseChecked")) != 0,
    };

    public async Task UpsertChecklistItemAsync(GitHubChecklistItemRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO GitHubChecklistItems (SubtaskId, IssueLinkId, BaseOrdinal, BaseText, BaseChecked) VALUES ($SubtaskId, $IssueLinkId, $BaseOrdinal, $BaseText, $BaseChecked) ON CONFLICT (SubtaskId) DO UPDATE SET IssueLinkId=excluded.IssueLinkId, BaseOrdinal=excluded.BaseOrdinal, BaseText=excluded.BaseText, BaseChecked=excluded.BaseChecked;";
        command.Parameters.AddWithValue("$SubtaskId", row.SubtaskId);
        command.Parameters.AddWithValue("$IssueLinkId", row.IssueLinkId);
        command.Parameters.AddWithValue("$BaseOrdinal", row.BaseOrdinal);
        command.Parameters.AddWithValue("$BaseText", row.BaseText);
        command.Parameters.AddWithValue("$BaseChecked", row.BaseChecked);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static GitHubCommentLinkRecord ReadCommentLink(SqliteDataReader reader) => new()
    {
        DiaryEntryId = reader.GetString(reader.GetOrdinal("DiaryEntryId")),
        IssueLinkId = reader.GetString(reader.GetOrdinal("IssueLinkId")),
        CommentId = reader.IsDBNull(reader.GetOrdinal("CommentId")) ? null : reader.GetInt64(reader.GetOrdinal("CommentId")),
        AuthorLogin = reader.IsDBNull(reader.GetOrdinal("AuthorLogin")) ? null : reader.GetString(reader.GetOrdinal("AuthorLogin")),
        HtmlUrl = reader.IsDBNull(reader.GetOrdinal("HtmlUrl")) ? null : reader.GetString(reader.GetOrdinal("HtmlUrl")),
        Origin = reader.GetInt32(reader.GetOrdinal("Origin")),
        State = reader.GetInt32(reader.GetOrdinal("State")),
        BaseBody = reader.IsDBNull(reader.GetOrdinal("BaseBody")) ? null : reader.GetString(reader.GetOrdinal("BaseBody")),
        BaseRemoteUpdatedUtc = reader.IsDBNull(reader.GetOrdinal("BaseRemoteUpdatedUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("BaseRemoteUpdatedUtc"))),
        LastErrorKind = reader.IsDBNull(reader.GetOrdinal("LastErrorKind")) ? null : reader.GetInt32(reader.GetOrdinal("LastErrorKind")),
        LastErrorMessage = reader.IsDBNull(reader.GetOrdinal("LastErrorMessage")) ? null : reader.GetString(reader.GetOrdinal("LastErrorMessage")),
        ErrorAttempts = reader.GetInt32(reader.GetOrdinal("ErrorAttempts")),
        NextAttemptUtc = reader.IsDBNull(reader.GetOrdinal("NextAttemptUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("NextAttemptUtc"))),
    };

    public async Task UpsertCommentLinkAsync(GitHubCommentLinkRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO GitHubCommentLinks (DiaryEntryId, IssueLinkId, CommentId, AuthorLogin, HtmlUrl, Origin, State, BaseBody, BaseRemoteUpdatedUtc, LastErrorKind, LastErrorMessage, ErrorAttempts, NextAttemptUtc) VALUES ($DiaryEntryId, $IssueLinkId, $CommentId, $AuthorLogin, $HtmlUrl, $Origin, $State, $BaseBody, $BaseRemoteUpdatedUtc, $LastErrorKind, $LastErrorMessage, $ErrorAttempts, $NextAttemptUtc) ON CONFLICT (DiaryEntryId) DO UPDATE SET IssueLinkId=excluded.IssueLinkId, CommentId=excluded.CommentId, AuthorLogin=excluded.AuthorLogin, HtmlUrl=excluded.HtmlUrl, Origin=excluded.Origin, State=excluded.State, BaseBody=excluded.BaseBody, BaseRemoteUpdatedUtc=excluded.BaseRemoteUpdatedUtc, LastErrorKind=excluded.LastErrorKind, LastErrorMessage=excluded.LastErrorMessage, ErrorAttempts=excluded.ErrorAttempts, NextAttemptUtc=excluded.NextAttemptUtc;";
        command.Parameters.AddWithValue("$DiaryEntryId", row.DiaryEntryId);
        command.Parameters.AddWithValue("$IssueLinkId", row.IssueLinkId);
        command.Parameters.AddWithValue("$CommentId", (object?)row.CommentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$AuthorLogin", (object?)row.AuthorLogin ?? DBNull.Value);
        command.Parameters.AddWithValue("$HtmlUrl", (object?)row.HtmlUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("$Origin", row.Origin);
        command.Parameters.AddWithValue("$State", row.State);
        command.Parameters.AddWithValue("$BaseBody", (object?)row.BaseBody ?? DBNull.Value);
        command.Parameters.AddWithValue("$BaseRemoteUpdatedUtc", row.BaseRemoteUpdatedUtc is { } BaseRemoteUpdatedUtc ? StorageFormats.Instant(BaseRemoteUpdatedUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorKind", (object?)row.LastErrorKind ?? DBNull.Value);
        command.Parameters.AddWithValue("$LastErrorMessage", (object?)row.LastErrorMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$ErrorAttempts", row.ErrorAttempts);
        command.Parameters.AddWithValue("$NextAttemptUtc", row.NextAttemptUtc is { } NextAttemptUtc ? StorageFormats.Instant(NextAttemptUtc) : (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static GitHubLabelTagRecord ReadLabelTag(SqliteDataReader reader) => new()
    {
        RepositorySubscriptionId = reader.GetString(reader.GetOrdinal("RepositorySubscriptionId")),
        LabelId = reader.GetInt64(reader.GetOrdinal("LabelId")),
        LabelName = reader.GetString(reader.GetOrdinal("LabelName")),
        LabelColor = reader.GetString(reader.GetOrdinal("LabelColor")),
        TagId = reader.GetString(reader.GetOrdinal("TagId")),
    };

    public async Task UpsertLabelTagAsync(GitHubLabelTagRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO GitHubLabelTags (RepositorySubscriptionId, LabelId, LabelName, LabelColor, TagId) VALUES ($RepositorySubscriptionId, $LabelId, $LabelName, $LabelColor, $TagId) ON CONFLICT (RepositorySubscriptionId, LabelId) DO UPDATE SET LabelName=excluded.LabelName, LabelColor=excluded.LabelColor, TagId=excluded.TagId;";
        command.Parameters.AddWithValue("$RepositorySubscriptionId", row.RepositorySubscriptionId);
        command.Parameters.AddWithValue("$LabelId", row.LabelId);
        command.Parameters.AddWithValue("$LabelName", row.LabelName);
        command.Parameters.AddWithValue("$LabelColor", row.LabelColor);
        command.Parameters.AddWithValue("$TagId", row.TagId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static GitHubMilestoneProjectRecord ReadMilestoneProject(SqliteDataReader reader) => new()
    {
        RepositorySubscriptionId = reader.GetString(reader.GetOrdinal("RepositorySubscriptionId")),
        MilestoneNumber = reader.GetInt64(reader.GetOrdinal("MilestoneNumber")),
        MilestoneId = reader.GetInt64(reader.GetOrdinal("MilestoneId")),
        ContainerId = reader.GetString(reader.GetOrdinal("ContainerId")),
        BaseTitle = reader.GetString(reader.GetOrdinal("BaseTitle")),
        BaseDueOn = reader.IsDBNull(reader.GetOrdinal("BaseDueOn")) ? null : reader.GetString(reader.GetOrdinal("BaseDueOn")),
    };

    public async Task UpsertMilestoneProjectAsync(GitHubMilestoneProjectRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO GitHubMilestoneProjects (RepositorySubscriptionId, MilestoneNumber, MilestoneId, ContainerId, BaseTitle, BaseDueOn) VALUES ($RepositorySubscriptionId, $MilestoneNumber, $MilestoneId, $ContainerId, $BaseTitle, $BaseDueOn) ON CONFLICT (RepositorySubscriptionId, MilestoneNumber) DO UPDATE SET MilestoneId=excluded.MilestoneId, ContainerId=excluded.ContainerId, BaseTitle=excluded.BaseTitle, BaseDueOn=excluded.BaseDueOn;";
        command.Parameters.AddWithValue("$RepositorySubscriptionId", row.RepositorySubscriptionId);
        command.Parameters.AddWithValue("$MilestoneNumber", row.MilestoneNumber);
        command.Parameters.AddWithValue("$MilestoneId", row.MilestoneId);
        command.Parameters.AddWithValue("$ContainerId", row.ContainerId);
        command.Parameters.AddWithValue("$BaseTitle", row.BaseTitle);
        command.Parameters.AddWithValue("$BaseDueOn", (object?)row.BaseDueOn ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static GitHubSyncRunRecord ReadSyncRun(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(reader.GetOrdinal("Id")),
        ConnectionId = reader.GetString(reader.GetOrdinal("ConnectionId")),
        StartedAtUtc = StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("StartedAtUtc")))!.Value,
        FinishedAtUtc = reader.IsDBNull(reader.GetOrdinal("FinishedAtUtc")) ? null : StorageFormats.ParseInstant(reader.GetString(reader.GetOrdinal("FinishedAtUtc"))),
        RequestCount = reader.GetInt64(reader.GetOrdinal("RequestCount")),
        NotModifiedCount = reader.GetInt64(reader.GetOrdinal("NotModifiedCount")),
        CreatedCount = reader.GetInt64(reader.GetOrdinal("CreatedCount")),
        UpdatedCount = reader.GetInt64(reader.GetOrdinal("UpdatedCount")),
        PushedCount = reader.GetInt64(reader.GetOrdinal("PushedCount")),
        ConflictCount = reader.GetInt64(reader.GetOrdinal("ConflictCount")),
        ErrorKind = reader.IsDBNull(reader.GetOrdinal("ErrorKind")) ? null : reader.GetInt32(reader.GetOrdinal("ErrorKind")),
    };

    public async Task AddSyncRunAsync(GitHubSyncRunRecord row, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO GitHubSyncRuns (Id, ConnectionId, StartedAtUtc, FinishedAtUtc, RequestCount, NotModifiedCount, CreatedCount, UpdatedCount, PushedCount, ConflictCount, ErrorKind) VALUES ($Id, $ConnectionId, $StartedAtUtc, $FinishedAtUtc, $RequestCount, $NotModifiedCount, $CreatedCount, $UpdatedCount, $PushedCount, $ConflictCount, $ErrorKind);";
        command.Parameters.AddWithValue("$Id", row.Id);
        command.Parameters.AddWithValue("$ConnectionId", row.ConnectionId);
        command.Parameters.AddWithValue("$StartedAtUtc", StorageFormats.Instant(row.StartedAtUtc));
        command.Parameters.AddWithValue("$FinishedAtUtc", row.FinishedAtUtc is { } FinishedAtUtc ? StorageFormats.Instant(FinishedAtUtc) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$RequestCount", row.RequestCount);
        command.Parameters.AddWithValue("$NotModifiedCount", row.NotModifiedCount);
        command.Parameters.AddWithValue("$CreatedCount", row.CreatedCount);
        command.Parameters.AddWithValue("$UpdatedCount", row.UpdatedCount);
        command.Parameters.AddWithValue("$PushedCount", row.PushedCount);
        command.Parameters.AddWithValue("$ConflictCount", row.ConflictCount);
        command.Parameters.AddWithValue("$ErrorKind", (object?)row.ErrorKind ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
        using var trim = connection.CreateCommand();
        trim.CommandText = "DELETE FROM GitHubSyncRuns WHERE ConnectionId=$id AND Id NOT IN (SELECT Id FROM GitHubSyncRuns WHERE ConnectionId=$id ORDER BY StartedAtUtc DESC, Id DESC LIMIT 200);";
        trim.Parameters.AddWithValue("$id", row.ConnectionId);
        await trim.ExecuteNonQueryAsync(ct);
    }

    public async Task<GitHubConnectionRecord?> GetConnectionForSpaceAsync(string id, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT * FROM GitHubConnections WHERE SpaceId=$id AND DeletedAtUtc IS NULL;", ReadConnection, ct, ("$id", id));
        return rows.SingleOrDefault();
    }

    public async Task<IReadOnlyList<GitHubRepositoryRecord>> ListSubscriptionsAsync(string id, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT * FROM GitHubRepositories WHERE ConnectionId=$id AND DeletedAtUtc IS NULL ORDER BY FullName, Id;", ReadRepository, ct, ("$id", id));
        return rows;
    }

    public async Task<GitHubIssueLinkRecord?> FindLinkByIssueAsync(string id, long issueId, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT * FROM GitHubIssueLinks WHERE ConnectionId=$id AND IssueId=$issue;", ReadIssueLink, ct, ("$id", id), ("$issue", issueId));
        return rows.SingleOrDefault();
    }

    public async Task<GitHubIssueLinkRecord?> FindLinkByTaskAsync(string id, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT * FROM GitHubIssueLinks WHERE TaskId=$id;", ReadIssueLink, ct, ("$id", id));
        return rows.SingleOrDefault();
    }

    public async Task<IReadOnlyList<GitHubChecklistItemRecord>> ListChecklistItemsAsync(string id, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT * FROM GitHubChecklistItems WHERE IssueLinkId=$id ORDER BY BaseOrdinal, SubtaskId;", ReadChecklistItem, ct, ("$id", id));
        return rows;
    }

    public async Task<IReadOnlyList<GitHubCommentLinkRecord>> ListCommentLinksAsync(string id, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT * FROM GitHubCommentLinks WHERE IssueLinkId=$id ORDER BY DiaryEntryId;", ReadCommentLink, ct, ("$id", id));
        return rows;
    }

    public async Task<IReadOnlyList<GitHubLabelTagRecord>> ListLabelTagsAsync(string id, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT * FROM GitHubLabelTags WHERE RepositorySubscriptionId=$id ORDER BY LabelId;", ReadLabelTag, ct, ("$id", id));
        return rows;
    }

    public async Task<IReadOnlyList<GitHubMilestoneProjectRecord>> ListMilestoneProjectsAsync(string id, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT * FROM GitHubMilestoneProjects WHERE RepositorySubscriptionId=$id ORDER BY MilestoneNumber;", ReadMilestoneProject, ct, ("$id", id));
        return rows;
    }

    public async Task<IReadOnlyList<GitHubSyncRunRecord>> ListSyncRunsAsync(string id, CancellationToken ct)
    {
        var rows = await QueryAsync("SELECT * FROM GitHubSyncRuns WHERE ConnectionId=$id ORDER BY StartedAtUtc DESC, Id DESC LIMIT 200;", ReadSyncRun, ct, ("$id", id));
        return rows;
    }

    public Task<IReadOnlyList<GitHubIssueLinkRecord>> ListDirtyLinksAsync(int limit, CancellationToken ct) =>
        QueryAsync("SELECT * FROM GitHubIssueLinks WHERE LocalDirty=1 AND State=0 ORDER BY LocalDirtySinceUtc, Id LIMIT $limit;", ReadIssueLink, ct, ("$limit", Bounded(limit)));

    public Task<IReadOnlyList<GitHubIssueLinkRecord>> ListLinksDueForRetryAsync(DateTimeOffset now, int limit, CancellationToken ct) =>
        QueryAsync("SELECT * FROM GitHubIssueLinks WHERE State=0 AND NextAttemptUtc <= $now ORDER BY NextAttemptUtc, Id LIMIT $limit;", ReadIssueLink, ct, ("$now", StorageFormats.Instant(now)), ("$limit", Bounded(limit)));

    public Task<IReadOnlyList<GitHubIssueLinkRecord>> ListLinksForTasksAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Task.FromResult<IReadOnlyList<GitHubIssueLinkRecord>>([]);
        Bounded(ids.Count);
        var parameters = ids.Select((id, index) => ("$id" + index, (object)id)).ToArray();
        return QueryAsync($"SELECT * FROM GitHubIssueLinks WHERE TaskId IN ({string.Join(", ", parameters.Select(parameter => parameter.Item1))}) ORDER BY TaskId;", ReadIssueLink, ct, parameters);
    }

    public async Task ReplaceChecklistItemsAsync(string issueLinkId, IReadOnlyList<GitHubChecklistItemRecord> rows, CancellationToken ct)
    {
        if (rows.Any(row => row.IssueLinkId != issueLinkId)) throw new ArgumentException("Checklist rows must belong to the requested link.", nameof(rows));
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM GitHubChecklistItems WHERE IssueLinkId=$id;";
        command.Parameters.AddWithValue("$id", issueLinkId);
        await command.ExecuteNonQueryAsync(ct);
        foreach (var row in rows) await UpsertChecklistItemAsync(row, ct);
    }

    public Task<IReadOnlyList<GitHubIssueLinkRecord>> ListRepositoryLinksAsync(string repositoryId, string? afterId, int limit, CancellationToken ct) =>
        QueryAsync("SELECT * FROM GitHubIssueLinks WHERE RepositorySubscriptionId=$repo AND Id>$after ORDER BY Id LIMIT $limit;", ReadIssueLink, ct,
            ("$repo", repositoryId), ("$after", afterId ?? ""), ("$limit", Bounded(limit)));

    public async Task ClearIssueMappingsAsync(string issueLinkId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM GitHubChecklistItems WHERE IssueLinkId=$id; DELETE FROM GitHubCommentLinks WHERE IssueLinkId=$id;";
        command.Parameters.AddWithValue("$id", issueLinkId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static int Bounded(int limit) => limit is >= 1 and <= 200 ? limit : throw new ArgumentOutOfRangeException(nameof(limit));

    private async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> read, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(read(reader));
        return rows;
    }
}
