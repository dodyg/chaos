-- My Day (PLAN §10, work package 8). One plan per profile and local date; items are
-- profile-owned membership records that never edit a task's planned/due fields.

CREATE TABLE DayPlans (
    Id                    TEXT NOT NULL PRIMARY KEY,
    ProfileId             TEXT NOT NULL REFERENCES Profiles(Id),
    LocalDate             TEXT NOT NULL CHECK (length(LocalDate) = 10),
    -- Weekly seeding (package 9) records which confirmed week revision seeded this day.
    SeededWeekPlanId      TEXT NULL,
    SeededWeekRevision    INTEGER NULL CHECK (SeededWeekRevision IS NULL OR SeededWeekRevision >= 1),
    InitializedAtUtc      TEXT NOT NULL,
    Version               INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc          TEXT NOT NULL,
    UpdatedAtUtc          TEXT NOT NULL,
    CHECK ((SeededWeekPlanId IS NULL) = (SeededWeekRevision IS NULL))
);

-- The unique key that makes day initialization idempotent across midnight/restart races.
CREATE UNIQUE INDEX UX_DayPlans_ProfileDate ON DayPlans (ProfileId, LocalDate);

-- Kind 0 = Task. Follow-up items (kind 1) arrive with package 10 together with the
-- FollowUps table and their own target column.
-- Origin 0 = Manual, 1 = Week (seeded by a confirmed weekly plan), 2 = CarryOver.
-- A removed item keeps its row with RemovedAtUtc set, so reopening the day or a later
-- week seed never silently reinserts a task the person took out.
CREATE TABLE DayPlanItems (
    Id            TEXT NOT NULL PRIMARY KEY,
    DayPlanId     TEXT NOT NULL REFERENCES DayPlans(Id),
    Kind          INTEGER NOT NULL CHECK (Kind = 0),
    TaskId        TEXT NOT NULL REFERENCES Tasks(Id),
    Position      INTEGER NOT NULL,
    Origin        INTEGER NOT NULL CHECK (Origin IN (0,1,2)),
    AddedAtUtc    TEXT NOT NULL,
    RemovedAtUtc  TEXT NULL,
    Version       INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc  TEXT NOT NULL,
    UpdatedAtUtc  TEXT NOT NULL
);

-- One membership row per task per day (active or removed): re-adding reactivates it.
CREATE UNIQUE INDEX UX_DayPlanItems_PlanTask ON DayPlanItems (DayPlanId, TaskId);
CREATE INDEX IX_DayPlanItems_PlanPosition ON DayPlanItems (DayPlanId, Position) WHERE RemovedAtUtc IS NULL;
CREATE INDEX IX_DayPlanItems_Task ON DayPlanItems (TaskId);
