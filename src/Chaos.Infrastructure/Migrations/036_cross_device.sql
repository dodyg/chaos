-- Shared decision for one original reminder target; jobs and application markers stay local.
CREATE TABLE ReminderAcknowledgements (
    Id TEXT PRIMARY KEY NOT NULL,
    SourceKind INTEGER NOT NULL,
    SourceId TEXT NOT NULL,
    TargetAtUtc TEXT NOT NULL,
    Action TEXT NOT NULL,
    SnoozedUntilUtc TEXT NULL,
    Revision TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE TABLE AppliedReminderAcknowledgements (
    Id TEXT PRIMARY KEY NOT NULL,
    Revision TEXT NOT NULL
);
