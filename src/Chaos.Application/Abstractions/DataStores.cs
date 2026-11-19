using Chaos.Application.Contracts;

namespace Chaos.Application.Abstractions;

/// <summary>
/// Typed stores over one bounded unit; no arbitrary SQL from the UI layer (PLAN §4).
/// A session is either a write transaction (ExecuteAsync) or a read snapshot (QueryAsync).
/// </summary>
public interface IDataSession
{
    IProfileStore Profiles { get; }
    ISpaceStore Spaces { get; }
    IContainerStore Containers { get; }
    ISectionStore Sections { get; }
    ITaskStore Tasks { get; }
    ISubtaskStore Subtasks { get; }
    ITemplateStore Templates { get; }
    ITagStore Tags { get; }
    ITaskQueryStore Queries { get; }
    ITaskHistoryStore History { get; }
    IDiaryStore Diary { get; }
    IUndoStore Undo { get; }
    IChecklistConversionStore ChecklistConversions { get; }
    IPadStore Pad { get; }
    IPageStore Pages { get; }
    IRecurrenceStore Recurrence { get; }
    IReminderRuleStore ReminderRules { get; }
    IWaitingStore Waiting { get; }
    INotificationJobStore Notifications { get; }
    IDayPlanStore DayPlans { get; }
    IWeekPlanStore Weeks { get; }
    ISummaryStore Summaries { get; }
    IOperationReceiptStore Receipts { get; }
    IBackupCatalogStore Backups { get; }
    IImportStore Imports { get; }
    IGitHubStore GitHub { get; }
    IBoardStore Boards { get; }
    ISavedViewStore SavedViews { get; }
    ICalendarStore Calendars { get; }
    IAttachmentStore Attachments { get; }
    /// <summary>Device-only writers note their changes here (PLAN2 §6.4 rule 3).</summary>
    Chaos.Application.Activity.IActivityRecorder Activity { get; }
}

/// <summary>My Day plans and their membership rows (PLAN §10), within the caller's transaction.</summary>
public interface IDayPlanStore
{
    Task<DayPlanRecord?> FindAsync(string profileId, DateOnly localDate, CancellationToken ct);
    /// <summary>
    /// Inserts the plan unless one already exists for the same profile and date (the unique
    /// key settles midnight/restart races).
    /// </summary>
    /// <returns>True when this call created the plan.</returns>
    Task<bool> TryAddAsync(DayPlanRecord plan, CancellationToken ct);
    /// <summary>Items ordered by (Position, Id); removed ones only when asked.</summary>
    Task<IReadOnlyList<DayPlanItemRecord>> ListItemsAsync(string dayPlanId, bool includeRemoved, CancellationToken ct);
    /// <summary>The task's row in this plan, active or removed.</summary>
    Task<DayPlanItemRecord?> FindItemAsync(string dayPlanId, string taskId, CancellationToken ct);
    Task AddItemAsync(DayPlanItemRecord item, CancellationToken ct);
    Task<DayPlanItemRecord?> UpdateItemAsync(DayPlanItemRecord item, long expectedVersion, CancellationToken ct);
    /// <summary>Records which confirmed week revision this day now follows (seeding and reconciliation).</summary>
    Task<DayPlanRecord?> UpdateSeedAsync(DayPlanRecord plan, long expectedVersion, CancellationToken ct);
    /// <summary>Plans of one profile for dates in the inclusive range, ordered by date.</summary>
    Task<IReadOnlyList<DayPlanRecord>> ListRangeAsync(string profileId, DateOnly from, DateOnly to, CancellationToken ct);
    /// <summary>
    /// Open, nondeleted tasks of active containers in the space whose active recurrence
    /// occurrence falls on this local date.
    /// </summary>
    Task<IReadOnlyList<string>> ListRecurrenceTaskIdsDueOnAsync(string spaceId, DateOnly localDate, CancellationToken ct);
}

/// <summary>
/// Week plans, their confirmed goals and assignments, revision snapshots and availability
/// (PLAN §10 My Week), within the caller's transaction. Migration 009 holds the constraints.
/// </summary>
public interface IWeekPlanStore
{
    Task<WeekPlanRecord?> GetAsync(string weekPlanId, CancellationToken ct);
    Task<WeekPlanRecord?> FindByStartAsync(string profileId, DateOnly weekStart, CancellationToken ct);
    /// <summary>Plans that are not superseded and include at least one date of the inclusive range, ordered by start.</summary>
    Task<IReadOnlyList<WeekPlanRecord>> ListOverlappingAsync(string profileId, DateOnly from, DateOnly to, CancellationToken ct);
    /// <returns>False when a plan with the same profile and start already exists.</returns>
    Task<bool> TryAddAsync(WeekPlanRecord plan, CancellationToken ct);
    Task<WeekPlanRecord?> UpdateAsync(WeekPlanRecord plan, long expectedVersion, CancellationToken ct);

    /// <summary>Confirmed goals ordered by (Position, Id), each with its linked task IDs.</summary>
    Task<IReadOnlyList<WeekGoalRecord>> ListGoalsAsync(string weekPlanId, CancellationToken ct);
    Task<WeekGoalRecord?> GetGoalAsync(string goalId, CancellationToken ct);
    Task<WeekGoalRecord?> UpdateGoalOutcomeAsync(WeekGoalRecord goal, long expectedVersion, CancellationToken ct);
    /// <summary>Confirmed assignments ordered by (day, Position, Id); pool items (no day) last.</summary>
    Task<IReadOnlyList<WeekAssignmentRecord>> ListAssignmentsAsync(string weekPlanId, CancellationToken ct);
    /// <summary>Replaces every confirmed goal, goal link and assignment of the plan with the given rows. A goal's links are removed with the goal.</summary>
    Task ReplaceContentAsync(string weekPlanId, IReadOnlyList<WeekGoalRecord> goals, IReadOnlyList<WeekAssignmentRecord> assignments, CancellationToken ct);
    Task AddRevisionAsync(string weekPlanId, long revision, string snapshot, DateTimeOffset confirmedAtUtc, CancellationToken ct);
    Task<long> CountRevisionsAsync(string weekPlanId, CancellationToken ct);

    /// <summary>Stored weekday defaults of the profile (missing weekdays use the built-in default).</summary>
    Task<IReadOnlyList<AvailabilityRecord>> ListRulesAsync(string profileId, CancellationToken ct);
    Task UpsertRuleAsync(string profileId, DayOfWeek weekday, int totalMinutes, int? workMinutes, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<AvailabilityRecord>> ListOverridesAsync(string profileId, DateOnly from, DateOnly to, CancellationToken ct);
    Task UpsertOverrideAsync(string profileId, DateOnly date, int totalMinutes, int? workMinutes, DateTimeOffset now, CancellationToken ct);
    Task DeleteOverrideAsync(string profileId, DateOnly date, CancellationToken ct);
}

/// <summary>
/// Stored summary revisions and the period reads that build them (PLAN §12, work package 11).
/// Summaries are derived records: they are not part of undo and never edited after insert,
/// except for the prompt-seen mark.
/// </summary>
public interface ISummaryStore
{
    Task<SummaryRecord?> GetAsync(string id, CancellationToken ct);
    /// <summary>The newest revision of one period, if any.</summary>
    Task<SummaryRecord?> FindLatestAsync(string profileId, Chaos.Domain.Summaries.SummaryKind kind, DateOnly periodStart, CancellationToken ct);
    /// <summary>All revisions of one period, newest first.</summary>
    Task<IReadOnlyList<SummaryRecord>> ListRevisionsAsync(string profileId, Chaos.Domain.Summaries.SummaryKind kind, DateOnly periodStart, CancellationToken ct);
    /// <returns>False when the same period and revision already exist (a concurrent generation won).</returns>
    Task<bool> TryAddAsync(SummaryRecord row, CancellationToken ct);
    Task MarkPromptSeenAsync(string id, DateTimeOffset seenAtUtc, CancellationToken ct);
    /// <summary>
    /// Stored summaries: the newest revision of each period, ordered by (PeriodEnd, Kind)
    /// descending. The cursor is exclusive: rows strictly before (endBefore, kindBefore).
    /// </summary>
    Task<IReadOnlyList<SummaryRecord>> ListLatestAsync(string profileId, DateOnly? endBefore, Chaos.Domain.Summaries.SummaryKind? kindBefore, int limit, CancellationToken ct);
    /// <summary>Newest revisions whose prompt time has come and that were not opened or dismissed, newest first.</summary>
    Task<IReadOnlyList<SummaryRecord>> ListPromptsDueAsync(string profileId, DateTimeOffset now, CancellationToken ct);
    /// <summary>History rows of the space's tasks effective at or after <paramref name="fromUtc"/>, in recorded order.</summary>
    Task<IReadOnlyList<TaskHistoryRecord>> ListHistorySinceAsync(string spaceId, DateTimeOffset fromUtc, CancellationToken ct);
    /// <summary>Nondeleted diary entries of the space's tasks written in [fromUtc, beforeUtc), oldest first.</summary>
    Task<IReadOnlyList<DiaryEntryRecord>> ListDiaryEntriesAsync(string spaceId, DateTimeOffset fromUtc, DateTimeOffset beforeUtc, CancellationToken ct);
}

/// <summary>
/// Waiting details and follow-up rules (PLAN §11), within the caller's transaction. Versioned
/// writes are recorded for history and undo; the derived follow-up due instant is not.
/// </summary>
public interface IWaitingStore
{
    /// <summary>The row even when tombstoned (undo and history need it).</summary>
    Task<WaitingDetailRecord?> GetAsync(string id, CancellationToken ct);
    Task<WaitingDetailRecord?> FindActiveAsync(string taskId, CancellationToken ct);
    Task AddAsync(WaitingDetailRecord row, CancellationToken ct);
    Task<WaitingDetailRecord?> UpdateAsync(WaitingDetailRecord row, long expectedVersion, CancellationToken ct);

    /// <summary>The rule even when resolved or tombstoned.</summary>
    Task<FollowUpRecord?> GetFollowUpAsync(string id, CancellationToken ct);
    /// <summary>The task's unresolved, nondeleted follow-up rule, if any.</summary>
    Task<FollowUpRecord?> FindActiveFollowUpAsync(string taskId, CancellationToken ct);
    Task AddFollowUpAsync(FollowUpRecord row, CancellationToken ct);
    Task<FollowUpRecord?> UpdateFollowUpAsync(FollowUpRecord row, long expectedVersion, CancellationToken ct);
    /// <summary>Stores the derived due instant (null = suspended) without changing the rule's version.</summary>
    Task SetFollowUpDueAsync(string followUpId, DateTimeOffset? dueAtUtc, CancellationToken ct);
    /// <summary>Unresolved, nondeleted follow-up rules of one profile, earliest due first (suspended last).</summary>
    Task<IReadOnlyList<FollowUpRecord>> ListActiveFollowUpsAsync(string profileId, CancellationToken ct);
}

/// <summary>
/// Desired and delivered notifications (PLAN §11 NotificationJobs/ReminderDeliveries). Rows are
/// derived from rules and follow-ups, so they are not part of undo; state changes are guarded
/// by the state the caller read, inside the serialized write transaction.
/// </summary>
public interface INotificationJobStore
{
    Task<ReminderAcknowledgement?> GetAcknowledgementAsync(NotificationSourceKind kind, string sourceId, DateTimeOffset target, CancellationToken ct);
    Task SaveAcknowledgementAsync(ReminderAcknowledgement acknowledgement, CancellationToken ct);
    Task<string?> GetAppliedAcknowledgementAsync(string id, CancellationToken ct);
    Task MarkAcknowledgementAppliedAsync(string id, string revision, CancellationToken ct);
    Task<NotificationJobRecord?> GetAsync(string id, CancellationToken ct);
    /// <summary>The newest row of a source: highest generation, then highest sequence.</summary>
    Task<NotificationJobRecord?> FindLatestAsync(NotificationSourceKind kind, string sourceId, CancellationToken ct);
    Task<IReadOnlyList<NotificationJobRecord>> ListBySourceAsync(NotificationSourceKind kind, string sourceId, CancellationToken ct);
    Task<IReadOnlyList<NotificationJobRecord>> ListPendingByTaskAsync(string taskId, CancellationToken ct);
    Task<IReadOnlyList<NotificationJobRecord>> ListActiveDeliveredByTaskAsync(string taskId, CancellationToken ct);
    Task AddAsync(NotificationJobRecord row, CancellationToken ct);
    /// <returns>False when the row is no longer in <paramref name="expectedState"/> (a concurrent action won).</returns>
    Task<bool> UpdateAsync(NotificationJobRecord row, NotificationJobState expectedState, CancellationToken ct);
    /// <summary>Pending rows of the profile whose delivery instant has come, oldest first.</summary>
    Task<IReadOnlyList<NotificationJobRecord>> ListDueAsync(string profileId, DateTimeOffset now, int limit, CancellationToken ct);
    /// <summary>Rows the OS reconciler must act on: pending rows not scheduled yet or failed, and cancelled or delivered rows still scheduled.</summary>
    Task<IReadOnlyList<NotificationJobRecord>> ListOsWorkAsync(string profileId, int limit, CancellationToken ct);
    /// <summary>Delivered rows (any later state), newest delivery first: the notification center.</summary>
    Task<IReadOnlyList<NotificationJobRecord>> ListDeliveredAsync(string profileId, int limit, CancellationToken ct);
    /// <summary>Tasks of the profile that have an active reminder rule, an active follow-up or a pending notification.</summary>
    Task<IReadOnlyList<string>> ListTasksWithSourcesAsync(string profileId, CancellationToken ct);
}

public interface IReminderRuleStore
{
    Task<ReminderRuleRecord?> GetAsync(string id, CancellationToken ct);
    Task<IReadOnlyList<ReminderRuleRecord>> ListByTaskAsync(string taskId, CancellationToken ct);
    Task AddAsync(ReminderRuleRecord row, CancellationToken ct);
    Task<ReminderRuleRecord?> UpdateAsync(ReminderRuleRecord row, long expectedVersion, CancellationToken ct);
}

/// <summary>Versioned series and occurrence rows within the caller's transaction.</summary>
public interface IRecurrenceStore
{
    Task<RecurrenceSeriesRecord?> GetSeriesAsync(string id, CancellationToken ct);
    Task<long> NextRevisionAsync(string seriesId, CancellationToken ct);
    Task<RecurrenceOccurrenceRecord?> GetOccurrenceAsync(string id, CancellationToken ct);
    Task<RecurrenceOccurrenceRecord?> FindByKeyAsync(string seriesId, string nominalKey, CancellationToken ct);
    Task<IReadOnlyList<RecurrenceOccurrenceRecord>> ListActiveBySpaceAsync(string spaceId, CancellationToken ct);
    Task<RecurrenceOccurrenceRecord?> FindByTaskAsync(string taskId, CancellationToken ct);
    Task<IReadOnlyList<RecurrenceOccurrenceRecord>> ListOccurrencesAsync(string seriesId, CancellationToken ct);
    Task AddSeriesAsync(RecurrenceSeriesRecord row, CancellationToken ct);
    Task<RecurrenceSeriesRecord?> UpdateSeriesAsync(RecurrenceSeriesRecord row, long expectedVersion, CancellationToken ct);
    Task AddOccurrenceAsync(RecurrenceOccurrenceRecord row, CancellationToken ct);
    Task<RecurrenceOccurrenceRecord?> UpdateOccurrenceAsync(RecurrenceOccurrenceRecord row, long expectedVersion, CancellationToken ct);
}

/// <summary>
/// Diary entries (PLAN §9). Add and Update also append the saved state as a revision and
/// keep the entry's search document current, in the caller's transaction.
/// </summary>
public interface IDiaryStore
{
    Task<DiaryEntryRecord?> GetAsync(string entryId, CancellationToken ct);
    Task<DiaryEntryRecord?> GetIncludingDeletedAsync(string entryId, CancellationToken ct);
    /// <summary>Entries of one task ordered by (EntryAtUtc, Id); tombstoned entries only when asked.</summary>
    Task<IReadOnlyList<DiaryEntryRecord>> ListByTaskAsync(string taskId, bool includeDeleted, CancellationToken ct);
    Task AddAsync(DiaryEntryRecord entry, CancellationToken ct);
    Task<DiaryEntryRecord?> UpdateAsync(DiaryEntryRecord updated, long expectedVersion, CancellationToken ct);
    /// <summary>All saved states of one entry, oldest first.</summary>
    Task<IReadOnlyList<DiaryRevisionRecord>> ListRevisionsAsync(string entryId, CancellationToken ct);
}

/// <summary>
/// Personal scratch pad items and the task backlinks made from them (PLAN §9). Add and Update
/// keep the item's search document current in the caller's transaction; archived items stay
/// searchable, tombstoned ones do not.
/// </summary>
public interface IPadStore
{
    Task<PadItemRecord?> GetAsync(string itemId, CancellationToken ct);
    Task<PadItemRecord?> GetIncludingDeletedAsync(string itemId, CancellationToken ct);
    /// <summary>Active items of one space ordered by (Position, Id); archived ones only when asked.</summary>
    Task<IReadOnlyList<PadItemRecord>> ListBySpaceAsync(string spaceId, bool archived, CancellationToken ct);
    Task<IReadOnlyList<PadItemRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct);
    Task AddAsync(PadItemRecord item, CancellationToken ct);
    Task<PadItemRecord?> UpdateAsync(PadItemRecord updated, long expectedVersion, CancellationToken ct);
    Task<long> GetNextPositionAsync(string spaceId, CancellationToken ct);
    Task AddTaskLinkAsync(ScratchTaskLinkRecord link, CancellationToken ct);
    /// <summary>Tasks made from one item, oldest first.</summary>
    Task<IReadOnlyList<ScratchTaskLinkRecord>> ListTaskLinksAsync(string itemId, CancellationToken ct);
    Task<ScratchTaskLinkRecord?> FindTaskLinkAsync(string taskId, CancellationToken ct);
    /// <summary>IDs of active items in the space whose text matches the search words, best match first.</summary>
    Task<IReadOnlyList<string>> SearchAsync(string spaceId, string text, int limit, CancellationToken ct);
    Task<PadDrawingRecord?> GetDrawingAsync(string itemId, CancellationToken ct);
    Task AddDrawingAsync(PadDrawingRecord drawing, CancellationToken ct);
    /// <summary>Replaces the strokes and preview when the stored Version is <paramref name="expectedVersion"/>; null otherwise.</summary>
    Task<PadDrawingRecord?> UpdateDrawingAsync(PadDrawingRecord updated, long expectedVersion, CancellationToken ct);
}

/// <summary>
/// Pages, their versions and their task backlinks (SPEC Pages), within the caller's
/// transaction. Every add/update also refreshes the page's search document and its tags from
/// front matter, so derived data always agrees with the row (undo included).
/// </summary>
public interface IPageStore
{
    Task<PageRecord?> GetAsync(string pageId, CancellationToken ct);
    Task<PageRecord?> GetIncludingDeletedAsync(string pageId, CancellationToken ct);
    /// <summary>Active pages of one space (tree and daily), ordered by (Position, Id).</summary>
    Task<IReadOnlyList<PageRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct);
    Task<IReadOnlyList<PageRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct);
    Task<PageRecord?> FindDailyAsync(string spaceId, DateOnly date, CancellationToken ct);
    Task AddAsync(PageRecord page, CancellationToken ct);
    Task<PageRecord?> UpdateAsync(PageRecord updated, long expectedVersion, CancellationToken ct);
    /// <summary>The position after the last active sibling under <paramref name="parentPageId"/> (null: top level).</summary>
    Task<long> GetNextPositionAsync(string spaceId, string? parentPageId, CancellationToken ct);

    /// <summary>Versions newest first.</summary>
    Task<IReadOnlyList<PageRevisionRecord>> ListRevisionsAsync(string pageId, CancellationToken ct);
    Task<PageRevisionRecord?> GetRevisionAsync(string pageId, long number, CancellationToken ct);
    Task AddRevisionAsync(PageRevisionRecord revision, CancellationToken ct);
    Task UpdateRevisionAsync(PageRevisionRecord revision, CancellationToken ct);

    Task AddTaskLinkAsync(PageTaskLinkRecord link, CancellationToken ct);
    Task<IReadOnlyList<PageTaskLinkRecord>> ListTaskLinksAsync(string pageId, CancellationToken ct);
    Task<PageTaskLinkRecord?> FindTaskLinkAsync(string taskId, CancellationToken ct);

    /// <summary>IDs of active pages in the space whose title or text matches the search words, best match first.</summary>
    Task<IReadOnlyList<string>> SearchAsync(string spaceId, string text, int limit, CancellationToken ct);
    /// <summary>Active pages of the space whose text contains any of the given strings (case-insensitive).</summary>
    Task<IReadOnlyList<PageRecord>> ListContainingAsync(string spaceId, IReadOnlyList<string> needles, CancellationToken ct);
    /// <summary>Active tasks whose notes contain any of the given strings (case-insensitive), as (Id, Title, Notes).</summary>
    Task<IReadOnlyList<(string Id, string Title, string Notes)>> ListTasksContainingAsync(IReadOnlyList<string> needles, CancellationToken ct);
    /// <summary>Active tag IDs linked to the page from its front matter.</summary>
    Task<IReadOnlyList<string>> ListTagIdsAsync(string pageId, CancellationToken ct);
    /// <summary>Active pages linked to any of these tags.</summary>
    Task<IReadOnlyList<PageRecord>> ListByTagsAsync(IReadOnlyCollection<string> tagIds, CancellationToken ct);
}

/// <summary>Reversible operations per profile (PLAN §9: latest 100 kept).</summary>
public interface IUndoStore
{
    Task AddAsync(UndoEntryRecord entry, CancellationToken ct);
    Task<UndoEntryRecord?> FindByOperationAsync(string profileId, string operationId, CancellationToken ct);
    /// <summary>Entries not undone yet, newest first.</summary>
    Task<IReadOnlyList<UndoEntryRecord>> ListOpenAsync(string profileId, int limit, CancellationToken ct);
    /// <returns>False when the entry was already marked undone.</returns>
    Task<bool> MarkUndoneAsync(string entryId, DateTimeOffset undoneAtUtc, CancellationToken ct);
    /// <summary>Deletes all but the newest <paramref name="keep"/> entries of the profile.</summary>
    Task PruneAsync(string profileId, int keep, CancellationToken ct);
    /// <summary>Rewrites the change set of an open entry (an undo re-points older entries to the versions it wrote).</summary>
    Task ReplacePayloadAsync(string entryId, string inversePayload, string expectedVersions, CancellationToken ct);
}

public interface IChecklistConversionStore
{
    Task<ChecklistConversionRecord?> FindAsync(string taskId, string itemKey, CancellationToken ct);
    /// <summary>Inserts the marker, replacing an older one for the same item (its subtask was deleted).</summary>
    Task UpsertAsync(ChecklistConversionRecord marker, CancellationToken ct);
}

public interface IProfileStore
{
    Task<ProfileRecord?> GetAsync(string profileId, CancellationToken ct);
    Task<ProfileRecord?> GetPrimaryAsync(CancellationToken ct);
    Task AddAsync(ProfileRecord profile, CancellationToken ct);
    /// <summary>Optimistic update of the first weekday (PLAN §10 week-start changes).</summary>
    Task<ProfileRecord?> UpdateFirstWeekdayAsync(ProfileRecord profile, long expectedVersion, CancellationToken ct);
    /// <summary>Optimistic update of the default reminder time and quiet hours (PLAN §11).</summary>
    Task<ProfileRecord?> UpdateReminderSettingsAsync(ProfileRecord profile, long expectedVersion, CancellationToken ct);
    /// <summary>Optimistic update of the morning, evening and weekly summary schedule (PLAN §12).</summary>
    Task<ProfileRecord?> UpdateSummarySettingsAsync(ProfileRecord profile, long expectedVersion, CancellationToken ct);
    /// <summary>Optimistic update of the display name, time zone, date format and desktop shell settings (PLAN §13).</summary>
    Task<ProfileRecord?> UpdateGeneralSettingsAsync(ProfileRecord profile, long expectedVersion, CancellationToken ct);
}

public interface ISpaceStore
{
    Task<SpaceRecord?> GetAsync(string spaceId, CancellationToken ct);
    Task<SpaceRecord?> GetPersonalByOwnerAsync(string ownerProfileId, CancellationToken ct);
    Task AddAsync(SpaceRecord space, CancellationToken ct);
}

public interface IContainerStore
{
    Task<ContainerRecord?> GetAsync(string containerId, CancellationToken ct);
    Task<ContainerRecord?> GetIncludingDeletedAsync(string containerId, CancellationToken ct);
    Task<IReadOnlyList<ContainerRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct);
    Task<ContainerRecord?> GetInboxAsync(string spaceId, CancellationToken ct);
    Task<IReadOnlyList<ContainerRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct);
    Task AddAsync(ContainerRecord container, CancellationToken ct);
    Task<ContainerRecord?> UpdateAsync(ContainerRecord updated, long expectedVersion, CancellationToken ct);
    Task<long> GetNextPositionAsync(string spaceId, CancellationToken ct);
    /// <summary>Active (nontombstoned) top-level task count inside one container.</summary>
    Task<long> CountActiveTasksAsync(string containerId, CancellationToken ct);
}

public interface ISectionStore
{
    Task<SectionRecord?> GetAsync(string sectionId, CancellationToken ct);
    Task<SectionRecord?> GetIncludingDeletedAsync(string sectionId, CancellationToken ct);
    Task<IReadOnlyList<SectionRecord>> ListDeletedByContainerAsync(string containerId, CancellationToken ct);
    /// <summary>Active sections of one container, ordered by (Position, Id).</summary>
    Task<IReadOnlyList<SectionRecord>> ListByContainerAsync(string containerId, CancellationToken ct);
    Task AddAsync(SectionRecord section, CancellationToken ct);
    Task<SectionRecord?> UpdateAsync(SectionRecord updated, long expectedVersion, CancellationToken ct);
    Task<long> GetNextPositionAsync(string containerId, CancellationToken ct);
}

public interface ITaskStore
{
    Task<TaskRecord?> GetAsync(string taskId, CancellationToken ct);
    /// <summary>The row even when tombstoned; restore and history need the deleted record.</summary>
    Task<TaskRecord?> GetIncludingDeletedAsync(string taskId, CancellationToken ct);
    /// <summary>Active top-level tasks of one container, ordered by (Position, Id).</summary>
    Task<IReadOnlyList<TaskRecord>> ListByContainerAsync(string containerId, CancellationToken ct);
    Task<IReadOnlyList<TaskRecord>> ListDeletedByContainerAsync(string containerId, CancellationToken ct);
    Task AddAsync(TaskRecord task, CancellationToken ct);
    /// <summary>
    /// Optimistic-concurrency update: rewrites the row only when its stored version equals
    /// <paramref name="expectedVersion"/> (PLAN §5). Returns the updated row, or null when
    /// the row is gone or was changed concurrently (version mismatch).
    /// </summary>
    Task<TaskRecord?> UpdateAsync(TaskRecord updated, long expectedVersion, CancellationToken ct);
    /// <summary>Highest Position currently used inside a container among active tasks.</summary>
    Task<long> GetNextPositionAsync(string containerId, CancellationToken ct);
}

public interface ISubtaskStore
{
    Task<SubtaskRecord?> GetAsync(string subtaskId, CancellationToken ct);
    /// <summary>The row even when tombstoned (tag restore re-links tombstoned subtasks too).</summary>
    Task<SubtaskRecord?> GetIncludingDeletedAsync(string subtaskId, CancellationToken ct);
    /// <summary>Active subtasks of one task, ordered by (Position, Id).</summary>
    Task<IReadOnlyList<SubtaskRecord>> ListByTaskAsync(string taskId, CancellationToken ct);
    /// <summary>All subtasks of one task including tombstoned ones (history snapshots, restore).</summary>
    Task<IReadOnlyList<SubtaskRecord>> ListByTaskIncludingDeletedAsync(string taskId, CancellationToken ct);
    Task AddAsync(SubtaskRecord subtask, CancellationToken ct);
    Task<SubtaskRecord?> UpdateAsync(SubtaskRecord updated, long expectedVersion, CancellationToken ct);
    Task<long> GetNextPositionAsync(string taskId, CancellationToken ct);
}

public interface ITemplateStore
{
    Task<TemplateRecord?> GetAsync(string templateId, CancellationToken ct);
    Task<TemplateRecord?> GetIncludingDeletedAsync(string templateId, CancellationToken ct);
    /// <summary>Active template with this normalized name in the space, if any.</summary>
    Task<TemplateRecord?> FindByNameAsync(string spaceId, string normalizedName, CancellationToken ct);
    /// <summary>Active templates of one space, ordered by (NormalizedName, Id).</summary>
    Task<IReadOnlyList<TemplateRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct);
    Task<IReadOnlyList<TemplateRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct);
    Task AddAsync(TemplateRecord template, CancellationToken ct);
    Task<TemplateRecord?> UpdateAsync(TemplateRecord updated, long expectedVersion, CancellationToken ct);
}

/// <summary>
/// Tags and their task/subtask links (PLAN §5/§6). Tags are profile-owned and nested by
/// adjacency; link writes are set operations that report whether anything changed.
/// </summary>
public interface ITagStore
{
    Task<TagRecord?> GetAsync(string tagId, CancellationToken ct);
    Task<TagRecord?> GetIncludingDeletedAsync(string tagId, CancellationToken ct);
    /// <summary>Active tags of one owner, ordered by (Position, NormalizedName, Id).</summary>
    Task<IReadOnlyList<TagRecord>> ListByOwnerAsync(string ownerProfileId, CancellationToken ct);
    Task<IReadOnlyList<TagRecord>> ListDeletedByOwnerAsync(string ownerProfileId, CancellationToken ct);
    /// <summary>The active child of <paramref name="parentTagId"/> (null = root) with this identity key.</summary>
    Task<TagRecord?> FindChildAsync(string ownerProfileId, string? parentTagId, string normalizedName, CancellationToken ct);
    /// <summary>Active direct children of a tag, ordered by (Position, NormalizedName, Id).</summary>
    Task<IReadOnlyList<TagRecord>> ListChildrenAsync(string tagId, CancellationToken ct);
    Task AddAsync(TagRecord tag, CancellationToken ct);
    Task<TagRecord?> UpdateAsync(TagRecord updated, long expectedVersion, CancellationToken ct);
    Task<long> GetNextPositionAsync(string ownerProfileId, string? parentTagId, CancellationToken ct);

    Task<IReadOnlyList<string>> ListTaskTagIdsAsync(string taskId, CancellationToken ct);
    Task<IReadOnlyList<string>> ListSubtaskTagIdsAsync(string subtaskId, CancellationToken ct);
    /// <summary>Task IDs linked to this exact tag (no descendants), including tombstoned tasks.</summary>
    Task<IReadOnlyList<string>> ListTaskIdsAsync(string tagId, CancellationToken ct);
    Task<IReadOnlyList<string>> ListSubtaskIdsAsync(string tagId, CancellationToken ct);
    /// <returns>False when the link already existed.</returns>
    Task<bool> AddTaskLinkAsync(string taskId, string tagId, CancellationToken ct);
    /// <returns>False when there was no such link.</returns>
    Task<bool> RemoveTaskLinkAsync(string taskId, string tagId, DateTimeOffset removedAtUtc, CancellationToken ct);
    Task<bool> AddSubtaskLinkAsync(string subtaskId, string tagId, CancellationToken ct);
    Task<bool> RemoveSubtaskLinkAsync(string subtaskId, string tagId, DateTimeOffset removedAtUtc, CancellationToken ct);

    Task<IReadOnlyList<string>> ListPadTagIdsAsync(string padItemId, CancellationToken ct);
    /// <summary>Pad item IDs linked to this exact tag, including tombstoned items.</summary>
    Task<IReadOnlyList<string>> ListPadItemIdsAsync(string tagId, CancellationToken ct);
    Task<bool> AddPadLinkAsync(string padItemId, string tagId, CancellationToken ct);
    Task<bool> RemovePadLinkAsync(string padItemId, string tagId, DateTimeOffset removedAtUtc, CancellationToken ct);
}

/// <summary>
/// The shared built-in query/search service port (PLAN §10): one criteria object yields both
/// rows and counts, ordered deterministically with keyset cursors.
/// </summary>
public interface ITaskQueryStore
{
    Task<TaskQueryPage> QueryAsync(TaskQueryCriteria criteria, CancellationToken ct);
    Task<long> CountAsync(TaskQueryCriteria criteria, CancellationToken ct);
    /// <summary>Distinct due zones among open timed tasks of a space (to resolve per-zone overdue cutoffs).</summary>
    Task<IReadOnlyList<string>> ListOpenDueZoneIdsAsync(string spaceId, CancellationToken ct);
    /// <summary>Drops and rebuilds every search document from the source tables (PLAN §5: tested rebuild command).</summary>
    Task<long> RebuildSearchIndexAsync(DateTimeOffset now, CancellationToken ct);
}

public interface ITaskHistoryStore
{
    Task AddAsync(TaskHistoryRecord entry, CancellationToken ct);
    /// <summary>All history of one task, oldest first (PLAN §5: append-only).</summary>
    Task<IReadOnlyList<TaskHistoryRecord>> ListByTaskAsync(string taskId, CancellationToken ct);
}

public interface IOperationReceiptStore
{
    Task<OperationReceiptRecord?> FindAsync(string profileId, string operationId, CancellationToken ct);
    Task<IReadOnlyList<OperationReceiptRecord>> ListByKindAsync(string profileId, string commandKind, CancellationToken ct);
    Task AddAsync(OperationReceiptRecord receipt, CancellationToken ct);
}

public interface IBackupCatalogStore
{
    Task AddAsync(BackupCatalogRecord entry, CancellationToken ct);
    Task<BackupCatalogRecord?> FindByFileNameAsync(string fileName, CancellationToken ct);
}

/// <summary>
/// Board settings rows and the card reads a board needs (PLAN2 P2). Settings are a synced
/// aggregate; the card reads are ordinary read-snapshot queries over tasks, subtasks, tag
/// links and GitHub issue links.
/// </summary>
public interface IBoardStore
{
    /// <summary>The target's active settings row, or null when the board still uses the defaults.</summary>
    Task<BoardSettingsRecord?> GetAsync(BoardTargetKind targetKind, string targetId, CancellationToken ct);
    Task AddAsync(BoardSettingsRecord settings, CancellationToken ct);
    Task<BoardSettingsRecord?> UpdateAsync(BoardSettingsRecord updated, long expectedVersion, CancellationToken ct);
    /// <summary>Active top-level tasks of one container with subtask progress and an issue-link mark, ordered by (Position, Id).</summary>
    Task<IReadOnlyList<BoardCardRow>> ListCardsAsync(string containerId, CancellationToken ct);
    /// <summary>Active task-tag links of one container's tasks, ordered by (TaskId, TagId).</summary>
    Task<IReadOnlyList<BoardCardTag>> ListCardTagsAsync(string containerId, CancellationToken ct);
    /// <summary>Subtask progress and the issue-link mark for these tasks, keyed by task id (a view's board reads cards across lists).</summary>
    Task<IReadOnlyDictionary<string, BoardCardExtras>> ListCardExtrasAsync(IReadOnlyList<string> taskIds, CancellationToken ct);
}

/// <summary>
/// Saved custom views and their device-local alert baseline (PLAN2 P3). Views are a synced
/// aggregate; the match rows record what this device's alert evaluator has already seen.
/// </summary>
public interface ISavedViewStore
{
    /// <summary>One view by id, deleted ones included (restore needs them); null when it never existed.</summary>
    Task<SavedViewRecord?> GetAsync(string id, CancellationToken ct);
    /// <summary>The space's views ordered by (Position, Id); deleted ones only when asked.</summary>
    Task<IReadOnlyList<SavedViewRecord>> ListAsync(string spaceId, bool includeDeleted, CancellationToken ct);
    Task AddAsync(SavedViewRecord view, CancellationToken ct);
    /// <returns>The stored row, or null when the expected version no longer matches.</returns>
    Task<SavedViewRecord?> UpdateAsync(SavedViewRecord updated, long expectedVersion, CancellationToken ct);
    Task<IReadOnlyList<SavedViewMatchRecord>> ListMatchesAsync(string viewId, CancellationToken ct);
    /// <summary>Replaces one view's baseline with exactly these rows.</summary>
    Task ReplaceMatchesAsync(string viewId, IReadOnlyList<SavedViewMatchRecord> matches, CancellationToken ct);
    /// <summary>Writes baseline rows that are new or changed (a task seen for the first time, or one that left or returned) and removes the given tasks.</summary>
    Task ApplyMatchChangesAsync(string viewId, IReadOnlyList<SavedViewMatchRecord> upserts, IReadOnlyList<string> removedTaskIds, CancellationToken ct);
    Task AddAlertAsync(ViewAlertRecord alert, CancellationToken ct);
    Task<ViewAlertRecord?> GetAlertAsync(string id, CancellationToken ct);
    /// <summary>The view's pending alert, if one is waiting for its delivery time.</summary>
    Task<ViewAlertRecord?> FindPendingAlertAsync(string viewId, CancellationToken ct);
    /// <summary>Writes the alert when it is still in <paramref name="expected"/> state; false when it changed meanwhile.</summary>
    Task<bool> UpdateAlertAsync(ViewAlertRecord alert, ViewAlertState expected, CancellationToken ct);
    /// <summary>Pending alerts whose delivery time has come, oldest first.</summary>
    Task<IReadOnlyList<ViewAlertRecord>> ListDueAlertsAsync(DateTimeOffset now, int limit, CancellationToken ct);
    /// <summary>Delivered alerts waiting to be dismissed, newest first.</summary>
    Task<IReadOnlyList<ViewAlertRecord>> ListDeliveredAlertsAsync(int limit, CancellationToken ct);
}

/// <summary>
/// The application store owns transactions and read snapshots (PLAN §4 IApplicationStore /
/// IUnitOfWork). Writes run on a single serialized storage worker; changes are published
/// only after commit by the implementation.
/// </summary>
public interface IApplicationStore
{
    /// <summary>Runs one bounded mutation atomically. Throw inside to roll back everything.</summary>
    Task<TResult> ExecuteAsync<TResult>(Func<IDataSession, CancellationToken, Task<TResult>> operation, CancellationToken ct = default);

    /// <summary>Runs one bounded read. Never mutates.</summary>
    Task<TResult> QueryAsync<TResult>(Func<IDataSession, CancellationToken, Task<TResult>> query, CancellationToken ct = default);
}

/// <summary>Import bookkeeping (PLAN §14): committed batches and which task each imported source item became.</summary>
public interface IImportStore
{
    /// <summary>The task an item with this fingerprint became, when it still exists (deleted or not).</summary>
    Task<string?> FindTaskForFingerprintAsync(string itemFingerprint, CancellationToken ct);
    Task AddBatchAsync(Transfer.ImportBatchRecord batch, CancellationToken ct);
    /// <summary>Records (or re-points, after the earlier task was deleted) the task an item became.</summary>
    Task MapItemAsync(string itemFingerprint, string batchId, string taskId, string sourceKey, CancellationToken ct);
    Task<IReadOnlyList<Transfer.ImportBatchRecord>> ListBatchesAsync(int limit, CancellationToken ct);
}

/// <summary>
/// Calendar sources, the device-local meeting cache and event links (PLAN2 P5). Sources and links are
/// synced aggregates; events and fetch state stay on this device.
/// </summary>
public interface ICalendarStore
{
    Task<CalendarSourceRecord?> GetSourceAsync(string id, CancellationToken ct);
    /// <summary>Active (not removed) sources ordered by (Position, Id).</summary>
    Task<IReadOnlyList<CalendarSourceRecord>> ListSourcesAsync(CancellationToken ct);
    Task AddSourceAsync(CalendarSourceRecord source, CancellationToken ct);
    /// <returns>The stored row, or null when the expected version no longer matches.</returns>
    Task<CalendarSourceRecord?> UpdateSourceAsync(CalendarSourceRecord updated, long expectedVersion, CancellationToken ct);

    Task<CalendarSourceState?> GetStateAsync(string sourceId, CancellationToken ct);
    Task SaveStateAsync(CalendarSourceState state, CancellationToken ct);

    /// <summary>Replaces every cached event of the source with these rows.</summary>
    Task ReplaceEventsAsync(string sourceId, IReadOnlyList<CalendarEventRecord> events, CancellationToken ct);
    Task DeleteEventsAsync(string sourceId, CancellationToken ct);
    /// <summary>Events of enabled, active sources that may touch the half-open UTC window, ordered by start. Date-only events are matched by date.</summary>
    Task<IReadOnlyList<CalendarEventRecord>> ListEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, DateOnly fromDate, DateOnly toDate, CancellationToken ct);
    Task<CalendarEventRecord?> GetEventAsync(string sourceId, string externalId, CancellationToken ct);

    Task<GoogleAccountRecord?> GetAccountAsync(string id, CancellationToken ct);
    /// <summary>Active Google accounts, oldest first.</summary>
    Task<IReadOnlyList<GoogleAccountRecord>> ListAccountsAsync(CancellationToken ct);
    Task AddAccountAsync(GoogleAccountRecord account, CancellationToken ct);
    /// <returns>The stored row, or null when the expected version no longer matches.</returns>
    Task<GoogleAccountRecord?> UpdateAccountAsync(GoogleAccountRecord updated, long expectedVersion, CancellationToken ct);
    Task<GoogleAccountState?> GetAccountStateAsync(string accountId, CancellationToken ct);
    Task SaveAccountStateAsync(GoogleAccountState state, CancellationToken ct);

    Task<CalendarEventLinkRecord?> FindLinkAsync(string taskId, CancellationToken ct);
    Task<IReadOnlyList<CalendarEventLinkRecord>> ListLinksAsync(IReadOnlyList<string> taskIds, CancellationToken ct);
    Task AddLinkAsync(CalendarEventLinkRecord link, CancellationToken ct);
}

/// <summary>Attachment metadata and this device's byte state (PLAN2 §9.1; migration 031). The bytes themselves are in <c>IAttachmentFileStore</c>.</summary>
public interface IAttachmentStore
{
    /// <summary>An active attachment.</summary>
    Task<AttachmentRecord?> GetAsync(string id, CancellationToken ct);
    Task<AttachmentRecord?> GetIncludingDeletedAsync(string id, CancellationToken ct);
    /// <summary>Attachments of one owner, oldest first; deleted ones only when asked.</summary>
    Task<IReadOnlyList<AttachmentRecord>> ListByOwnerAsync(AttachmentOwnerKind kind, string ownerId, bool includeDeleted, CancellationToken ct);
    /// <summary>Every active attachment of a space, for lookups by name (page backgrounds).</summary>
    Task<IReadOnlyList<AttachmentRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct);
    Task AddAsync(AttachmentRecord attachment, CancellationToken ct);
    Task<AttachmentRecord?> UpdateAsync(AttachmentRecord updated, long expectedVersion, CancellationToken ct);
    /// <summary>True when any row, deleted or not, still names these bytes.</summary>
    Task<bool> IsHashReferencedAsync(string sha256, CancellationToken ct);
    Task<AttachmentLocalStateRecord?> GetLocalStateAsync(string sha256, CancellationToken ct);
    Task SetLocalStateAsync(AttachmentLocalStateRecord state, CancellationToken ct);
}
