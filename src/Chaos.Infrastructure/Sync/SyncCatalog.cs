using System.Globalization;

namespace Chaos.Infrastructure.Sync;

/// <summary>How a table takes part in sync (PLAN2 §7.2).</summary>
public enum SyncMode
{
    /// <summary>Rows are upserted and tombstoned field-group by field-group, newest clock wins.</summary>
    Merge = 0,

    /// <summary>Rows are inserted once and never edited afterwards (history, records).</summary>
    AppendOnly = 1,

    /// <summary>The table stays on the device: bookkeeping, per-device state, rebuildable data.</summary>
    LocalOnly = 2,
}

/// <summary>How a duplicate under a unique index resolves (PLAN2 §7.6); every device makes the same choice.</summary>
public enum SyncUniqueRuleKind
{
    /// <summary>Legacy duplicates of a natural key: children move to the lowest-ID winner, the loser goes.</summary>
    RepointChildren = 0,

    /// <summary>Sibling tags with the same normalized name: merged like a tag merge, loser tombstoned.</summary>
    MergeTag = 1,

    /// <summary>Containers (and templates) with the same normalized name: the loser is renamed, nothing merges.</summary>
    RenameLoser = 2,

    /// <summary>Legacy daily pages for the same date: the loser's text is appended to the winner.</summary>
    AppendText = 3,

    /// <summary>One winner, the other retired with a notice, where combining makes no sense.</summary>
    KeepLowestRetireOther = 4,

    /// <summary>Profiles, Personal space and Inbox are adopted during device join (P14), never applied.</summary>
    Adopt = 5,
}

/// <summary>A named set of columns that always travel and merge together (PLAN2 §7.1).</summary>
public sealed record SyncFieldGroup(string Name, IReadOnlyList<string> Columns);

/// <summary>How one unique index of a synced table resolves duplicates created on two devices.</summary>
public sealed record SyncUniqueRule(string Index, SyncUniqueRuleKind Kind, string Reason);

/// <summary>A unique index a duplicate cannot violate: a deterministic ID (§7.5) or a per-entity invariant.</summary>
public sealed record SyncIndexExemption(string Index, string Reason);

/// <summary>
/// How one SQLite table takes part in sync. This is the single, hand-written, reviewed table
/// of PLAN2 §7.2: every client table appears here, every column is either in exactly one
/// field group, a local column, or <c>UpdatedAtUtc</c> (never in a group; on a remote apply it
/// becomes max(local, change wall time)). The guard test
/// <c>SyncCatalog_EveryTableAndColumnClassified</c> compares this against the migrated schema.
/// Judgment calls live in the comments next to their entries.
/// </summary>
public static class SyncCatalog
{
    public static readonly IReadOnlyList<SyncTable> Tables =
    [
        // ---- Identity and structure -------------------------------------------------------
        new SyncTable("Profiles", "profile", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["CreatedAtUtc"]),
                new("name", ["DisplayName"]),
                new("zone", ["ZoneId"]),
                new("locale", ["Locale"]),
                new("dates", ["DateFormat", "FirstWeekday"]),
                new("planning", ["PlanningTime", "SummaryTime", "MorningTime", "WeeklySummaryDay", "IncludeMeetingsInSummaries"]),
                new("reminders", ["DefaultReminderTime"]),
                new("quiet", ["QuietHoursEnabled", "QuietStart", "QuietEnd"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version", "CaptureShortcut", "PadShortcut", "KeepRunningInTray"],
            ApplyOrder: 10), // shortcuts and tray preference describe this computer (§7.2 rule 2)

        new SyncTable("Spaces", "space", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["OwnerProfileId", "Kind", "CreatedAtUtc"]),
                new("name", ["Name"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            UniqueRules: [new("UX_Spaces_PersonalPerOwner", SyncUniqueRuleKind.Adopt,
                "The Personal space of an account is canonical; duplicates are settled by device join (§7.6, P14).")],
            ApplyOrder: 20),

        new SyncTable("Containers", "container", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["SpaceId", "Kind", "IsInbox", "CreatedAtUtc"]),
                new("name", ["Name", "NormalizedName"]),
                new("position", ["Position"]),
                new("deadline", ["DeadlineDate"]),
                new("archived", ["ArchivedAtUtc"]),
                new("category", ["DefaultCategory"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            UniqueRules: [new("UX_Containers_ActiveNamePerSpace", SyncUniqueRuleKind.RenameLoser,
                "Two lists that happen to share a name stay separate; the loser is renamed (§7.6).")],
            ApplyOrder: 30),

        new SyncTable("TaskTemplates", "task_template", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["SpaceId", "CreatedAtUtc"]),
                new("name", ["Name", "NormalizedName"]),
                new("payload", ["Payload"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            UniqueRules: [new("UX_TaskTemplates_ActiveNamePerSpace", SyncUniqueRuleKind.RenameLoser,
                "Templates combine no better than containers: same name, loser renamed (§7.6).")],
            ApplyOrder: 35),

        new SyncTable("Sections", "section", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["ContainerId", "CreatedAtUtc"]),
                new("title", ["Title"]),
                new("position", ["Position"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            DeterministicIndexes: [new("UX_Sections_IdContainer",
                "Invariant: one row per entity id; a second (Id, ContainerId) pair cannot arise because applies are upserts keyed by Id.")],
            ApplyOrder: 40),

        new SyncTable("Tags", "tag", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["OwnerProfileId", "CreatedAtUtc"]),
                new("name", ["DisplayName", "NormalizedName"]),
                new("parent", ["ParentTagId"]),
                new("color", ["ColorKey"]),
                new("position", ["Position"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            UniqueRules: [new("UX_Tags_ActiveSiblingName", SyncUniqueRuleKind.MergeTag,
                "Sibling tags with the same normalized name merge, links deduplicated, loser tombstoned (§7.6).")],
            ApplyOrder: 50),

        // ---- Work -------------------------------------------------------------------------
        // P2 Kanban settings: deterministic id (boardsetting:{targetKind}:{targetId}) makes a
        // second row for the same target impossible, so the target unique index needs no rule.
        new SyncTable("BoardSettings", "board_setting", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["TargetKind", "TargetId", "CreatedAtUtc"]),
                new("grouping", ["GroupBy", "SwimlaneBy", "ColumnTagId", "SwimlaneTagId"]),
                new("limits", ["WipLimitsJson", "HiddenColumnsJson"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            DeterministicIndexes: [new("UX_BoardSettings_Target",
                "Deterministic ID: both devices derive boardsetting:{targetKind}:{targetId}, so the row upserts onto itself (§7.5).")],
            ApplyOrder: 55),
        // P3 saved views. The id is random, so two devices can create views with the same name;
        // there is no unique index on Name and nothing to merge.
        new SyncTable("SavedViews", "saved_view", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["SpaceId", "CreatedAtUtc"]),
                new("name", ["Name"]),
                new("look", ["Icon", "Color"]),
                new("pinned", ["Pinned"]),
                new("position", ["Position"]),
                new("definition", ["DefinitionJson"]),
                new("layout", ["LayoutJson"]),
                new("alerts", ["AlertsEnabled"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            ApplyOrder: 56),
        // P6 Google accounts. The refresh token is NOT a column: it lives in the secret store by account id.
        new SyncTable("GoogleAccounts", "google_account", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["Email", "Subject", "CreatedAtUtc"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            ApplyOrder: 54),
        // P5 calendar sources. The feed address is NOT a column: it lives in the secret store by source id.
        new SyncTable("CalendarSources", "calendar_source", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["Provider", "AccountRef", "ExternalId", "CreatedAtUtc"]),
                new("name", ["Name"]),
                new("look", ["Color"]),
                new("busy", ["CountsAsBusy"]),
                new("enabled", ["Enabled"]),
                new("owner", ["OwnerEmail"]),
                new("position", ["Position"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            ApplyOrder: 57),
        // A task made from a meeting. Deterministic id (calendareventlink:{taskId}): one link per task.
        new SyncTable("CalendarEventLinks", "calendar_event_link", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["TaskId", "Provider", "CalendarId", "EventId", "EventStartUtc", "EventTitle", "EventWebUrl", "CreatedAtUtc"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            DeterministicIndexes: [new("UX_CalendarEventLinks_Task",
                "Deterministic ID: both devices derive calendareventlink:{taskId}, so the row upserts onto itself (§7.5).")],
            ApplyOrder: 65),
        new SyncTable("CalendarEvents", "calendar_event", SyncMode.LocalOnly, ["SourceId", "ExternalId"], ApplyOrder: 532), // each device fetches its own cache (PLAN2 §10.2)
        new SyncTable("CalendarSourceState", "calendar_source_state", SyncMode.LocalOnly, ["SourceId"], ApplyOrder: 533), // this device's fetch state, ETag included
        new SyncTable("GoogleAccountState", "google_account_state", SyncMode.LocalOnly, ["AccountId"], ApplyOrder: 534), // this device's sign-in problems (P6)
        new SyncTable("Tasks", "task", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["SpaceId", "CreatedAtUtc", "RecurrenceOccurrenceKey"]),
                new("title", ["Title"]),
                new("notes", ["Notes"]),
                new("status", ["Status", "CompletedAtUtc", "CancelledAtUtc"]),
                new("place", ["ContainerId", "SectionId"]),
                new("position", ["Position"]),
                new("priority", ["Priority"]),
                new("estimate", ["EstimateMinutes"]),
                new("category", ["CapacityCategory"]),
                new("planned", ["PlannedDate", "PlannedTime", "PlannedZoneId"]),
                new("due", ["DueDate", "DueTime", "DueZoneId"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            ApplyOrder: 60),

        new SyncTable("Subtasks", "subtask", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["TaskId", "CreatedAtUtc"]),
                new("title", ["Title"]),
                new("notes", ["Notes"]),
                new("status", ["Status", "CompletedAtUtc", "CancelledAtUtc"]),
                new("position", ["Position"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            ApplyOrder: 70),

        new SyncTable("TaskTags", "task_tag", SyncMode.Merge, ["TaskId", "TagId"],
            Groups: [new("deleted", ["DeletedAtUtc"])],
            ApplyOrder: 80), // link tables: composite key, one tombstone group (§7.2 rule 6)

        new SyncTable("SubtaskTags", "subtask_tag", SyncMode.Merge, ["SubtaskId", "TagId"],
            Groups: [new("deleted", ["DeletedAtUtc"])],
            ApplyOrder: 90),

        // Task history travels with the task (§7.7 contrasts sync.merged entries, which stay
        // local, with the ordinary history the viewer shares); AggregateVersion counts per device.
        new SyncTable("TaskHistory", "task_history", SyncMode.AppendOnly, ["Id"],
            Groups: [new("record",
                ["TaskId", "OperationId", "EffectiveAtUtc", "Actor", "EventKind", "ChangedFields",
                 "SnapshotBefore", "SnapshotAfter", "RecordedAtUtc"])],
            LocalColumns: ["AggregateVersion"],
            ApplyOrder: 110),

        new SyncTable("DiaryEntries", "diary_entry", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["TaskId", "AuthorProfileId", "EntryAtUtc", "CreatedAtUtc"]),
                new("entry", ["RawMarkdown", "EditedAtUtc"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version", "Revision"], // Revision numbers this device's version rows (DiaryRevisions stay local)
            ApplyOrder: 100),

        new SyncTable("ScratchPadItems", "pad", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["SpaceId", "Kind", "CreatedAtUtc"]),
                new("text", ["RawMarkdown"]),
                new("position", ["Position"]),
                new("archived", ["ArchivedAtUtc"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version", "DraftVersion"],
            ApplyOrder: 120),

        new SyncTable("ScratchTaskLinks", "scratch_task_link", SyncMode.Merge, ["PadItemId", "TaskId"],
            Groups: [new("anchor", ["SourceRevision", "StartIndex", "EndIndex", "Quote", "ContextBefore", "ContextAfter", "RegionJson", "CreatedAtUtc"])],
            UniqueRules: [new("UX_ScratchTaskLinks_Task", SyncUniqueRuleKind.KeepLowestRetireOther,
                "A task anchors to one pad note; combining two anchors makes no sense, so the higher-ID link retires (§7.6).")],
            ApplyOrder: 130), // no DeletedAtUtc: a removal is a hard delete and travels as a purge

        // A drawing item's strokes: one row per item, the strokes merged whole (latest wins). The
        // preview attachment travels with them so a peer shows the picture before the strokes load.
        // No tombstone: the row goes with its pad item (SyncPurgeSql "pad").
        new SyncTable("PadDrawings", "pad_drawing", SyncMode.Merge, ["PadItemId"],
            Groups:
            [
                new("strokes", ["StrokesJson", "Width", "Height", "CreatedAtUtc"]),
                new("preview", ["PreviewAttachmentId"]),
            ],
            LocalColumns: ["Version", "Revision"],
            ApplyOrder: 125),

        // ---- Recurrence ---------------------------------------------------------------------
        new SyncTable("RecurrenceSeries", "recurrence_series", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["SpaceId", "ContainerId", "CreatedAtUtc"]),
                new("rule", ["RuleJson", "TemplateJson", "Revision"]), // the revision triggers bump them together
                new("ended", ["EndedAtUtc"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version", "ActionableCount"], // derived count is recomputed per device
            ApplyOrder: 160),

        new SyncTable("RecurrenceOccurrences", "recurrence_occurrence", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["SeriesId", "NominalKey", "ScheduleKey", "EffectiveLocal", "CreatedAtUtc"]),
                new("task", ["TaskId"]),
                new("state", ["State", "CompletedAtUtc"]),
                new("edit", ["EditSnapshotJson"]),
                new("series", ["SeriesRevision"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            UniqueRules:
            [
                new("UX_RecurrenceOccurrences_Active", SyncUniqueRuleKind.KeepLowestRetireOther,
                    "One active occurrence per series; the retired duplicate raises attention (§7.6)."),
                new("UX_RecurrenceOccurrences_Task", SyncUniqueRuleKind.KeepLowestRetireOther,
                    "One task per occurrence; the retired duplicate raises attention (§7.6)."),
                new("sqlite_autoindex_RecurrenceOccurrences_2", SyncUniqueRuleKind.RepointChildren,
                    "Legacy occurrence rows created before deterministic ids: same series and nominal key on two devices is one entity — the lowest-id row stays, the other is removed with its task and a notice (§7.5 prevents new ones)."),
            ],
            ApplyOrder: 170),

        new SyncTable("RecurrenceExceptions", "recurrence_exception", SyncMode.Merge, ["SeriesId", "NominalKey"],
            Groups: [new("exception", ["Kind", "PayloadJson"])],
            ApplyOrder: 180), // the row IS its natural key: two devices upsert the same row; removal is a hard delete

        // ---- Reminders and follow-ups -------------------------------------------------------
        new SyncTable("ReminderRules", "reminder_rule", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["ProfileId", "TaskId", "CreatedAtUtc"]),
                new("rule", ["Anchor", "OffsetMinutes", "AbsoluteAtUtc", "InsistentMinutes", "Enabled"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            ApplyOrder: 190), // OS notification jobs are derived per device (NotificationJobs stay local)

        new SyncTable("WaitingDetails", "waiting_detail", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["TaskId", "ProfileId", "CreatedAtUtc"]),
                new("person", ["Person", "PersonNormalized", "StartedAtUtc"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            UniqueRules: [new("UX_WaitingDetails_ActiveTask", SyncUniqueRuleKind.KeepLowestRetireOther,
                "One active waiting-for per task; combining makes no sense (§7.6).")],
            ApplyOrder: 200),

        new SyncTable("FollowUps", "follow_up", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["TaskId", "ProfileId", "CreatedAtUtc"]),
                new("kind", ["Kind", "FixedAtUtc"]),
                new("schedule", ["Amount", "Unit", "SnoozedUntilUtc", "DueAtUtc"]),
                new("resolved", ["ResolvedAtUtc"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            UniqueRules: [new("UX_FollowUps_ActiveTask", SyncUniqueRuleKind.KeepLowestRetireOther,
                "One active follow-up per task; combining makes no sense (§7.6).")],
            ApplyOrder: 210),

        // ---- Planning -----------------------------------------------------------------------
        new SyncTable("DayPlans", "day_plan", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["ProfileId", "LocalDate", "CreatedAtUtc"]),
                new("seed", ["SeededWeekPlanId", "SeededWeekRevision"]),
                new("initialized", ["InitializedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            UniqueRules: [new("UX_DayPlans_ProfileDate", SyncUniqueRuleKind.RepointChildren,
                "Legacy duplicates of (profile, date): items move to the lowest-ID winner (§7.5 prevents new ones).")],
            ApplyOrder: 220),

        new SyncTable("DayPlanItems", "day_plan_item", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["DayPlanId", "TaskId", "Kind", "Origin", "AddedAtUtc", "CreatedAtUtc"]),
                new("position", ["Position"]),
                new("removed", ["RemovedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            UniqueRules: [new("UX_DayPlanItems_PlanTask", SyncUniqueRuleKind.KeepLowestRetireOther,
                "One membership row per task and plan; the loser's membership is removed (RemovedAtUtc) (§7.5 prevents new ones).")],
            ApplyOrder: 230),

        new SyncTable("WeekPlans", "week_plan", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["ProfileId", "WeekStartDate", "CreatedAtUtc"]),
                new("state", ["State", "ConfirmedRevision", "ConfirmedAtUtc"]),
            ],
            LocalColumns: ["Version", "DraftPayload", "DraftRevision", "DraftBaseRevision"], // the draft is this device's editor state (§7.10)
            UniqueRules: [new("UX_WeekPlans_ProfileWeek", SyncUniqueRuleKind.RepointChildren,
                "Legacy duplicates of (profile, week): children move to the lowest-ID winner (§7.5 prevents new ones).")],
            ApplyOrder: 240),

        new SyncTable("WeekGoals", "week_goal", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["WeekPlanId", "CreatedAtUtc"]),
                new("title", ["Title"]),
                new("position", ["Position"]),
                new("project", ["ProjectId"]),
                new("outcome", ["Outcome"]),
            ],
            LocalColumns: ["Version"],
            ApplyOrder: 250),

        new SyncTable("WeekGoalTasks", "week_goal_task", SyncMode.Merge, ["GoalId", "TaskId"],
            Groups: [new("deleted", ["DeletedAtUtc"])],
            ApplyOrder: 260), // today a link only leaves with its goal (a purge); the tombstone covers applier-written removals

        new SyncTable("WeekAssignments", "week_assignment", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["WeekPlanId", "TaskId", "CreatedAtUtc"]),
                new("placement", ["LocalDate", "Position", "Origin"]),
            ],
            UniqueRules: [new("UX_WeekAssignments_PlanTask", SyncUniqueRuleKind.KeepLowestRetireOther,
                "One place per task and plan; the loser retires (§7.5 prevents new ones).")],
            ApplyOrder: 270),

        new SyncTable("AvailabilityRules", "availability_rule", SyncMode.Merge, ["ProfileId", "Weekday"],
            Groups: [new("minutes", ["TotalMinutes", "WorkMinutes"])],
            ApplyOrder: 280), // the row IS its natural key (§7.5 names the same identity); upserts cannot duplicate it

        new SyncTable("WeekAvailabilityOverrides", "week_availability_override", SyncMode.Merge, ["ProfileId", "LocalDate"],
            Groups: [new("minutes", ["TotalMinutes", "WorkMinutes"])],
            ApplyOrder: 290), // natural-key row, as AvailabilityRules

        // ---- Summaries and imports ------------------------------------------------------------
        new SyncTable("Summaries", "summary", SyncMode.AppendOnly, ["Id"],
            Groups: [new("record",
                ["ProfileId", "Kind", "PeriodStart", "PeriodEnd", "Revision", "PeriodStartUtc", "PeriodEndUtc",
                 "CapturedThroughUtc", "ZoneId", "Trigger", "Facts", "GeneratedAtUtc"])],
            LocalColumns: ["ResourceVersion", "PromptAtUtc", "PromptSeenAtUtc"], // prompt nudges are this device's UI state
            DeterministicIndexes: [new("UX_Summaries_PeriodRevision",
                "The summary id is deterministic: summary:{profileId}:{kind}:{periodStart}:{revision} (§7.5).")],
            ApplyOrder: 300),

        new SyncTable("ImportBatches", "import_batch", SyncMode.Merge, ["Id"],
            Groups: [new("record",
                ["ProfileId", "SourceKind", "SourceName", "FileFingerprint", "ImportedAtUtc", "CreatedCount", "SkippedCount"])],
            LocalColumns: ["BackupFileName"], // the safety backup file is this computer's; the table has no Version column
            ApplyOrder: 310), // part of data history; carried in archives (§7.2 judgment)

        new SyncTable("ImportedItems", "imported_item", SyncMode.Merge, ["ItemFingerprint"],
            Groups: [new("record", ["BatchId", "TaskId", "SourceKey"])],
            ApplyOrder: 320), // the fingerprint is the natural key: two imports of the same row upsert

        // ---- GitHub (§7.11): connections and mappings sync; runner state stays local ----------
        new SyncTable("GitHubConnections", "github_connection", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["SpaceId", "ProfileId", "WebBaseUrl", "ApiBaseUrl", "AccountId", "AccountLogin", "CreatedAtUtc"]),
                new("settings", ["PostDiaryByDefault", "CreateLabelsForTags"]),
                new("state", ["State"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version", "RateLimitResetUtc", "LastSyncStartedUtc", "LastSyncCompletedUtc", "LastErrorKind", "LastErrorMessage"],
            UniqueRules: [new("UX_GitHubConnections_ActivePerSpace", SyncUniqueRuleKind.KeepLowestRetireOther,
                "One active connection per space; combining makes no sense (§7.6).")],
            ApplyOrder: 330),

        new SyncTable("GitHubRepositories", "github_repository", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["ConnectionId", "RepositoryId", "Owner", "Name", "FullName", "HtmlUrl", "IsPrivate",
                     "TargetContainerId", "TargetSectionId", "CreatedAtUtc"]),
                new("settings", ["FilterJson", "MilestonesAsProjects", "TaskCategory"]),
                new("state", ["State"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version", "IssuesCursorUtc", "CommentsCursorUtc", "IssuesEtag", "CommentsEtag", "LabelsEtag",
                 "MilestonesEtag", "InitialImportDoneUtc", "LastSyncedUtc", "LastErrorKind", "LastErrorMessage"],
            UniqueRules: [new("UX_GitHubRepositories_ActiveRepository", SyncUniqueRuleKind.KeepLowestRetireOther,
                "One active mapping per repository and connection; combining makes no sense (§7.6).")],
            DeterministicIndexes: [new("sqlite_autoindex_GitHubRepositories_2",
                "Invariant: one row per entity id; the (Id, ConnectionId) pair cannot duplicate through upserts.")],
            ApplyOrder: 340),

        new SyncTable("GitHubIssueLinks", "github_issue_link", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["RepositorySubscriptionId", "ConnectionId", "IssueId", "IssueNodeId", "IssueNumber", "HtmlUrl", "CreatedAtUtc"]),
                new("base", ["BaseJson", "BaseRemoteUpdatedUtc"]),
                new("state", ["State", "TaskId", "PlacementManaged"]),
            ],
            // LocalDirty/LocalDirtySinceUtc mark this runner device's unpushed edits (§7.11); the
            // assignee intent belongs to the device that queues the GitHub write.
            LocalColumns: ["Version", "IssueEtag", "LocalAssigneeIntent", "LocalDirty", "LocalDirtySinceUtc",
                 "LastSyncedUtc", "ConflictNoticeUtc", "LastErrorKind", "LastErrorMessage", "ErrorAttempts", "NextAttemptUtc"],
            UniqueRules:
            [
                new("UX_GitHubIssueLinks_Task", SyncUniqueRuleKind.KeepLowestRetireOther,
                    "A task links to one issue; combining makes no sense (§7.6)."),
                new("sqlite_autoindex_GitHubIssueLinks_2", SyncUniqueRuleKind.KeepLowestRetireOther,
                    "One link per (connection, issue); two devices linking the same issue keep the lowest id (§7.6)."),
            ],
            ApplyOrder: 350),

        new SyncTable("GitHubChecklistItems", "github_checklist_item", SyncMode.Merge, ["SubtaskId"],
            Groups: [new("base", ["IssueLinkId", "BaseOrdinal", "BaseText", "BaseChecked"])],
            ApplyOrder: 360), // keyed by the (deterministic) subtask id; refresh rewrites the row in place

        new SyncTable("GitHubCommentLinks", "github_comment_link", SyncMode.Merge, ["DiaryEntryId"],
            Groups:
            [
                new("create", ["IssueLinkId", "CommentId", "AuthorLogin", "HtmlUrl", "Origin"]),
                new("state", ["State", "BaseBody", "BaseRemoteUpdatedUtc"]),
            ],
            LocalColumns: ["LastErrorKind", "LastErrorMessage", "ErrorAttempts", "NextAttemptUtc"],
            UniqueRules: [new("UX_GitHubCommentLinks_Comment", SyncUniqueRuleKind.KeepLowestRetireOther,
                "One comment link per (issue link, comment); combining makes no sense (§7.6).")],
            ApplyOrder: 370),

        new SyncTable("GitHubLabelTags", "github_label_tag", SyncMode.Merge, ["RepositorySubscriptionId", "LabelId"],
            Groups: [new("mapping", ["LabelName", "LabelColor", "TagId"])],
            ApplyOrder: 380), // natural-key row; removal is a hard delete that travels as a purge

        new SyncTable("GitHubMilestoneProjects", "github_milestone_project", SyncMode.Merge, ["RepositorySubscriptionId", "MilestoneNumber"],
            Groups: [new("mapping", ["MilestoneId", "ContainerId", "BaseTitle", "BaseDueOn"])],
            ApplyOrder: 390), // natural-key row, as GitHubLabelTags

        // ---- Pages -----------------------------------------------------------------------------
        new SyncTable("Pages", "page", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["SpaceId", "DailyDate", "CreatedAtUtc"]),
                new("title", ["Title"]),
                new("text", ["RawMarkdown"]),
                new("tree", ["ParentPageId"]),
                new("position", ["Position"]),
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version", "DraftVersion"],
            UniqueRules: [new("UX_Pages_SpaceDaily", SyncUniqueRuleKind.AppendText,
                "Legacy daily pages for the same date: the loser's text is appended to the winner (§7.5 prevents new ones).")],
            ApplyOrder: 400),

        new SyncTable("PageTags", "page_tag", SyncMode.Merge, ["PageId", "TagId"],
            Groups: [new("deleted", ["DeletedAtUtc"])],
            ApplyOrder: 430),

        new SyncTable("PageTaskLinks", "page_task_link", SyncMode.Merge, ["PageId", "TaskId"],
            Groups: [new("anchor", ["SourceRevision", "StartIndex", "EndIndex", "Quote", "ContextBefore", "ContextAfter", "CreatedAtUtc"])],
            UniqueRules: [new("UX_PageTaskLinks_Task", SyncUniqueRuleKind.KeepLowestRetireOther,
                "A task anchors to one page selection; combining makes no sense (§7.6).")],
            ApplyOrder: 420), // no DeletedAtUtc: a removal is a hard delete and travels as a purge

        // ---- Attachments (P9) ------------------------------------------------------------------
        // The row syncs; the bytes do not (P16 moves them). OwnerId is polymorphic, so there is no
        // foreign key; an owner's purge removes its attachments (SyncPurgeSql).
        new SyncTable("Attachments", "attachment", SyncMode.Merge, ["Id"],
            Groups:
            [
                new("create", ["SpaceId", "OwnerKind", "OwnerId", "CreatedAtUtc"]),
                new("name", ["FileName", "OriginalFileName"]),
                new("file", ["MediaType", "ByteSize", "Sha256", "Width", "Height", "DurationMs"]), // changes only when an image is edited (crop, rotate, resize), and then all together
                new("deleted", ["DeletedAtUtc"]),
            ],
            LocalColumns: ["Version"],
            ApplyOrder: 440),

        new SyncTable("ScratchPadTags", "pad_tag", SyncMode.Merge, ["PadItemId", "TagId"],
            Groups: [new("deleted", ["DeletedAtUtc"])],
            ApplyOrder: 140),

        // ---- Local-only: bookkeeping, per-device state, rebuildable data -----------------------
        new SyncTable("NotificationJobs", "notification_job", SyncMode.LocalOnly, ["Id"], ApplyOrder: 500), // derived per device (§7.4), OS schedule columns included
        new SyncTable("SavedViewMatches", "saved_view_match", SyncMode.LocalOnly, ["ViewId", "TaskId"], ApplyOrder: 530), // each device evaluates its own alerts (PLAN2 §11.2)
        new SyncTable("ViewAlerts", "view_alert", SyncMode.LocalOnly, ["Id"], ApplyOrder: 531), // raised and delivered per device (PLAN2 §11.2)
        new SyncTable("UndoEntries", "undo_entry", SyncMode.LocalOnly, ["Id"], ApplyOrder: 501), // undo stays local (§7.10)
        new SyncTable("OperationReceipts", "operation_receipt", SyncMode.LocalOnly, ["ProfileId", "OperationId"], ApplyOrder: 502), // idempotency per device
        new SyncTable("BackupCatalog", "backup_catalog", SyncMode.LocalOnly, ["Id"], ApplyOrder: 503), // this device's backup log
        new SyncTable("SearchDocuments", "search_document", SyncMode.LocalOnly, ["RowId"], ApplyOrder: 504), // rebuildable search index
        new SyncTable("SearchIndex", "search_index", SyncMode.LocalOnly, [], ApplyOrder: 506), // fts5 index, rebuilt; shadow tables are internal
        new SyncTable("SearchIndexState", "search_index_state", SyncMode.LocalOnly, ["Id"], ApplyOrder: 507),
        new SyncTable("DiaryRevisions", "diary_revision", SyncMode.LocalOnly, ["EntryId", "Revision"], ApplyOrder: 508), // per-device revision numbering; devices share the current text (§7.7 style)
        new SyncTable("WeekPlanRevisions", "week_plan_revision", SyncMode.LocalOnly, ["WeekPlanId", "Revision"], ApplyOrder: 509), // per-device snapshot numbering
        new SyncTable("RecurrenceSeriesRevisions", "recurrence_series_revision", SyncMode.LocalOnly, ["SeriesId", "Revision"], ApplyOrder: 510), // per-device revision numbering
        new SyncTable("PageRevisions", "page_revision", SyncMode.LocalOnly, ["PageId", "Number"], ApplyOrder: 511), // per-device version history; sync.merged writes a local revision (§7.7)
        new SyncTable("AttachmentLocalState", "attachment_local_state", SyncMode.LocalOnly, ["Sha256"], ApplyOrder: 539), // whether this device holds the bytes (PLAN2 §9.1)
        new SyncTable("GitHubSyncRuns", "github_sync_run", SyncMode.LocalOnly, ["Id"], ApplyOrder: 512), // runner diagnostics (§7.11)
        new SyncTable("GitHubIssueWrites", "github_issue_write", SyncMode.LocalOnly, ["IssueLinkId"], ApplyOrder: 513), // only the runner device queues GitHub writes (§7.11)
        new SyncTable("GitHubCommentCheckpoints", "github_comment_checkpoint", SyncMode.LocalOnly, ["IssueLinkId"], ApplyOrder: 514), // runner pagination state (§7.11)
        new SyncTable("GitHubSyncCheckpoints", "github_sync_checkpoint", SyncMode.LocalOnly, ["RepositorySubscriptionId", "Kind"], ApplyOrder: 515), // runner pagination state (§7.11)
        new SyncTable("GitHubConnectionBackoff", "github_connection_backoff", SyncMode.LocalOnly, ["ConnectionId"], ApplyOrder: 516), // runner retry state (§7.11)
        new SyncTable("ChecklistConversions", "checklist_conversion", SyncMode.LocalOnly, ["TaskId", "ItemKey"], ApplyOrder: 517), // idempotency marker tied to device-random subtask ids
        new SyncTable("SchemaMigrations", "schema_migration", SyncMode.LocalOnly, ["Version"], ApplyOrder: 518), // rebuilt by the app
        new SyncTable("DeviceSettings", "device_setting", SyncMode.LocalOnly, ["Key"], ApplyOrder: 519), // this device's identity (§7.1); never exported
        new SyncTable("SyncPending", "sync_pending", SyncMode.LocalOnly, ["Kind", "EntityKey", "GroupName"], ApplyOrder: 520), // capture queue (§7.3)
        new SyncTable("SyncApplyState", "sync_apply_state", SyncMode.LocalOnly, ["Id"], ApplyOrder: 521), // apply flag (§7.4)
        new SyncTable("SyncState", "sync_state", SyncMode.LocalOnly, ["Id"], ApplyOrder: 522), // account binding, cursor, trigger hash
        new SyncTable("SyncClocks", "sync_clock", SyncMode.LocalOnly, ["Kind", "EntityKey", "GroupName"], ApplyOrder: 523), // per-entity clocks
        new SyncTable("SyncOutbox", "sync_outbox", SyncMode.LocalOnly, ["Seq"], ApplyOrder: 524), // outgoing queue
        new SyncTable("SyncInboxParked", "sync_inbox_parked", SyncMode.LocalOnly, ["ChangeId"], ApplyOrder: 525), // parked remote changes (§7.8)
        new SyncTable("SyncAttention", "sync_attention", SyncMode.LocalOnly, ["Id"], ApplyOrder: 526), // notices for the person (§7.9)
        new SyncTable("ActivityLog", "activity_log", SyncMode.LocalOnly, ["Seq"], ApplyOrder: 535), // system event stream (PLAN2 §6.4)
        new SyncTable("ActivityLogEntities", "activity_log_entity", SyncMode.LocalOnly, ["Seq", "Kind", "EntityKey"], ApplyOrder: 536),
        new SyncTable("ActivityCursors", "activity_cursor", SyncMode.LocalOnly, ["Consumer"], ApplyOrder: 537),
        new SyncTable("JobRuns", "job_run", SyncMode.LocalOnly, ["Id"], ApplyOrder: 538), // scheduled-pass diagnostics
    ];

    public static SyncTable? Find(string table) =>
        Tables.FirstOrDefault(candidate => string.Equals(candidate.Table, table, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tables in apply order, LocalOnly last (the applier skips them).</summary>
    public static IEnumerable<SyncTable> Ordered() => Tables.OrderBy(table => table.ApplyOrder);
}

/// <summary>One table's shape and role in sync (PLAN2 §7.2); see <see cref="SyncCatalog"/>.</summary>
public sealed record SyncTable(
    string Table,
    string Kind,
    SyncMode Mode,
    IReadOnlyList<string> Key,
    IReadOnlyList<SyncFieldGroup>? Groups = null,
    IReadOnlyList<string>? LocalColumns = null,
    IReadOnlyList<SyncUniqueRule>? UniqueRules = null,
    IReadOnlyList<SyncIndexExemption>? DeterministicIndexes = null,
    int ApplyOrder = 0)
{
    public IReadOnlyList<SyncFieldGroup> Groups { get; init; } = Groups ?? [];
    public IReadOnlyList<string> LocalColumns { get; init; } = LocalColumns ?? [];
    public IReadOnlyList<SyncUniqueRule> UniqueRules { get; init; } = UniqueRules ?? [];
    public IReadOnlyList<SyncIndexExemption> DeterministicIndexes { get; init; } = DeterministicIndexes ?? [];
}
