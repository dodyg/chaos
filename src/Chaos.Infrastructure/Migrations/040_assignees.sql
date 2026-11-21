-- Q7 assignment state and assignee board settings.
ALTER TABLE Tasks ADD COLUMN AssignMode INTEGER NOT NULL DEFAULT 0 CHECK (AssignMode IN (0, 1));

CREATE TABLE TaskAssignees (
    TaskId TEXT NOT NULL REFERENCES Tasks(Id),
    MemberId TEXT NOT NULL,
    AddedAtUtc TEXT NOT NULL,
    CompletedAtUtc TEXT NULL,
    DeletedAtUtc TEXT NULL,
    PRIMARY KEY (TaskId, MemberId)
);
CREATE INDEX IX_TaskAssignees_Member ON TaskAssignees (MemberId, TaskId) WHERE DeletedAtUtc IS NULL;

CREATE TABLE BoardSettings_new (
    Id TEXT NOT NULL PRIMARY KEY,
    TargetKind INTEGER NOT NULL CHECK (TargetKind IN (1, 2)),
    TargetId TEXT NOT NULL,
    GroupBy INTEGER NOT NULL DEFAULT 0 CHECK (GroupBy BETWEEN 0 AND 4),
    SwimlaneBy INTEGER NOT NULL DEFAULT 0 CHECK (SwimlaneBy BETWEEN 0 AND 5),
    ColumnTagId TEXT NULL,
    SwimlaneTagId TEXT NULL,
    WipLimitsJson TEXT NOT NULL DEFAULT '{}',
    HiddenColumnsJson TEXT NOT NULL DEFAULT '[]',
    Version INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL,
    CHECK (GroupBy <> 3 OR ColumnTagId IS NOT NULL),
    CHECK (SwimlaneBy <> 2 OR SwimlaneTagId IS NOT NULL)
);
INSERT INTO BoardSettings_new SELECT * FROM BoardSettings;
DROP TABLE BoardSettings;
ALTER TABLE BoardSettings_new RENAME TO BoardSettings;
CREATE UNIQUE INDEX UX_BoardSettings_Target ON BoardSettings (TargetKind, TargetId) WHERE DeletedAtUtc IS NULL;
