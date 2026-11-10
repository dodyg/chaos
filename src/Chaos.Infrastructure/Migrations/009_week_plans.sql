-- My Week (PLAN §10, work package 9). Weekly plans are profile-owned records: a weekly
-- assignment never changes a task's planned or due date.

-- State 0 = Draft only (never confirmed, or revived after a week-start change),
-- 1 = Confirmed (the active plan for its seven dates), 2 = Superseded by a week-start change.
-- The draft lives in DraftPayload, apart from the confirmed rows below, so editing a draft
-- can never change the confirmed assignment set. DraftBaseRevision is the confirmed revision
-- the draft started from.
CREATE TABLE WeekPlans (
    Id                 TEXT NOT NULL PRIMARY KEY,
    ProfileId          TEXT NOT NULL REFERENCES Profiles(Id),
    WeekStartDate      TEXT NOT NULL CHECK (length(WeekStartDate) = 10),
    State              INTEGER NOT NULL CHECK (State IN (0,1,2)),
    ConfirmedRevision  INTEGER NOT NULL DEFAULT 0 CHECK (ConfirmedRevision >= 0),
    ConfirmedAtUtc     TEXT NULL,
    DraftPayload       TEXT NULL,
    DraftRevision      INTEGER NOT NULL DEFAULT 0 CHECK (DraftRevision >= 0),
    DraftBaseRevision  INTEGER NULL CHECK (DraftBaseRevision IS NULL OR DraftBaseRevision >= 0),
    Version            INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc       TEXT NOT NULL,
    UpdatedAtUtc       TEXT NOT NULL,
    CHECK ((ConfirmedRevision = 0) = (ConfirmedAtUtc IS NULL)),
    CHECK ((DraftPayload IS NULL) = (DraftBaseRevision IS NULL)),
    CHECK (State <> 1 OR ConfirmedRevision >= 1)
);

CREATE UNIQUE INDEX UX_WeekPlans_ProfileWeek ON WeekPlans (ProfileId, WeekStartDate);

-- Every confirmed revision keeps a snapshot of its goals and assignments (previous plan history).
CREATE TABLE WeekPlanRevisions (
    WeekPlanId      TEXT NOT NULL REFERENCES WeekPlans(Id),
    Revision        INTEGER NOT NULL CHECK (Revision >= 1),
    Snapshot        TEXT NOT NULL,
    ConfirmedAtUtc  TEXT NOT NULL,
    PRIMARY KEY (WeekPlanId, Revision)
);

-- Outcome 0 = Pending, 1 = Achieved, 2 = LetGo (assessed by the person, never automatically).
CREATE TABLE WeekGoals (
    Id            TEXT NOT NULL PRIMARY KEY,
    WeekPlanId    TEXT NOT NULL REFERENCES WeekPlans(Id),
    Title         TEXT NOT NULL CHECK (length(trim(Title)) > 0),
    Position      INTEGER NOT NULL,
    ProjectId     TEXT NULL REFERENCES Containers(Id),
    Outcome       INTEGER NOT NULL DEFAULT 0 CHECK (Outcome IN (0,1,2)),
    Version       INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc  TEXT NOT NULL,
    UpdatedAtUtc  TEXT NOT NULL
);

CREATE INDEX IX_WeekGoals_Plan ON WeekGoals (WeekPlanId, Position);

CREATE TABLE WeekGoalTasks (
    GoalId  TEXT NOT NULL REFERENCES WeekGoals(Id) ON DELETE CASCADE,
    TaskId  TEXT NOT NULL REFERENCES Tasks(Id),
    PRIMARY KEY (GoalId, TaskId)
);

CREATE INDEX IX_WeekGoalTasks_Task ON WeekGoalTasks (TaskId);

-- A task has at most one place in a week: one day, or the week's unassigned pool
-- (LocalDate NULL). Origin 0 = Planned, 1 = Carried from the previous week.
CREATE TABLE WeekAssignments (
    Id            TEXT NOT NULL PRIMARY KEY,
    WeekPlanId    TEXT NOT NULL REFERENCES WeekPlans(Id),
    TaskId        TEXT NOT NULL REFERENCES Tasks(Id),
    LocalDate     TEXT NULL CHECK (LocalDate IS NULL OR length(LocalDate) = 10),
    Position      INTEGER NOT NULL,
    Origin        INTEGER NOT NULL CHECK (Origin IN (0,1)),
    CreatedAtUtc  TEXT NOT NULL,
    UpdatedAtUtc  TEXT NOT NULL
);

CREATE UNIQUE INDEX UX_WeekAssignments_PlanTask ON WeekAssignments (WeekPlanId, TaskId);
CREATE INDEX IX_WeekAssignments_PlanDay ON WeekAssignments (WeekPlanId, LocalDate, Position);
CREATE INDEX IX_WeekAssignments_Task ON WeekAssignments (TaskId);

-- The day of an assignment must be one of its plan's seven dates. Each trigger stays on one
-- line because the migration runner ends a statement at a line ending in a semicolon.
CREATE TRIGGER TR_WeekAssignments_DayInWeek_Insert BEFORE INSERT ON WeekAssignments WHEN NEW.LocalDate IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WeekPlans p WHERE p.Id = NEW.WeekPlanId AND NEW.LocalDate BETWEEN p.WeekStartDate AND date(p.WeekStartDate, '+6 days')) BEGIN SELECT RAISE(ABORT, 'A week assignment must fall within its week.'); END;
CREATE TRIGGER TR_WeekAssignments_DayInWeek_Update BEFORE UPDATE OF LocalDate, WeekPlanId ON WeekAssignments WHEN NEW.LocalDate IS NOT NULL AND NOT EXISTS (SELECT 1 FROM WeekPlans p WHERE p.Id = NEW.WeekPlanId AND NEW.LocalDate BETWEEN p.WeekStartDate AND date(p.WeekStartDate, '+6 days')) BEGIN SELECT RAISE(ABORT, 'A week assignment must fall within its week.'); END;

-- Default availability per weekday (0 = Sunday). Without rows the built-in default applies:
-- 8 hours on weekdays, nothing at weekends.
CREATE TABLE AvailabilityRules (
    ProfileId     TEXT NOT NULL REFERENCES Profiles(Id),
    Weekday       INTEGER NOT NULL CHECK (Weekday BETWEEN 0 AND 6),
    TotalMinutes  INTEGER NOT NULL CHECK (TotalMinutes BETWEEN 0 AND 1440),
    WorkMinutes   INTEGER NULL CHECK (WorkMinutes IS NULL OR WorkMinutes BETWEEN 0 AND 1440),
    UpdatedAtUtc  TEXT NOT NULL,
    PRIMARY KEY (ProfileId, Weekday),
    CHECK (WorkMinutes IS NULL OR WorkMinutes <= TotalMinutes)
);

-- A one-date override of the weekday default, chosen while planning a week.
CREATE TABLE WeekAvailabilityOverrides (
    ProfileId     TEXT NOT NULL REFERENCES Profiles(Id),
    LocalDate     TEXT NOT NULL CHECK (length(LocalDate) = 10),
    TotalMinutes  INTEGER NOT NULL CHECK (TotalMinutes BETWEEN 0 AND 1440),
    WorkMinutes   INTEGER NULL CHECK (WorkMinutes IS NULL OR WorkMinutes BETWEEN 0 AND 1440),
    UpdatedAtUtc  TEXT NOT NULL,
    PRIMARY KEY (ProfileId, LocalDate),
    CHECK (WorkMinutes IS NULL OR WorkMinutes <= TotalMinutes)
);
