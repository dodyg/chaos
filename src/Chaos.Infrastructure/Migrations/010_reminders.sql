-- Reminder engine and outside-person follow-up (PLAN §11, work package 10).

-- Waiting on someone outside the app: free text only, never a contact or account (nothing
-- is ever sent). Clearing waiting tombstones the row; one active row per task.
CREATE TABLE WaitingDetails (
    Id              TEXT NOT NULL PRIMARY KEY,
    TaskId          TEXT NOT NULL REFERENCES Tasks(Id),
    ProfileId       TEXT NOT NULL REFERENCES Profiles(Id),
    Person          TEXT NOT NULL CHECK (length(trim(Person)) > 0 AND length(Person) <= 200),
    PersonNormalized TEXT NOT NULL,
    StartedAtUtc    TEXT NOT NULL,
    Version         INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc    TEXT NOT NULL,
    UpdatedAtUtc    TEXT NOT NULL,
    DeletedAtUtc    TEXT NULL
);
CREATE UNIQUE INDEX UX_WaitingDetails_ActiveTask ON WaitingDetails (TaskId) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_WaitingDetails_Person ON WaitingDetails (PersonNormalized) WHERE DeletedAtUtc IS NULL;

-- Follow-up rules. Kind 0 = Fixed (FixedAtUtc), 1 = AfterWaiting (Amount/Unit after waiting
-- started), 2 = BeforeDue (Amount/Unit before the task's due anchor), 3 = AfterInactivity
-- (Amount days without qualifying task activity). Unit 0 = minutes, 1 = hours, 2 = days,
-- 3 = weeks. DueAtUtc is derived (recomputed after committed events, not versioned); NULL
-- means suspended, e.g. BeforeDue on a task without a due date. One active rule per task.
CREATE TABLE FollowUps (
    Id              TEXT NOT NULL PRIMARY KEY,
    TaskId          TEXT NOT NULL REFERENCES Tasks(Id),
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
CREATE UNIQUE INDEX UX_FollowUps_ActiveTask ON FollowUps (TaskId) WHERE DeletedAtUtc IS NULL AND ResolvedAtUtc IS NULL;
CREATE INDEX IX_FollowUps_Due ON FollowUps (DueAtUtc) WHERE DeletedAtUtc IS NULL AND ResolvedAtUtc IS NULL;

-- Desired and delivered notifications. One row per scheduled delivery: a reminder rule's
-- target or a follow-up's due instant. Generation increases whenever the desired schedule of
-- a source changes, so stale OS callbacks are recognized; Sequence numbers snoozes and
-- insistent repeats of the same target. The row is also the notification center history.
-- SourceKind 0 = ReminderRule, 1 = FollowUp.
-- State 0 = Pending, 1 = Delivered, 2 = Acknowledged, 3 = Snoozed, 4 = Cancelled.
-- Reason 0 = Initial, 1 = Snoozed, 2 = Repeat.
-- Presentation 0 = not yet, 1 = shown by the OS, 2 = shown in the app only, 3 = denied/suppressed.
-- OsState 0 = not scheduled with the OS, 1 = scheduled, 2 = failed (retried by the reconciler).
CREATE TABLE NotificationJobs (
    Id                TEXT NOT NULL PRIMARY KEY,
    ProfileId         TEXT NOT NULL REFERENCES Profiles(Id),
    TaskId            TEXT NOT NULL REFERENCES Tasks(Id),
    SourceKind        INTEGER NOT NULL CHECK (SourceKind IN (0,1)),
    SourceId          TEXT NOT NULL,
    Generation        INTEGER NOT NULL CHECK (Generation >= 1),
    Sequence          INTEGER NOT NULL CHECK (Sequence >= 1),
    Reason            INTEGER NOT NULL CHECK (Reason IN (0,1,2)),
    TargetAtUtc       TEXT NOT NULL,
    NotBeforeUtc      TEXT NULL,
    DeliverAtUtc      TEXT NOT NULL,
    State             INTEGER NOT NULL CHECK (State BETWEEN 0 AND 4),
    DeliveredAtUtc    TEXT NULL,
    Presentation      INTEGER NOT NULL DEFAULT 0 CHECK (Presentation BETWEEN 0 AND 3),
    AcknowledgedAtUtc TEXT NULL,
    ActionTaken       TEXT NULL,
    OsState           INTEGER NOT NULL DEFAULT 0 CHECK (OsState IN (0,1,2)),
    OsNotificationId  TEXT NULL,
    OsAttempts        INTEGER NOT NULL DEFAULT 0 CHECK (OsAttempts >= 0),
    OsLastAttemptUtc  TEXT NULL,
    OsError           TEXT NULL,
    CreatedAtUtc      TEXT NOT NULL,
    UpdatedAtUtc      TEXT NOT NULL,
    -- Only delivered rows can be acknowledged or snoozed; cancelled rows were never delivered.
    CHECK ((State IN (1,2,3)) = (DeliveredAtUtc IS NOT NULL)),
    CHECK ((State = 2) = (AcknowledgedAtUtc IS NOT NULL))
);
-- A delivery identity is stable: never two rows for the same source, generation and sequence.
CREATE UNIQUE INDEX UX_NotificationJobs_Key ON NotificationJobs (SourceKind, SourceId, Generation, Sequence);
-- At most one pending delivery per source (rolling horizon, no unbounded pre-registration).
CREATE UNIQUE INDEX UX_NotificationJobs_PendingSource ON NotificationJobs (SourceKind, SourceId) WHERE State = 0;
CREATE INDEX IX_NotificationJobs_StateDeliver ON NotificationJobs (State, DeliverAtUtc);
CREATE INDEX IX_NotificationJobs_Task ON NotificationJobs (TaskId);
CREATE INDEX IX_NotificationJobs_Delivered ON NotificationJobs (ProfileId, DeliveredAtUtc) WHERE DeliveredAtUtc IS NOT NULL;
