-- Chaos migration 024 (PLAN2 §7.3): sync foundation. Bookkeeping tables for change capture
-- and apply, plus tombstones on the link tables, so that removing a link becomes a recorded
-- change instead of a row that silently disappears. Nothing here changes behaviour while
-- sync is disabled: SyncState starts with Enabled = 0, the trigger installer only installs
-- triggers at startup (7.3), and the flush hook clears SyncPending on every write
-- transaction while sync is off.

CREATE TABLE SyncPending (
    Kind      TEXT NOT NULL,
    EntityKey TEXT NOT NULL,
    GroupName TEXT NOT NULL,
    PRIMARY KEY (Kind, EntityKey, GroupName)
);

-- The apply flag lives in a real table because triggers may not reference temp tables.
CREATE TABLE SyncApplyState (
    Id       INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
    Applying INTEGER NOT NULL CHECK (Applying IN (0,1))
);
INSERT INTO SyncApplyState (Id, Applying) VALUES (1, 0);

-- One row. The trigger installer stores the catalog hash here (7.3); account binding and
-- the pull cursor arrive with P13. Seeded disabled: offline-only behaviour is unchanged.
CREATE TABLE SyncState (
    Id             INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
    Enabled        INTEGER NOT NULL CHECK (Enabled IN (0,1)),
    AccountId      TEXT NULL,
    ProfileBinding TEXT NULL,
    Cursor         INTEGER NOT NULL,
    LastHlc        TEXT NULL,
    TriggerHash    TEXT NULL,
    LastPushUtc    TEXT NULL,
    LastPullUtc    TEXT NULL,
    LastError      TEXT NULL
);
INSERT INTO SyncState (Id, Enabled, Cursor) VALUES (1, 0, 0);

-- Highest clock seen per entity group, local or received (7.3 flush, 7.4 apply).
CREATE TABLE SyncClocks (
    Kind      TEXT NOT NULL,
    EntityKey TEXT NOT NULL,
    GroupName TEXT NOT NULL,
    Hlc       TEXT NOT NULL,
    PRIMARY KEY (Kind, EntityKey, GroupName)
);

-- Changes waiting to be pushed; written by the flush inside the user's transaction.
CREATE TABLE SyncOutbox (
    Seq          INTEGER PRIMARY KEY AUTOINCREMENT,
    ChangeId     TEXT NOT NULL UNIQUE,
    Kind         TEXT NOT NULL,
    EntityKey    TEXT NOT NULL,
    Op           TEXT NOT NULL CHECK (Op IN ('upsert','purge')),
    PayloadJson  TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL
);

-- Remote changes that reference something that has not arrived yet (7.8); retried after
-- every pull, and raised as attention after ten pull cycles.
CREATE TABLE SyncInboxParked (
    ChangeId         TEXT NOT NULL PRIMARY KEY,
    Kind             TEXT NOT NULL,
    EntityKey        TEXT NOT NULL,
    Op               TEXT NOT NULL CHECK (Op IN ('upsert','purge')),
    PayloadJson      TEXT NOT NULL,
    Reason           TEXT NOT NULL,
    FirstParkedAtUtc TEXT NOT NULL,
    PullCycles       INTEGER NOT NULL DEFAULT 0 CHECK (PullCycles >= 0)
);

-- Conflicts that need the person (7.9); listed by the sync status page (P15).
CREATE TABLE SyncAttention (
    Id            TEXT NOT NULL PRIMARY KEY,
    Category      TEXT NOT NULL,
    Kind          TEXT NULL,
    EntityKey     TEXT NULL,
    Message       TEXT NOT NULL,
    DetailJson    TEXT NULL,
    CreatedAtUtc  TEXT NOT NULL,
    ResolvedAtUtc TEXT NULL
);

-- This data root's device identity and device-only settings (7.1). Never exported: a
-- restored or merged archive must never carry another device's identity.
CREATE TABLE DeviceSettings (
    Key   TEXT NOT NULL PRIMARY KEY,
    Value TEXT NOT NULL
);

-- Link tables gain tombstones (7.2 rule 6): removing a link becomes DeletedAtUtc, so it can
-- sync and be undone exactly like other deletions. Their composite primary keys already
-- allow only one row per link, so a re-added link un-tombstones the existing row instead of
-- inserting a duplicate; reads filter DeletedAtUtc IS NULL, and a purge removes the rows.
ALTER TABLE TaskTags ADD COLUMN DeletedAtUtc TEXT NULL;
ALTER TABLE SubtaskTags ADD COLUMN DeletedAtUtc TEXT NULL;
ALTER TABLE ScratchPadTags ADD COLUMN DeletedAtUtc TEXT NULL;
ALTER TABLE PageTags ADD COLUMN DeletedAtUtc TEXT NULL;

-- WeekGoalTasks also gains the tombstone, but its ON DELETE CASCADE has to go: goal
-- replacement removes a goal's links outright, and a cascade delete would bypass the
-- tombstone the removal should travel as. Links removed while their goal survives keep a
-- tombstone instead. Rebuilt in place; its rows and index are carried over.
CREATE TABLE WeekGoalTasks_new (
    GoalId       TEXT NOT NULL REFERENCES WeekGoals(Id),
    TaskId       TEXT NOT NULL REFERENCES Tasks(Id),
    DeletedAtUtc TEXT NULL,
    PRIMARY KEY (GoalId, TaskId)
);
INSERT INTO WeekGoalTasks_new (GoalId, TaskId) SELECT GoalId, TaskId FROM WeekGoalTasks;
DROP TABLE WeekGoalTasks;
ALTER TABLE WeekGoalTasks_new RENAME TO WeekGoalTasks;
CREATE INDEX IX_WeekGoalTasks_Task ON WeekGoalTasks (TaskId);
