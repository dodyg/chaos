-- Q16: Google Calendar time blocking (PLAN3 §11). The choice travels with the profile in the account stream
-- (group timeblock): which Google account, task titles or Busy, which spaces, which device pushes and the calendar
-- the app created. TimeBlockEvents is the record of the one Google event per task: the client event id derived from
-- the task, the etag Google last returned, the hash of what was pushed and when Google last moved the event. It
-- travels with the account too, so a new runner device knows what already exists. The sync token is this device's
-- position in the calendar's change feed and stays here (TimeBlockState).
ALTER TABLE Profiles ADD COLUMN TimeBlockEnabled INTEGER NOT NULL DEFAULT 0 CHECK (TimeBlockEnabled IN (0, 1));
ALTER TABLE Profiles ADD COLUMN TimeBlockAccountId TEXT NULL;
ALTER TABLE Profiles ADD COLUMN TimeBlockShowTitles INTEGER NOT NULL DEFAULT 1 CHECK (TimeBlockShowTitles IN (0, 1));
ALTER TABLE Profiles ADD COLUMN TimeBlockRunnerDeviceId TEXT NULL;
ALTER TABLE Profiles ADD COLUMN TimeBlockCalendarId TEXT NULL;
ALTER TABLE Profiles ADD COLUMN TimeBlockSpacesJson TEXT NULL;
-- No foreign key to Tasks: a row must outlive its task until the runner has deleted the event at Google, and the
-- runner's hard delete of the row travels as a purge (like a label mapping).
CREATE TABLE TimeBlockEvents (
    TaskId           TEXT NOT NULL PRIMARY KEY,
    EventId          TEXT NOT NULL CHECK (length(EventId) BETWEEN 5 AND 1024),
    ETag             TEXT NULL,
    PushedHash       TEXT NULL,
    LastSyncedAtUtc  TEXT NULL,
    RemoteMovedAtUtc TEXT NULL,
    Version          INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc     TEXT NOT NULL,
    UpdatedAtUtc     TEXT NOT NULL
);
CREATE INDEX IX_TimeBlockEvents_Event ON TimeBlockEvents(EventId);
CREATE TABLE TimeBlockState (
    Id              INTEGER PRIMARY KEY CHECK (Id = 1),
    CalendarId      TEXT NULL,
    SyncToken       TEXT NULL,
    PageToken       TEXT NULL,
    LastPassUtc     TEXT NULL,
    LastError       TEXT NULL,
    NeedsSignIn     INTEGER NOT NULL DEFAULT 0 CHECK (NeedsSignIn IN (0, 1))
);
