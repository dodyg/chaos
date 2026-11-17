-- Chaos migration 030 (PLAN2 P6a, activity log). All four tables are device-local (LocalOnly) and never
-- exported or synced: they are the system event stream of section 6.4, read in order by in-process consumers.

CREATE TABLE ActivityLog (
    Seq            INTEGER PRIMARY KEY AUTOINCREMENT,
    OperationId    TEXT NOT NULL,
    OccurredAtUtc  TEXT NOT NULL,
    Source         TEXT NOT NULL CHECK (Source IN ('user','schedule','sync','import','undo','system')),
    Action         TEXT NOT NULL,
    Outcome        TEXT NOT NULL CHECK (Outcome IN ('ok','failed')),
    JobRunId       TEXT NULL,
    DetailJson     TEXT NULL,
    DetailVersion  INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX IX_ActivityLog_OccurredAt ON ActivityLog (OccurredAtUtc);

CREATE TABLE ActivityLogEntities (
    Seq        INTEGER NOT NULL REFERENCES ActivityLog(Seq) ON DELETE CASCADE,
    Kind       TEXT NOT NULL,
    EntityKey  TEXT NOT NULL,
    Op         TEXT NOT NULL CHECK (Op IN ('upsert','delete')),
    Version    INTEGER NULL,
    PRIMARY KEY (Seq, Kind, EntityKey)
);

CREATE TABLE ActivityCursors (
    Consumer      TEXT NOT NULL PRIMARY KEY,
    Seq           INTEGER NOT NULL,
    UpdatedAtUtc  TEXT NOT NULL
);

CREATE TABLE JobRuns (
    Id             TEXT NOT NULL PRIMARY KEY,
    Job            TEXT NOT NULL,
    StartedAtUtc   TEXT NOT NULL,
    FinishedAtUtc  TEXT NULL,
    Outcome        TEXT NOT NULL CHECK (Outcome IN ('running','ok','noop','failed')),
    Error          TEXT NULL,
    ActivityCount  INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IX_JobRuns_Job ON JobRuns (Job, StartedAtUtc);
