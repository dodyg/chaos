-- Chaos migration 001: identity, containers, tasks/subtasks, tags, diary/history,
-- undo, operation receipts, scratch pad, backup catalog (PLAN §5 schema map).
-- Timestamps: fixed-width ISO 8601 UTC text ('o' format). Dates 'yyyy-MM-dd', times 'HH:mm:ss'.
-- IDs are canonical GUID strings.

CREATE TABLE Profiles (
    Id                  TEXT NOT NULL PRIMARY KEY,
    DisplayName         TEXT NOT NULL,
    ZoneId              TEXT NOT NULL,             -- validated canonical IANA/Windows ID
    Locale              TEXT NOT NULL,
    DateFormat          TEXT NOT NULL CHECK (DateFormat IN ('DMY','MDY','YMD')),
    FirstWeekday        INTEGER NOT NULL CHECK (FirstWeekday BETWEEN 0 AND 6), -- 0=Sunday
    PlanningTime        TEXT NULL CHECK (PlanningTime IS NULL OR length(PlanningTime) = 8),
    SummaryTime         TEXT NULL CHECK (SummaryTime IS NULL OR length(SummaryTime) = 8),
    DefaultReminderTime TEXT NOT NULL DEFAULT '09:00:00' CHECK (length(DefaultReminderTime) = 8),
    QuietHoursEnabled   INTEGER NOT NULL DEFAULT 0 CHECK (QuietHoursEnabled IN (0,1)),
    QuietStart          TEXT NULL CHECK (QuietStart IS NULL OR length(QuietStart) = 8),
    QuietEnd            TEXT NULL CHECK (QuietEnd IS NULL OR length(QuietEnd) = 8),
    Version             INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc        TEXT NOT NULL,
    UpdatedAtUtc        TEXT NOT NULL,
    DeletedAtUtc        TEXT NULL
);

CREATE TABLE Spaces (
    Id             TEXT NOT NULL PRIMARY KEY,
    OwnerProfileId TEXT NOT NULL REFERENCES Profiles(Id),
    Kind           INTEGER NOT NULL CHECK (Kind = 1), -- 1 = Personal (only kind in phase 1)
    Name           TEXT NOT NULL,
    Version        INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc   TEXT NOT NULL,
    UpdatedAtUtc   TEXT NOT NULL,
    DeletedAtUtc   TEXT NULL
);

-- Exactly one Personal space per owner (PLAN §1 decision 1).
CREATE UNIQUE INDEX UX_Spaces_PersonalPerOwner
    ON Spaces (OwnerProfileId, Kind) WHERE DeletedAtUtc IS NULL;

CREATE TABLE Containers (
    Id             TEXT NOT NULL PRIMARY KEY,
    SpaceId        TEXT NOT NULL REFERENCES Spaces(Id),
    Kind           INTEGER NOT NULL CHECK (Kind IN (0,1,2)), -- 0=Inbox, 1=List, 2=Project
    Name           TEXT NOT NULL CHECK (length(trim(Name)) > 0),
    NormalizedName TEXT NOT NULL,
    IsInbox        INTEGER NOT NULL DEFAULT 0 CHECK (IsInbox IN (0,1)),
    Position       INTEGER NOT NULL CHECK (Position >= 0),
    DeadlineDate   TEXT NULL CHECK (DeadlineDate IS NULL OR length(DeadlineDate) = 10),
    ArchivedAtUtc  TEXT NULL,
    Version        INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc   TEXT NOT NULL,
    UpdatedAtUtc   TEXT NOT NULL,
    DeletedAtUtc   TEXT NULL
);

-- Unique normalized name among active containers of one space (SQLite UNIQUE ignores NULLs,
-- so an ordinary UNIQUE on nullable columns would not do; partial index is deliberate).
CREATE UNIQUE INDEX UX_Containers_ActiveNamePerSpace
    ON Containers (SpaceId, NormalizedName) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_Containers_SpacePosition ON Containers (SpaceId, Position) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_Containers_SpaceArchived ON Containers (SpaceId) WHERE ArchivedAtUtc IS NOT NULL;

CREATE TABLE Sections (
    Id          TEXT NOT NULL PRIMARY KEY,
    ContainerId TEXT NOT NULL REFERENCES Containers(Id),
    Title       TEXT NOT NULL CHECK (length(trim(Title)) > 0),
    Position    INTEGER NOT NULL CHECK (Position >= 0),
    Version     INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL
);

-- Composite target for the task-section foreign key (PLAN §5: unique (Id, ContainerId)).
CREATE UNIQUE INDEX UX_Sections_IdContainer ON Sections (Id, ContainerId);
CREATE INDEX IX_Sections_ContainerPosition ON Sections (ContainerId, Position) WHERE DeletedAtUtc IS NULL;

CREATE TABLE Tasks (
    Id                      TEXT NOT NULL PRIMARY KEY,
    SpaceId                 TEXT NOT NULL REFERENCES Spaces(Id),
    ContainerId             TEXT NOT NULL REFERENCES Containers(Id),
    SectionId               TEXT NULL,
    Title                   TEXT NOT NULL CHECK (length(trim(Title)) > 0),
    Notes                   TEXT NOT NULL DEFAULT '',
    Status                  INTEGER NOT NULL CHECK (Status BETWEEN 0 AND 5),
    Priority                INTEGER NOT NULL DEFAULT 2 CHECK (Priority BETWEEN 0 AND 4),
    EstimateMinutes         INTEGER NULL CHECK (EstimateMinutes IS NULL OR (EstimateMinutes >= 0 AND EstimateMinutes <= 10080)),
    CapacityCategory        INTEGER NOT NULL DEFAULT 0 CHECK (CapacityCategory IN (0,1)),
    PlannedDate             TEXT NULL CHECK (PlannedDate IS NULL OR length(PlannedDate) = 10),
    PlannedTime             TEXT NULL CHECK (PlannedTime IS NULL OR length(PlannedTime) = 8),
    PlannedZoneId           TEXT NULL,
    DueDate                 TEXT NULL CHECK (DueDate IS NULL OR length(DueDate) = 10),
    DueTime                 TEXT NULL CHECK (DueTime IS NULL OR length(DueTime) = 8),
    DueZoneId               TEXT NULL,
    CompletedAtUtc          TEXT NULL,
    CancelledAtUtc          TEXT NULL,
    Position                INTEGER NOT NULL CHECK (Position >= 0),
    RecurrenceOccurrenceKey TEXT NULL,
    Version                 INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc            TEXT NOT NULL,
    UpdatedAtUtc            TEXT NOT NULL,
    DeletedAtUtc            TEXT NULL,
    -- A time requires a date and a zone (PLAN §5 connection rules).
    CHECK ((PlannedTime IS NULL) OR (PlannedDate IS NOT NULL AND PlannedZoneId IS NOT NULL)),
    CHECK ((DueTime IS NULL) OR (DueDate IS NOT NULL AND DueZoneId IS NOT NULL)),
    -- Terminal timestamps exactly when the status requires them (PLAN §5).
    CHECK (
        (Status = 4 AND CompletedAtUtc IS NOT NULL AND CancelledAtUtc IS NULL) OR
        (Status = 5 AND CancelledAtUtc IS NOT NULL AND CompletedAtUtc IS NULL) OR
        (Status IN (0,1,2,3) AND CompletedAtUtc IS NULL AND CancelledAtUtc IS NULL)
    ),
    FOREIGN KEY (SectionId, ContainerId) REFERENCES Sections (Id, ContainerId)
);

CREATE INDEX IX_Tasks_ContainerPosition ON Tasks (ContainerId, Position) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_Tasks_StatusDue ON Tasks (Status, DueDate) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_Tasks_PlannedDate ON Tasks (PlannedDate) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_Tasks_CompletedAt ON Tasks (CompletedAtUtc) WHERE CompletedAtUtc IS NOT NULL;
CREATE INDEX IX_Tasks_SpaceContainer ON Tasks (SpaceId, ContainerId);

CREATE TABLE Subtasks (
    Id             TEXT NOT NULL PRIMARY KEY,
    TaskId         TEXT NOT NULL REFERENCES Tasks(Id),
    Title          TEXT NOT NULL CHECK (length(trim(Title)) > 0),
    Notes          TEXT NOT NULL DEFAULT '',
    Status         INTEGER NOT NULL CHECK (Status BETWEEN 0 AND 5),
    CompletedAtUtc TEXT NULL,
    CancelledAtUtc TEXT NULL,
    Position       INTEGER NOT NULL CHECK (Position >= 0),
    Version        INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc   TEXT NOT NULL,
    UpdatedAtUtc   TEXT NOT NULL,
    DeletedAtUtc   TEXT NULL,
    -- No independent space/container/recurrence columns by design (PLAN §5).
    CHECK (
        (Status = 4 AND CompletedAtUtc IS NOT NULL AND CancelledAtUtc IS NULL) OR
        (Status = 5 AND CancelledAtUtc IS NOT NULL AND CompletedAtUtc IS NULL) OR
        (Status IN (0,1,2,3) AND CompletedAtUtc IS NULL AND CancelledAtUtc IS NULL)
    )
);

CREATE INDEX IX_Subtasks_TaskPosition ON Subtasks (TaskId, Position) WHERE DeletedAtUtc IS NULL;

CREATE TABLE Tags (
    Id             TEXT NOT NULL PRIMARY KEY,
    OwnerProfileId TEXT NOT NULL REFERENCES Profiles(Id),
    DisplayName    TEXT NOT NULL CHECK (length(trim(DisplayName)) > 0),
    NormalizedName TEXT NOT NULL CHECK (length(NormalizedName) > 0),
    ParentTagId    TEXT NULL REFERENCES Tags(Id),
    ColorKey       TEXT NULL CHECK (ColorKey IS NULL OR length(ColorKey) BETWEEN 1 AND 16),
    Position       INTEGER NOT NULL DEFAULT 0 CHECK (Position >= 0),
    Version        INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc   TEXT NOT NULL,
    UpdatedAtUtc   TEXT NOT NULL,
    DeletedAtUtc   TEXT NULL
);

-- Unique active sibling name including root handling (NULL parent folds to '').
CREATE UNIQUE INDEX UX_Tags_ActiveSiblingName
    ON Tags (COALESCE(ParentTagId, ''), NormalizedName) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_Tags_Parent ON Tags (ParentTagId) WHERE DeletedAtUtc IS NULL;

CREATE TABLE TaskTags (
    TaskId TEXT NOT NULL REFERENCES Tasks(Id),
    TagId  TEXT NOT NULL REFERENCES Tags(Id),
    PRIMARY KEY (TaskId, TagId)
);
CREATE INDEX IX_TaskTags_Tag ON TaskTags (TagId);

CREATE TABLE SubtaskTags (
    SubtaskId TEXT NOT NULL REFERENCES Subtasks(Id),
    TagId     TEXT NOT NULL REFERENCES Tags(Id),
    PRIMARY KEY (SubtaskId, TagId)
);
CREATE INDEX IX_SubtaskTags_Tag ON SubtaskTags (TagId);

CREATE TABLE DiaryEntries (
    Id              TEXT NOT NULL PRIMARY KEY,
    TaskId          TEXT NOT NULL REFERENCES Tasks(Id),
    AuthorProfileId TEXT NOT NULL REFERENCES Profiles(Id),
    EntryAtUtc      TEXT NOT NULL,
    RawMarkdown     TEXT NOT NULL,
    Revision        INTEGER NOT NULL DEFAULT 1 CHECK (Revision >= 1),
    EditedAtUtc     TEXT NULL,
    Version         INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc    TEXT NOT NULL,
    UpdatedAtUtc    TEXT NOT NULL,
    DeletedAtUtc    TEXT NULL
);
CREATE INDEX IX_DiaryEntries_TaskTime ON DiaryEntries (TaskId, EntryAtUtc);

CREATE TABLE TaskHistory (
    Id               TEXT NOT NULL PRIMARY KEY,
    TaskId           TEXT NOT NULL REFERENCES Tasks(Id),
    OperationId      TEXT NOT NULL,
    EffectiveAtUtc   TEXT NOT NULL,
    Actor            TEXT NOT NULL,
    EventKind        TEXT NOT NULL,
    ChangedFields    TEXT NOT NULL,  -- JSON array of field paths
    SnapshotBefore   TEXT NULL,      -- versioned JSON aggregate snapshot (or null for first creation)
    SnapshotAfter    TEXT NULL,      -- versioned JSON aggregate snapshot
    AggregateVersion INTEGER NOT NULL CHECK (AggregateVersion >= 1),
    RecordedAtUtc    TEXT NOT NULL
);
CREATE INDEX IX_TaskHistory_TaskTime ON TaskHistory (TaskId, EffectiveAtUtc);
CREATE INDEX IX_TaskHistory_Operation ON TaskHistory (OperationId);

CREATE TABLE UndoEntries (
    Id               TEXT NOT NULL PRIMARY KEY,
    ProfileId        TEXT NOT NULL REFERENCES Profiles(Id),
    OperationId      TEXT NOT NULL,
    Label            TEXT NOT NULL,
    InversePayload   TEXT NOT NULL,  -- versioned JSON inverse action
    ExpectedVersions TEXT NOT NULL,  -- JSON map entity-id -> expected version
    AppliedAtUtc     TEXT NOT NULL,
    UndoneAtUtc      TEXT NULL
);
CREATE UNIQUE INDEX UX_UndoEntries_PerOperation ON UndoEntries (OperationId);
CREATE INDEX IX_UndoEntries_ProfileApplied ON UndoEntries (ProfileId, AppliedAtUtc DESC);

CREATE TABLE OperationReceipts (
    ProfileId    TEXT NOT NULL REFERENCES Profiles(Id),
    OperationId  TEXT NOT NULL,
    CommandKind  TEXT NOT NULL,
    PayloadHash  TEXT NOT NULL,
    ResultJson   TEXT NOT NULL,
    CommittedAtUtc TEXT NOT NULL,
    PRIMARY KEY (ProfileId, OperationId)
);

CREATE TABLE ScratchPadItems (
    Id           TEXT NOT NULL PRIMARY KEY,
    SpaceId      TEXT NOT NULL REFERENCES Spaces(Id),
    RawMarkdown  TEXT NOT NULL,
    Position     INTEGER NOT NULL CHECK (Position >= 0),
    ArchivedAtUtc TEXT NULL,
    DraftVersion INTEGER NOT NULL DEFAULT 0 CHECK (DraftVersion >= 0),
    Version      INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL
);
CREATE INDEX IX_ScratchPadItems_SpacePosition ON ScratchPadItems (SpaceId, Position) WHERE DeletedAtUtc IS NULL;

CREATE TABLE ScratchTaskLinks (
    PadItemId      TEXT NOT NULL REFERENCES ScratchPadItems(Id),
    TaskId         TEXT NOT NULL REFERENCES Tasks(Id),
    SourceRevision INTEGER NOT NULL CHECK (SourceRevision >= 0),
    StartIndex     INTEGER NULL CHECK (StartIndex IS NULL OR StartIndex >= 0),
    EndIndex       INTEGER NULL CHECK (EndIndex IS NULL OR EndIndex >= 0),
    Quote          TEXT NOT NULL,
    CreatedAtUtc   TEXT NOT NULL,
    PRIMARY KEY (PadItemId, TaskId)
);

CREATE TABLE BackupCatalog (
    Id               TEXT NOT NULL PRIMARY KEY,
    FileName         TEXT NOT NULL UNIQUE,
    SchemaVersion    INTEGER NOT NULL CHECK (SchemaVersion >= 1),
    AppVersion       TEXT NOT NULL,
    CreatedAtUtc     TEXT NOT NULL,
    ChecksumSha256   TEXT NOT NULL,
    ValidationStatus TEXT NOT NULL CHECK (ValidationStatus IN ('valid','unvalidated','invalid')),
    Reason           TEXT NOT NULL CHECK (Reason IN ('pre-migration','pre-import','pre-restore','manual','scheduled'))
);
CREATE INDEX IX_BackupCatalog_Created ON BackupCatalog (CreatedAtUtc DESC);
