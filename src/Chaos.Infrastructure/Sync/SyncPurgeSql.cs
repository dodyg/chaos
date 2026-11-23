namespace Chaos.Infrastructure.Sync;

/// <summary>
/// The dependents that leave with one purged entity (PLAN2 §7.4 step 2.1), mirroring what
/// <c>DataPurge</c> removes for that kind, expressed per entity key instead of as a sweep of the
/// trash. A purge is a hard delete: its changes are never captured, and its SyncClocks rows go.
/// </summary>
internal static class SyncPurgeSql
{
    /// <summary>The delete statements for one entity, in dependency order; <c>$k</c> is the first key column's value.</summary>
    public static IReadOnlyList<string> For(string kind, IReadOnlyList<string> key) => kind switch
    {
        "saved_view" =>
        [
            "DELETE FROM SavedViewMatches WHERE ViewId = $k0;",
            "DELETE FROM ViewAlertBaselineState WHERE ViewId = $k0;",
            "DELETE FROM ViewAlerts WHERE ViewId = $k0;",
        ],
        "task" =>
        [
            "DELETE FROM SearchIndex WHERE rowid IN (SELECT RowId FROM SearchDocuments WHERE TaskId = $k0);",
            "DELETE FROM SearchDocuments WHERE TaskId = $k0;",
            "DELETE FROM SubtaskTags WHERE SubtaskId IN (SELECT Id FROM Subtasks WHERE TaskId = $k0);",
            "DELETE FROM GitHubChecklistItems WHERE SubtaskId IN (SELECT Id FROM Subtasks WHERE TaskId = $k0);",
            "DELETE FROM Attachments WHERE (OwnerKind = 1 AND OwnerId = $k0) OR (OwnerKind = 2 AND OwnerId IN (SELECT Id FROM DiaryEntries WHERE TaskId = $k0));",
            "DELETE FROM GitHubCommentLinks WHERE DiaryEntryId IN (SELECT Id FROM DiaryEntries WHERE TaskId = $k0);",
            "DELETE FROM GitHubIssueWrites WHERE IssueLinkId IN (SELECT Id FROM GitHubIssueLinks WHERE TaskId = $k0);",
            "UPDATE GitHubIssueLinks SET TaskId = NULL, State = 3, LocalDirty = 0, LocalDirtySinceUtc = NULL WHERE TaskId = $k0;",
            "DELETE FROM ChecklistConversions WHERE TaskId = $k0;",
            "DELETE FROM Subtasks WHERE TaskId = $k0;",
            "DELETE FROM DiaryRevisions WHERE EntryId IN (SELECT Id FROM DiaryEntries WHERE TaskId = $k0);",
            "DELETE FROM DiaryEntries WHERE TaskId = $k0;",
            "DELETE FROM TaskTags WHERE TaskId = $k0;",
            "DELETE FROM TaskAssignees WHERE TaskId = $k0;",
            "DELETE FROM Nudges WHERE TaskId = $k0;",
            "DELETE FROM FollowUps WHERE DelegationId IN (SELECT Id FROM TaskDelegations WHERE TaskId = $k0);",
            "DELETE FROM TaskDelegations WHERE TaskId = $k0;",
            "DELETE FROM TaskHistory WHERE TaskId = $k0;",
            "DELETE FROM ScratchTaskLinks WHERE TaskId = $k0;",
            "DELETE FROM PageTaskLinks WHERE TaskId = $k0;",
            "UPDATE RecurrenceOccurrences SET TaskId = NULL WHERE TaskId = $k0;",
            "DELETE FROM SavedViewMatches WHERE TaskId = $k0;",
            "DELETE FROM NotificationJobs WHERE TaskId = $k0;",
            "DELETE FROM ReminderRules WHERE TaskId = $k0;",
            "DELETE FROM FollowUps WHERE TaskId = $k0;",
            "DELETE FROM WaitingDetails WHERE TaskId = $k0;",
            "DELETE FROM DayPlanItems WHERE TaskId = $k0;",
            "DELETE FROM WeekGoalTasks WHERE TaskId = $k0;",
            "DELETE FROM WeekAssignments WHERE TaskId = $k0;",
            "DELETE FROM ImportedItems WHERE TaskId = $k0;",
            "DELETE FROM UndoEntries WHERE instr(InversePayload, $k0) > 0 OR instr(ExpectedVersions, $k0) > 0;",
            "DELETE FROM Tasks WHERE Id = $k0;",
        ],
        "subtask" =>
        [
            "DELETE FROM SearchIndex WHERE rowid IN (SELECT RowId FROM SearchDocuments WHERE TargetKind = 2 AND TargetId = $k0);",
            "DELETE FROM SearchDocuments WHERE TargetKind = 2 AND TargetId = $k0;",
            "DELETE FROM SubtaskTags WHERE SubtaskId = $k0;",
            "DELETE FROM GitHubChecklistItems WHERE SubtaskId = $k0;",
            "DELETE FROM Subtasks WHERE Id = $k0;",
        ],
        "diary_entry" =>
        [
            "DELETE FROM SearchIndex WHERE rowid IN (SELECT RowId FROM SearchDocuments WHERE TargetKind = 3 AND TargetId = $k0);",
            "DELETE FROM SearchDocuments WHERE TargetKind = 3 AND TargetId = $k0;",
            "DELETE FROM Attachments WHERE OwnerKind = 2 AND OwnerId = $k0;",
            "DELETE FROM GitHubCommentLinks WHERE DiaryEntryId = $k0;",
            "DELETE FROM DiaryRevisions WHERE EntryId = $k0;",
            "DELETE FROM DiaryEntries WHERE Id = $k0;",
        ],
        "pad" =>
        [
            "DELETE FROM SearchIndex WHERE rowid IN (SELECT RowId FROM SearchDocuments WHERE TargetKind = 4 AND TargetId = $k0);",
            "DELETE FROM SearchDocuments WHERE TargetKind = 4 AND TargetId = $k0;",
            "DELETE FROM Attachments WHERE OwnerKind = 3 AND OwnerId = $k0;",
            "DELETE FROM ScratchTaskLinks WHERE PadItemId = $k0;",
            "DELETE FROM PadDrawings WHERE PadItemId = $k0;",
            "DELETE FROM ScratchPadTags WHERE PadItemId = $k0;",
            "DELETE FROM ScratchPadItems WHERE Id = $k0;",
        ],
        "page" =>
        [
            "DELETE FROM SearchIndex WHERE rowid IN (SELECT RowId FROM SearchDocuments WHERE TargetKind = 5 AND TargetId = $k0);",
            "DELETE FROM SearchDocuments WHERE TargetKind = 5 AND TargetId = $k0;",
            "DELETE FROM Attachments WHERE OwnerKind = 4 AND OwnerId = $k0;",
            "DELETE FROM PageTaskLinks WHERE PageId = $k0;",
            "DELETE FROM PageTags WHERE PageId = $k0;",
            "DELETE FROM PageRevisions WHERE PageId = $k0;",
            "UPDATE Pages SET ParentPageId = NULL WHERE ParentPageId = $k0;",
            "DELETE FROM Pages WHERE Id = $k0;",
        ],
        "space_state" =>
        [
            "UPDATE Tasks SET CustomStateId = NULL WHERE CustomStateId = $k0;",
            "DELETE FROM SpaceStates WHERE Id = $k0;",
        ],
        "tag" =>
        [
            "DELETE FROM TaskTags WHERE TagId = $k0;",
            "DELETE FROM SubtaskTags WHERE TagId = $k0;",
            "DELETE FROM ScratchPadTags WHERE TagId = $k0;",
            "DELETE FROM PageTags WHERE TagId = $k0;",
            "DELETE FROM GitHubLabelTags WHERE TagId = $k0;",
            "UPDATE Tags SET ParentTagId = NULL WHERE ParentTagId = $k0;", // children detach; a leaf-only purge is DataPurge's rule
            "DELETE FROM Tags WHERE Id = $k0;",
        ],
        "container" =>
        [
            // DataPurge removes a list only once nothing refers to it; a remote purge instead
            // detaches the references, so both devices converge on the same rows.
            "UPDATE Tasks SET ContainerId = NULL WHERE ContainerId = $k0;",
            "DELETE FROM Sections WHERE ContainerId = $k0;",
            "UPDATE RecurrenceSeries SET ContainerId = NULL WHERE ContainerId = $k0;",
            "UPDATE GitHubRepositories SET TargetContainerId = NULL WHERE TargetContainerId = $k0;",
            "UPDATE GitHubMilestoneProjects SET ContainerId = NULL WHERE ContainerId = $k0;",
            "UPDATE WeekGoals SET ProjectId = NULL WHERE ProjectId = $k0;",
            "DELETE FROM Containers WHERE Id = $k0;",
        ],
        "section" =>
        [
            "UPDATE Tasks SET SectionId = NULL WHERE SectionId = $k0;",
            "UPDATE GitHubRepositories SET TargetSectionId = NULL WHERE TargetSectionId = $k0;",
            "DELETE FROM Sections WHERE Id = $k0;",
        ],
        "attachment" => ["DELETE FROM Attachments WHERE Id = $k0;"],
        "task_template" => ["DELETE FROM TaskTemplates WHERE Id = $k0;"],
        "recurrence_series" =>
        [
            "DELETE FROM RecurrenceExceptions WHERE SeriesId = $k0;",
            "DELETE FROM RecurrenceOccurrences WHERE SeriesId = $k0;",
            "DELETE FROM RecurrenceSeriesRevisions WHERE SeriesId = $k0;",
            "DELETE FROM RecurrenceSeries WHERE Id = $k0;",
        ],
        "recurrence_occurrence" => ["DELETE FROM RecurrenceOccurrences WHERE SeriesId = $k0 AND NominalKey = $k1;"],
        "day_plan" =>
        [
            "DELETE FROM DayPlanItems WHERE DayPlanId = $k0;",
            "DELETE FROM DayPlans WHERE Id = $k0;",
        ],
        "week_plan" =>
        [
            "DELETE FROM WeekGoalTasks WHERE GoalId IN (SELECT Id FROM WeekGoals WHERE WeekPlanId = $k0);",
            "DELETE FROM WeekGoals WHERE WeekPlanId = $k0;",
            "DELETE FROM WeekAssignments WHERE WeekPlanId = $k0;",
            "DELETE FROM WeekPlanRevisions WHERE WeekPlanId = $k0;",
            "DELETE FROM WeekPlans WHERE Id = $k0;",
        ],
        "week_goal" =>
        [
            "DELETE FROM WeekGoalTasks WHERE GoalId = $k0;",
            "DELETE FROM WeekGoals WHERE Id = $k0;",
        ],
        "import_batch" =>
        [
            "DELETE FROM ImportedItems WHERE BatchId = $k0;",
            "DELETE FROM ImportBatches WHERE Id = $k0;",
        ],
        "github_connection" =>
        [
            "DELETE FROM GitHubLabelTags WHERE RepositorySubscriptionId IN (SELECT Id FROM GitHubRepositories WHERE ConnectionId = $k0);",
            "DELETE FROM GitHubMilestoneProjects WHERE RepositorySubscriptionId IN (SELECT Id FROM GitHubRepositories WHERE ConnectionId = $k0);",
            "DELETE FROM GitHubChecklistItems WHERE IssueLinkId IN (SELECT Id FROM GitHubIssueLinks WHERE RepositorySubscriptionId IN (SELECT Id FROM GitHubRepositories WHERE ConnectionId = $k0));",
            "DELETE FROM GitHubCommentLinks WHERE IssueLinkId IN (SELECT Id FROM GitHubIssueLinks WHERE RepositorySubscriptionId IN (SELECT Id FROM GitHubRepositories WHERE ConnectionId = $k0));",
            "DELETE FROM GitHubIssueWrites WHERE IssueLinkId IN (SELECT Id FROM GitHubIssueLinks WHERE RepositorySubscriptionId IN (SELECT Id FROM GitHubRepositories WHERE ConnectionId = $k0));",
            "DELETE FROM GitHubCommentCheckpoints WHERE IssueLinkId IN (SELECT Id FROM GitHubIssueLinks WHERE RepositorySubscriptionId IN (SELECT Id FROM GitHubRepositories WHERE ConnectionId = $k0));",
            "DELETE FROM GitHubIssueLinks WHERE RepositorySubscriptionId IN (SELECT Id FROM GitHubRepositories WHERE ConnectionId = $k0);",
            "DELETE FROM GitHubSyncCheckpoints WHERE RepositorySubscriptionId IN (SELECT Id FROM GitHubRepositories WHERE ConnectionId = $k0);",
            "DELETE FROM GitHubConnectionBackoff WHERE ConnectionId = $k0;",
            "DELETE FROM GitHubRepositories WHERE ConnectionId = $k0;",
            "DELETE FROM GitHubConnections WHERE Id = $k0;",
        ],
        "github_repository" =>
        [
            "DELETE FROM GitHubLabelTags WHERE RepositorySubscriptionId = $k0;",
            "DELETE FROM GitHubMilestoneProjects WHERE RepositorySubscriptionId = $k0;",
            "DELETE FROM GitHubChecklistItems WHERE IssueLinkId IN (SELECT Id FROM GitHubIssueLinks WHERE RepositorySubscriptionId = $k0);",
            "DELETE FROM GitHubCommentLinks WHERE IssueLinkId IN (SELECT Id FROM GitHubIssueLinks WHERE RepositorySubscriptionId = $k0);",
            "DELETE FROM GitHubIssueWrites WHERE IssueLinkId IN (SELECT Id FROM GitHubIssueLinks WHERE RepositorySubscriptionId = $k0);",
            "DELETE FROM GitHubCommentCheckpoints WHERE IssueLinkId IN (SELECT Id FROM GitHubIssueLinks WHERE RepositorySubscriptionId = $k0);",
            "DELETE FROM GitHubIssueLinks WHERE RepositorySubscriptionId = $k0;",
            "DELETE FROM GitHubSyncCheckpoints WHERE RepositorySubscriptionId = $k0;",
            "DELETE FROM GitHubRepositories WHERE Id = $k0;",
        ],
        "github_issue_link" =>
        [
            "DELETE FROM GitHubChecklistItems WHERE IssueLinkId = $k0;",
            "DELETE FROM GitHubCommentLinks WHERE IssueLinkId = $k0;",
            "DELETE FROM GitHubIssueWrites WHERE IssueLinkId = $k0;",
            "DELETE FROM GitHubCommentCheckpoints WHERE IssueLinkId = $k0;",
            "DELETE FROM GitHubIssueLinks WHERE Id = $k0;",
        ],
        // Link and natural-key rows, and rows nothing references: they leave alone.
        _ => [],
    };
}
