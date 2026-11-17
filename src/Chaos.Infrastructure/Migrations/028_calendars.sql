-- Chaos migration 028 (PLAN2 P5, calendar feeds): other calendars' meetings, read-only.
-- (PLAN2 names this 027; 027 was already taken by view alerts.)
--
-- CalendarSources is synced: the name, colour, busy flag and ordering follow the account. The feed
-- ADDRESS is a credential and is never stored here; it lives in the OS-protected secret store under
-- the source id. CalendarEvents and CalendarSourceState are device-local: each device fetches for
-- itself and replaces a source's window in one transaction. CalendarEventLinks (a task made from a
-- meeting) is synced and keeps the meeting title and link so another device can show them without
-- the cache.
--
-- Provider: 1 Feed (ICS), 2 Google (P6).

CREATE TABLE CalendarSources (
    Id           TEXT NOT NULL PRIMARY KEY,
    Provider     INTEGER NOT NULL CHECK (Provider IN (1, 2)),
    AccountRef   TEXT NULL,
    ExternalId   TEXT NOT NULL,
    Name         TEXT NOT NULL CHECK (length(Name) BETWEEN 1 AND 120),
    Color        TEXT NOT NULL,
    CountsAsBusy INTEGER NOT NULL DEFAULT 1 CHECK (CountsAsBusy IN (0, 1)),
    Enabled      INTEGER NOT NULL DEFAULT 1 CHECK (Enabled IN (0, 1)),
    OwnerEmail   TEXT NULL,   -- hides this person's own declined invitations when the feed does not say whose it is
    Position     INTEGER NOT NULL DEFAULT 0,
    Version      INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL
);
CREATE INDEX IX_CalendarSources_Active ON CalendarSources (Position, Id) WHERE DeletedAtUtc IS NULL;

CREATE TABLE CalendarEvents (
    SourceId    TEXT NOT NULL REFERENCES CalendarSources(Id) ON DELETE CASCADE,
    ExternalId  TEXT NOT NULL,
    StartUtc    TEXT NOT NULL,   -- timed: the instant; all-day: midnight UTC of StartDate (sort key only)
    EndUtc      TEXT NOT NULL,
    AllDay      INTEGER NOT NULL CHECK (AllDay IN (0, 1)),
    StartDate   TEXT NULL,       -- all-day only, local dates, end exclusive
    EndDate     TEXT NULL,
    Title       TEXT NOT NULL,
    Location    TEXT NULL,
    VideoUrl    TEXT NULL,
    WebUrl      TEXT NULL,
    Busy        INTEGER NOT NULL CHECK (Busy IN (0, 1)),
    FetchedUtc  TEXT NOT NULL,
    PRIMARY KEY (SourceId, ExternalId),
    CHECK ((AllDay = 1) = (StartDate IS NOT NULL AND EndDate IS NOT NULL))
);
CREATE INDEX IX_CalendarEvents_Range ON CalendarEvents (StartUtc, EndUtc);

CREATE TABLE CalendarSourceState (
    SourceId       TEXT NOT NULL PRIMARY KEY REFERENCES CalendarSources(Id) ON DELETE CASCADE,
    LastAttemptUtc TEXT NULL,
    LastSuccessUtc TEXT NULL,
    LastError      TEXT NULL,
    ETag           TEXT NULL,
    LastModified   TEXT NULL
);

CREATE TABLE CalendarEventLinks (
    Id           TEXT NOT NULL PRIMARY KEY,   -- calendareventlink:{TaskId}, deterministic
    TaskId       TEXT NOT NULL REFERENCES Tasks(Id),
    Provider     INTEGER NOT NULL CHECK (Provider IN (1, 2)),
    CalendarId   TEXT NOT NULL,
    EventId      TEXT NOT NULL,
    EventStartUtc TEXT NOT NULL,
    EventTitle   TEXT NOT NULL,
    EventWebUrl  TEXT NULL,
    Version      INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL
);
CREATE UNIQUE INDEX UX_CalendarEventLinks_Task ON CalendarEventLinks (TaskId) WHERE DeletedAtUtc IS NULL;

-- "Include meetings in summaries" (PLAN2 §10.4): off by default. A synced profile setting, so it
-- follows the person; a plain summary lists meetings only when it is on.
ALTER TABLE Profiles ADD COLUMN IncludeMeetingsInSummaries INTEGER NOT NULL DEFAULT 0 CHECK (IncludeMeetingsInSummaries IN (0, 1));
