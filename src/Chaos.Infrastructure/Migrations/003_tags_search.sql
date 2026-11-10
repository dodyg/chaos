-- Chaos migration 003 (PLAN §15 WP3): tag integrity and the full-text search index.

-- Sibling tag names are unique per owner (the 001 index ignored the owner, which would make
-- two profiles' root tags collide once sharing arrives). A NULL parent folds to '' so root
-- tags are covered too; the partial index ignores tombstoned tags.
DROP INDEX UX_Tags_ActiveSiblingName;
CREATE UNIQUE INDEX UX_Tags_ActiveSiblingName
    ON Tags (OwnerProfileId, COALESCE(ParentTagId, ''), NormalizedName) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_Tags_OwnerParentPosition
    ON Tags (OwnerProfileId, ParentTagId, Position) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_SubtaskTags_Subtask ON SubtaskTags (SubtaskId);

-- A tag can never be its own parent. Longer cycles are rejected by the tag service, which
-- walks the ancestor chain before every move (SQLite cannot express that as a CHECK).
-- Each trigger stays on one line: the migration runner ends a statement at a line ending in ';'.
CREATE TRIGGER TR_Tags_NoSelfParent_Insert BEFORE INSERT ON Tags WHEN NEW.ParentTagId = NEW.Id BEGIN SELECT RAISE(ABORT, 'A tag cannot be its own parent.'); END;
CREATE TRIGGER TR_Tags_NoSelfParent_Update BEFORE UPDATE OF ParentTagId ON Tags WHEN NEW.ParentTagId = NEW.Id BEGIN SELECT RAISE(ABORT, 'A tag cannot be its own parent.'); END;

-- One search document per active task, subtask, diary entry or pad item (PLAN §5). The
-- document row maps a stable integer rowid to its target; SearchIndex holds the normalized
-- text under the same rowid. Documents are written in the same transaction as their source
-- row and removed when the source is tombstoned. Deletion/archive state is still enforced
-- by ordinary SQL predicates on the source tables, never by the presence of a match.
CREATE TABLE SearchDocuments (
    RowId      INTEGER PRIMARY KEY,
    TargetKind INTEGER NOT NULL CHECK (TargetKind IN (1,2,3,4)), -- 1=Task 2=Subtask 3=Diary 4=Pad
    TargetId   TEXT NOT NULL,
    TaskId     TEXT NULL,                                        -- owning top-level task (NULL for pad items)
    UNIQUE (TargetKind, TargetId),
    CHECK ((TargetKind = 4) = (TaskId IS NULL))
);
CREATE INDEX IX_SearchDocuments_Task ON SearchDocuments (TaskId) WHERE TaskId IS NOT NULL;

CREATE VIRTUAL TABLE SearchIndex USING fts5(
    Title,
    Body,
    tokenize = 'unicode61 remove_diacritics 0',
    prefix = '2 3'
);

-- Normalization version of the indexed text. Startup rebuilds the index whenever it differs
-- from the version the running app writes (including the first start after this migration,
-- which fills the index for existing rows).
CREATE TABLE SearchIndexState (
    Id                   INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
    NormalizationVersion INTEGER NOT NULL CHECK (NormalizationVersion >= 0),
    RebuiltAtUtc         TEXT NULL
);
INSERT INTO SearchIndexState (Id, NormalizationVersion, RebuiltAtUtc) VALUES (1, 0, NULL);
