-- Rebuild referenced parents with the runner's foreign-key check before commit.
CREATE TABLE Spaces_new (
 Id TEXT NOT NULL PRIMARY KEY, OwnerProfileId TEXT NOT NULL,
 Kind INTEGER NOT NULL CHECK (Kind IN (1,2)), Name TEXT NOT NULL,
 Version INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1), CreatedAtUtc TEXT NOT NULL,
 UpdatedAtUtc TEXT NOT NULL, DeletedAtUtc TEXT NULL, ColorKey TEXT NULL, Icon TEXT NULL,
 TagsOwnerOnly INTEGER NOT NULL DEFAULT 0 CHECK (TagsOwnerOnly IN (0,1)),
 AgentsAllowed INTEGER NOT NULL DEFAULT 0 CHECK (AgentsAllowed IN (0,1)),
 ClaimMinutes INTEGER NOT NULL DEFAULT 120 CHECK (ClaimMinutes BETWEEN 15 AND 1440),
 DefaultDeadlineReminderJson TEXT NULL,
 AllowAgentsToClose INTEGER NOT NULL DEFAULT 0 CHECK (AllowAgentsToClose IN (0,1))
);
INSERT INTO Spaces_new (Id, OwnerProfileId, Kind, Name, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc) SELECT Id, OwnerProfileId, Kind, Name, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc FROM Spaces;
DROP TABLE Spaces;
ALTER TABLE Spaces_new RENAME TO Spaces;
CREATE UNIQUE INDEX UX_Spaces_PersonalPerOwner ON Spaces (OwnerProfileId, Kind) WHERE Kind = 1 AND DeletedAtUtc IS NULL;
CREATE TABLE DiaryEntries_new (
 Id TEXT NOT NULL PRIMARY KEY, TaskId TEXT NOT NULL REFERENCES Tasks(Id),
 AuthorProfileId TEXT NOT NULL CHECK (length(AuthorProfileId) = 36 AND substr(AuthorProfileId,9,1) = '-' AND substr(AuthorProfileId,14,1) = '-' AND substr(AuthorProfileId,19,1) = '-' AND substr(AuthorProfileId,24,1) = '-' AND length(replace(AuthorProfileId, '-', '')) = 32 AND replace(AuthorProfileId, '-', '') NOT GLOB '*[^0-9a-fA-F]*'),
 EntryAtUtc TEXT NOT NULL, RawMarkdown TEXT NOT NULL,
 Revision INTEGER NOT NULL DEFAULT 1 CHECK (Revision >= 1), EditedAtUtc TEXT NULL,
 Version INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1), CreatedAtUtc TEXT NOT NULL,
 UpdatedAtUtc TEXT NOT NULL, DeletedAtUtc TEXT NULL
);
INSERT INTO DiaryEntries_new SELECT * FROM DiaryEntries;
DROP TABLE DiaryEntries;
ALTER TABLE DiaryEntries_new RENAME TO DiaryEntries;
CREATE INDEX IX_DiaryEntries_TaskTime ON DiaryEntries (TaskId, EntryAtUtc);
CREATE INDEX IX_DiaryEntries_TaskActive ON DiaryEntries (TaskId, EntryAtUtc) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_DiaryEntries_EntryAt ON DiaryEntries (EntryAtUtc) WHERE DeletedAtUtc IS NULL;
CREATE TRIGGER GitHubDirtyDiary AFTER UPDATE OF RawMarkdown, DeletedAtUtc ON DiaryEntries BEGIN UPDATE GitHubCommentLinks SET State = CASE WHEN NEW.DeletedAtUtc IS NOT NULL THEN 3 WHEN State = 1 THEN 1 ELSE 2 END WHERE DiaryEntryId = NEW.Id AND State IN (0,1,2) AND EXISTS (SELECT 1 FROM GitHubIssueLinks l WHERE l.Id = IssueLinkId AND l.State = 0); END;
CREATE TABLE SpaceMembers (
 Id TEXT NOT NULL PRIMARY KEY, SpaceId TEXT NOT NULL REFERENCES Spaces(Id),
 MemberId TEXT NOT NULL, MemberKind INTEGER NOT NULL CHECK (MemberKind IN (1,2)),
 DisplayName TEXT NOT NULL, GitHubLogin TEXT NULL, Role INTEGER NOT NULL,
 JoinedAtUtc TEXT NOT NULL, RemovedAtUtc TEXT NULL, OwnerMemberId TEXT NULL
);
CREATE TABLE SyncStreams (
 StreamId TEXT NOT NULL PRIMARY KEY, Kind TEXT NOT NULL CHECK (Kind IN ('account','space')),
 SpaceId TEXT NULL, Role INTEGER NULL, Cursor INTEGER NOT NULL DEFAULT 0 CHECK (Cursor >= 0),
 State TEXT NOT NULL CHECK (State IN ('joining','active','evicting')), LastPullUtc TEXT NULL
);
-- Seed the account cursor; the multi-stream engine will use this in place of SyncState.Cursor.
INSERT INTO SyncStreams (StreamId, Kind, Cursor, State, LastPullUtc) SELECT AccountId, 'account', Cursor, 'active', LastPullUtc FROM SyncState WHERE AccountId IS NOT NULL;

-- Personal rows may outlive shared targets after eviction; preserve their tombstones and history.
CREATE TABLE "TaskTags_new" (
    TaskId TEXT NOT NULL,
    TagId  TEXT NOT NULL REFERENCES Tags(Id), DeletedAtUtc TEXT NULL,
    PRIMARY KEY (TaskId, TagId)
);
INSERT INTO "TaskTags_new" SELECT * FROM "TaskTags";
DROP TABLE "TaskTags";
ALTER TABLE "TaskTags_new" RENAME TO "TaskTags";
CREATE INDEX IX_TaskTags_Tag ON TaskTags (TagId);
CREATE TRIGGER GitHubDirtyTagDelete AFTER DELETE ON TaskTags BEGIN UPDATE GitHubIssueLinks SET LocalDirty = 1, LocalDirtySinceUtc = COALESCE(LocalDirtySinceUtc, strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z') WHERE TaskId = OLD.TaskId AND State = 0; END;
CREATE TRIGGER GitHubDirtyTagInsert AFTER INSERT ON TaskTags BEGIN UPDATE GitHubIssueLinks SET LocalDirty = 1, LocalDirtySinceUtc = COALESCE(LocalDirtySinceUtc, strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z') WHERE TaskId = NEW.TaskId AND State = 0; END;
CREATE TABLE "SubtaskTags_new" (
    SubtaskId TEXT NOT NULL,
    TagId     TEXT NOT NULL REFERENCES Tags(Id), DeletedAtUtc TEXT NULL,
    PRIMARY KEY (SubtaskId, TagId)
);
INSERT INTO "SubtaskTags_new" SELECT * FROM "SubtaskTags";
DROP TABLE "SubtaskTags";
ALTER TABLE "SubtaskTags_new" RENAME TO "SubtaskTags";
CREATE INDEX IX_SubtaskTags_Subtask ON SubtaskTags (SubtaskId);
CREATE INDEX IX_SubtaskTags_Tag ON SubtaskTags (TagId);
CREATE TABLE "ScratchPadTags_new" (
    PadItemId TEXT NOT NULL,
    TagId     TEXT NOT NULL REFERENCES Tags(Id), DeletedAtUtc TEXT NULL,
    PRIMARY KEY (PadItemId, TagId)
);
INSERT INTO "ScratchPadTags_new" SELECT * FROM "ScratchPadTags";
DROP TABLE "ScratchPadTags";
ALTER TABLE "ScratchPadTags_new" RENAME TO "ScratchPadTags";
CREATE INDEX IX_ScratchPadTags_Tag ON ScratchPadTags (TagId);
CREATE TABLE "PageTags_new" (
    PageId TEXT NOT NULL,
    TagId  TEXT NOT NULL REFERENCES Tags(Id), DeletedAtUtc TEXT NULL,
    PRIMARY KEY (PageId, TagId)
);
INSERT INTO "PageTags_new" SELECT * FROM "PageTags";
DROP TABLE "PageTags";
ALTER TABLE "PageTags_new" RENAME TO "PageTags";
CREATE INDEX IX_PageTags_Tag ON PageTags (TagId);
CREATE TABLE "CalendarEventLinks_new" (
    Id           TEXT NOT NULL PRIMARY KEY,   -- calendareventlink:{TaskId}, deterministic
    TaskId       TEXT NOT NULL,
    Provider     INTEGER NOT NULL CHECK (Provider IN (1, 2)),
    CalendarId   TEXT NOT NULL,
    EventId      TEXT NOT NULL,
    EventStartUtc TEXT NOT NULL,
    EventTitle   TEXT NOT NULL,
    EventWebUrl  TEXT NULL,
    Version      INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL
);
INSERT INTO "CalendarEventLinks_new" SELECT * FROM "CalendarEventLinks";
DROP TABLE "CalendarEventLinks";
ALTER TABLE "CalendarEventLinks_new" RENAME TO "CalendarEventLinks";
CREATE UNIQUE INDEX UX_CalendarEventLinks_Task ON CalendarEventLinks (TaskId) WHERE DeletedAtUtc IS NULL;
CREATE TABLE "ReminderRules_new" (
    Id TEXT NOT NULL PRIMARY KEY,
    ProfileId TEXT NOT NULL REFERENCES Profiles(Id),
    TaskId TEXT NOT NULL,
    Anchor INTEGER NOT NULL CHECK (Anchor IN (0,1,2)),
    OffsetMinutes INTEGER NULL,
    AbsoluteAtUtc TEXT NULL,
    InsistentMinutes INTEGER NULL CHECK (InsistentMinutes IS NULL OR InsistentMinutes > 0),
    Enabled INTEGER NOT NULL CHECK (Enabled IN (0,1)),
    Version INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL,
    CHECK ((Anchor = 0 AND AbsoluteAtUtc IS NOT NULL AND OffsetMinutes IS NULL) OR
           (Anchor IN (1,2) AND AbsoluteAtUtc IS NULL AND OffsetMinutes IS NOT NULL))
);
INSERT INTO "ReminderRules_new" SELECT * FROM "ReminderRules";
DROP TABLE "ReminderRules";
ALTER TABLE "ReminderRules_new" RENAME TO "ReminderRules";
CREATE INDEX IX_ReminderRules_Task ON ReminderRules(TaskId) WHERE DeletedAtUtc IS NULL;
CREATE TABLE "WaitingDetails_new" (
    Id              TEXT NOT NULL PRIMARY KEY,
    TaskId          TEXT NOT NULL,
    ProfileId       TEXT NOT NULL REFERENCES Profiles(Id),
    Person          TEXT NOT NULL CHECK (length(trim(Person)) > 0 AND length(Person) <= 200),
    PersonNormalized TEXT NOT NULL,
    StartedAtUtc    TEXT NOT NULL,
    Version         INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc    TEXT NOT NULL,
    UpdatedAtUtc    TEXT NOT NULL,
    DeletedAtUtc    TEXT NULL
);
INSERT INTO "WaitingDetails_new" SELECT * FROM "WaitingDetails";
DROP TABLE "WaitingDetails";
ALTER TABLE "WaitingDetails_new" RENAME TO "WaitingDetails";
CREATE INDEX IX_WaitingDetails_Person ON WaitingDetails (PersonNormalized) WHERE DeletedAtUtc IS NULL;
CREATE UNIQUE INDEX UX_WaitingDetails_ActiveTask ON WaitingDetails (TaskId) WHERE DeletedAtUtc IS NULL;
CREATE TABLE "FollowUps_new" (
    Id              TEXT NOT NULL PRIMARY KEY,
    TaskId          TEXT NOT NULL,
    ProfileId       TEXT NOT NULL REFERENCES Profiles(Id),
    Kind            INTEGER NOT NULL CHECK (Kind IN (0,1,2,3)),
    FixedAtUtc      TEXT NULL,
    Amount          INTEGER NULL CHECK (Amount IS NULL OR (Amount >= 1 AND Amount <= 999)),
    Unit            INTEGER NULL CHECK (Unit IS NULL OR Unit IN (0,1,2,3)),
    SnoozedUntilUtc TEXT NULL,
    DueAtUtc        TEXT NULL,
    ResolvedAtUtc   TEXT NULL,
    Version         INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc    TEXT NOT NULL,
    UpdatedAtUtc    TEXT NOT NULL,
    DeletedAtUtc    TEXT NULL,
    CHECK ((Kind = 0 AND FixedAtUtc IS NOT NULL AND Amount IS NULL AND Unit IS NULL) OR
           (Kind IN (1,2) AND FixedAtUtc IS NULL AND Amount IS NOT NULL AND Unit IS NOT NULL) OR
           (Kind = 3 AND FixedAtUtc IS NULL AND Amount IS NOT NULL AND Unit = 2))
);
INSERT INTO "FollowUps_new" SELECT * FROM "FollowUps";
DROP TABLE "FollowUps";
ALTER TABLE "FollowUps_new" RENAME TO "FollowUps";
CREATE INDEX IX_FollowUps_Due ON FollowUps (DueAtUtc) WHERE DeletedAtUtc IS NULL AND ResolvedAtUtc IS NULL;
CREATE UNIQUE INDEX UX_FollowUps_ActiveTask ON FollowUps (TaskId) WHERE DeletedAtUtc IS NULL AND ResolvedAtUtc IS NULL;
CREATE TABLE "DayPlanItems_new" (
    Id            TEXT NOT NULL PRIMARY KEY,
    DayPlanId     TEXT NOT NULL REFERENCES DayPlans(Id),
    Kind          INTEGER NOT NULL CHECK (Kind = 0),
    TaskId        TEXT NOT NULL,
    Position      INTEGER NOT NULL,
    Origin        INTEGER NOT NULL CHECK (Origin IN (0,1,2)),
    AddedAtUtc    TEXT NOT NULL,
    RemovedAtUtc  TEXT NULL,
    Version       INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc  TEXT NOT NULL,
    UpdatedAtUtc  TEXT NOT NULL
);
INSERT INTO "DayPlanItems_new" SELECT * FROM "DayPlanItems";
DROP TABLE "DayPlanItems";
ALTER TABLE "DayPlanItems_new" RENAME TO "DayPlanItems";
CREATE INDEX IX_DayPlanItems_PlanPosition ON DayPlanItems (DayPlanId, Position) WHERE RemovedAtUtc IS NULL;
CREATE INDEX IX_DayPlanItems_Task ON DayPlanItems (TaskId);
CREATE UNIQUE INDEX UX_DayPlanItems_PlanTask ON DayPlanItems (DayPlanId, TaskId);
CREATE TABLE "WeekGoalTasks_new" (
    GoalId       TEXT NOT NULL REFERENCES WeekGoals(Id),
    TaskId       TEXT NOT NULL,
    DeletedAtUtc TEXT NULL,
    PRIMARY KEY (GoalId, TaskId)
);
INSERT INTO "WeekGoalTasks_new" SELECT * FROM "WeekGoalTasks";
DROP TABLE "WeekGoalTasks";
ALTER TABLE "WeekGoalTasks_new" RENAME TO "WeekGoalTasks";
CREATE INDEX IX_WeekGoalTasks_Task ON WeekGoalTasks (TaskId);
CREATE TABLE "WeekAssignments_new" (
    Id            TEXT NOT NULL PRIMARY KEY,
    WeekPlanId    TEXT NOT NULL REFERENCES WeekPlans(Id),
    TaskId        TEXT NOT NULL,
    LocalDate     TEXT NULL CHECK (LocalDate IS NULL OR length(LocalDate) = 10),
    Position      INTEGER NOT NULL,
    Origin        INTEGER NOT NULL CHECK (Origin IN (0,1)),
    CreatedAtUtc  TEXT NOT NULL,
    UpdatedAtUtc  TEXT NOT NULL
);
INSERT INTO "WeekAssignments_new" SELECT * FROM "WeekAssignments";
DROP TABLE "WeekAssignments";
ALTER TABLE "WeekAssignments_new" RENAME TO "WeekAssignments";
CREATE INDEX IX_WeekAssignments_PlanDay ON WeekAssignments (WeekPlanId, LocalDate, Position);
CREATE INDEX IX_WeekAssignments_Task ON WeekAssignments (TaskId);
CREATE UNIQUE INDEX UX_WeekAssignments_PlanTask ON WeekAssignments (WeekPlanId, TaskId);
CREATE TRIGGER TR_WeekAssignments_DayInWeek_Insert BEFORE INSERT ON WeekAssignments WHEN NEW.LocalDate IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WeekPlans p WHERE p.Id = NEW.WeekPlanId AND NEW.LocalDate BETWEEN p.WeekStartDate AND date(p.WeekStartDate, '+6 days')) BEGIN SELECT RAISE(ABORT, 'A week assignment must fall within its week.'); END;
CREATE TRIGGER TR_WeekAssignments_DayInWeek_Update BEFORE UPDATE OF LocalDate, WeekPlanId ON WeekAssignments WHEN NEW.LocalDate IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WeekPlans p WHERE p.Id = NEW.WeekPlanId AND NEW.LocalDate BETWEEN p.WeekStartDate AND date(p.WeekStartDate, '+6 days')) BEGIN SELECT RAISE(ABORT, 'A week assignment must fall within its week.'); END;
CREATE TABLE "ImportedItems_new" (
    ItemFingerprint TEXT NOT NULL PRIMARY KEY CHECK (length(ItemFingerprint) = 64),
    BatchId         TEXT NOT NULL REFERENCES ImportBatches(Id),
    TaskId          TEXT NOT NULL,
    SourceKey       TEXT NOT NULL
);
INSERT INTO "ImportedItems_new" SELECT * FROM "ImportedItems";
DROP TABLE "ImportedItems";
ALTER TABLE "ImportedItems_new" RENAME TO "ImportedItems";
CREATE INDEX IX_ImportedItems_Batch ON ImportedItems (BatchId);
CREATE INDEX IX_ImportedItems_Task ON ImportedItems (TaskId);
CREATE TABLE "WeekGoals_new" (
    Id            TEXT NOT NULL PRIMARY KEY,
    WeekPlanId    TEXT NOT NULL REFERENCES WeekPlans(Id),
    Title         TEXT NOT NULL CHECK (length(trim(Title)) > 0),
    Position      INTEGER NOT NULL,
    ProjectId     TEXT NULL,
    Outcome       INTEGER NOT NULL DEFAULT 0 CHECK (Outcome IN (0,1,2)),
    Version       INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc  TEXT NOT NULL,
    UpdatedAtUtc  TEXT NOT NULL
);
INSERT INTO "WeekGoals_new" SELECT * FROM "WeekGoals";
DROP TABLE "WeekGoals";
ALTER TABLE "WeekGoals_new" RENAME TO "WeekGoals";
CREATE INDEX IX_WeekGoals_Plan ON WeekGoals (WeekPlanId, Position);
