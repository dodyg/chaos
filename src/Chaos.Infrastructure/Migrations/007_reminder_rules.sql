-- Durable reminder rules only; scheduling and delivery are implemented in package 10.
CREATE TABLE ReminderRules (
    Id TEXT NOT NULL PRIMARY KEY,
    ProfileId TEXT NOT NULL REFERENCES Profiles(Id),
    TaskId TEXT NOT NULL REFERENCES Tasks(Id),
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
CREATE INDEX IX_ReminderRules_Task ON ReminderRules(TaskId) WHERE DeletedAtUtc IS NULL;
