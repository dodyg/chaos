-- Chaos migration 004 (PLAN §15 WP4): diary revisions, checklist conversion markers and
-- undo bookkeeping. DiaryEntries, TaskHistory and UndoEntries already exist (migration 001).

-- Every saved state of a diary entry, oldest first (PLAN §9: "store edits as revisions").
-- Revision 1 is the text as first written; each edit, delete, restore or undo appends one.
CREATE TABLE DiaryRevisions (
    EntryId      TEXT NOT NULL REFERENCES DiaryEntries(Id),
    Revision     INTEGER NOT NULL CHECK (Revision >= 1),
    RawMarkdown  TEXT NOT NULL,
    Deleted      INTEGER NOT NULL DEFAULT 0 CHECK (Deleted IN (0,1)),
    RecordedAtUtc TEXT NOT NULL,
    PRIMARY KEY (EntryId, Revision)
);
CREATE INDEX IX_DiaryEntries_TaskActive ON DiaryEntries (TaskId, EntryAtUtc) WHERE DeletedAtUtc IS NULL;

-- Checklist-to-subtask conversion markers (PLAN §9). One row per converted checklist item:
-- a repeated conversion of the same item finds the marker and creates nothing while the
-- subtask it made still exists. ItemKey is a hash of the item text and its occurrence.
CREATE TABLE ChecklistConversions (
    TaskId       TEXT NOT NULL REFERENCES Tasks(Id),
    ItemKey      TEXT NOT NULL CHECK (length(ItemKey) = 64),
    SubtaskId    TEXT NOT NULL REFERENCES Subtasks(Id),
    OperationId  TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (TaskId, ItemKey)
);

-- Undo lists the newest entries that are not undone yet; pruning keeps the newest 100.
CREATE INDEX IX_UndoEntries_ProfileOpen ON UndoEntries (ProfileId, AppliedAtUtc DESC) WHERE UndoneAtUtc IS NULL;
