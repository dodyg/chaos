-- Plain daily/weekly summaries and the journal (PLAN §12, work package 11).

-- Summary schedule settings. SummaryTime (evening daily summary) already exists from
-- migration 001 but nothing could set it; MorningTime and WeeklySummaryDay are new. A NULL
-- time or day means that summary is not generated on a schedule (it can still be generated
-- by hand). Existing profiles start with the defaults a new profile gets: morning 08:00,
-- evening 18:00 and the weekly summary on the last day of the configured week.
ALTER TABLE Profiles ADD COLUMN MorningTime TEXT NULL CHECK (MorningTime IS NULL OR length(MorningTime) = 8);
ALTER TABLE Profiles ADD COLUMN WeeklySummaryDay INTEGER NULL CHECK (WeeklySummaryDay IS NULL OR WeeklySummaryDay BETWEEN 0 AND 6);
UPDATE Profiles SET
    MorningTime = '08:00:00',
    SummaryTime = COALESCE(SummaryTime, '18:00:00'),
    WeeklySummaryDay = (FirstWeekday + 6) % 7;

-- One row per generated revision of a summary. Kind 0 = Morning (the day ahead), 1 = Daily
-- (evening: how the day went), 2 = Weekly (seven configured local dates). The period is a
-- half-open UTC range [PeriodStartUtc, PeriodEndUtc) computed from local dates in the
-- profile zone; CapturedThroughUtc says how far the facts reach (a summary generated before
-- its period ends is labelled "through HH:mm"). Facts is versioned JSON holding captured
-- task titles and list names, so a later rename or deletion never rewrites the journal.
-- Trigger 0 = Scheduled, 1 = CatchUp (a missed period generated at the next start), 2 = Manual.
-- Generation is idempotent per (profile, kind, period start): scheduled and catch-up runs
-- never add a revision when one exists; an explicit regeneration adds the next revision and
-- keeps the earlier ones as history.
-- PromptAtUtc is when the "summary ready" prompt may appear (after quiet hours); NULL means
-- no prompt. PromptSeenAtUtc records that it was opened or dismissed.
CREATE TABLE Summaries (
    Id                 TEXT NOT NULL PRIMARY KEY,
    ProfileId          TEXT NOT NULL REFERENCES Profiles(Id),
    Kind               INTEGER NOT NULL CHECK (Kind IN (0,1,2)),
    PeriodStart        TEXT NOT NULL CHECK (length(PeriodStart) = 10),
    PeriodEnd          TEXT NOT NULL CHECK (length(PeriodEnd) = 10),
    Revision           INTEGER NOT NULL CHECK (Revision >= 1),
    PeriodStartUtc     TEXT NOT NULL,
    PeriodEndUtc       TEXT NOT NULL,
    CapturedThroughUtc TEXT NOT NULL,
    ZoneId             TEXT NOT NULL,
    Trigger            INTEGER NOT NULL CHECK (Trigger IN (0,1,2)),
    Facts              TEXT NOT NULL,
    ResourceVersion    INTEGER NOT NULL CHECK (ResourceVersion >= 1),
    GeneratedAtUtc     TEXT NOT NULL,
    PromptAtUtc        TEXT NULL,
    PromptSeenAtUtc    TEXT NULL,
    CHECK (PeriodEnd >= PeriodStart),
    CHECK (PeriodEndUtc > PeriodStartUtc),
    CHECK (CapturedThroughUtc >= PeriodStartUtc AND CapturedThroughUtc <= PeriodEndUtc),
    CHECK (Kind <> 2 OR julianday(PeriodEnd) - julianday(PeriodStart) = 6),
    CHECK (Kind = 2 OR PeriodEnd = PeriodStart)
);
CREATE UNIQUE INDEX UX_Summaries_PeriodRevision ON Summaries (ProfileId, Kind, PeriodStart, Revision);
CREATE INDEX IX_Summaries_Journal ON Summaries (ProfileId, PeriodEnd, Kind);
CREATE INDEX IX_Summaries_Prompt ON Summaries (ProfileId, PromptAtUtc) WHERE PromptAtUtc IS NOT NULL AND PromptSeenAtUtc IS NULL;

-- Period queries read task history and diary entries by instant across tasks.
CREATE INDEX IX_TaskHistory_Effective ON TaskHistory (EffectiveAtUtc);
CREATE INDEX IX_DiaryEntries_EntryAt ON DiaryEntries (EntryAtUtc) WHERE DeletedAtUtc IS NULL;
