-- Chaos migration 023 (SPEC Pages): titled Markdown pages in a space, in a tree, with daily
-- pages, version history, tags from front matter and "Make task" backlinks.

-- A page belongs to one space and optionally sits under another page of that space. A daily
-- page holds one local date (yyyy-MM-dd) and never sits in the tree. DraftVersion is the
-- highest editor revision the store acknowledged, as on pad items, so a late or repeated
-- autosave never replaces newer text.
CREATE TABLE Pages (
    Id           TEXT NOT NULL PRIMARY KEY,
    SpaceId      TEXT NOT NULL REFERENCES Spaces(Id),
    ParentPageId TEXT NULL REFERENCES Pages(Id),
    Title        TEXT NOT NULL,
    RawMarkdown  TEXT NOT NULL,
    Position     INTEGER NOT NULL CHECK (Position >= 0),
    DailyDate    TEXT NULL,
    DraftVersion INTEGER NOT NULL DEFAULT 0 CHECK (DraftVersion >= 0),
    Version      INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL,
    CHECK (DailyDate IS NULL OR ParentPageId IS NULL)
);
CREATE INDEX IX_Pages_SpaceParentPosition ON Pages (SpaceId, ParentPageId, Position) WHERE DeletedAtUtc IS NULL;
-- One active daily page per date and space; a deleted one does not block writing that day again.
CREATE UNIQUE INDEX UX_Pages_SpaceDaily ON Pages (SpaceId, DailyDate) WHERE DailyDate IS NOT NULL AND DeletedAtUtc IS NULL;
CREATE TRIGGER TR_Pages_NoSelfParent_Insert BEFORE INSERT ON Pages WHEN NEW.ParentPageId = NEW.Id BEGIN SELECT RAISE(ABORT, 'A page cannot be its own parent.'); END;
CREATE TRIGGER TR_Pages_NoSelfParent_Update BEFORE UPDATE OF ParentPageId ON Pages WHEN NEW.ParentPageId = NEW.Id BEGIN SELECT RAISE(ABORT, 'A page cannot be its own parent.'); END;

-- Earlier versions of a page (SPEC "Saved as you go"). Saves within a few minutes of the
-- newest version update it in place, so a version is one sitting of writing, not one keystroke.
CREATE TABLE PageRevisions (
    PageId       TEXT NOT NULL REFERENCES Pages(Id),
    Number       INTEGER NOT NULL CHECK (Number >= 1),
    Title        TEXT NOT NULL,
    RawMarkdown  TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (PageId, Number)
);

-- Tasks made from a selection on a page, with the same anchor rules as ScratchTaskLinks.
CREATE TABLE PageTaskLinks (
    PageId         TEXT NOT NULL REFERENCES Pages(Id),
    TaskId         TEXT NOT NULL REFERENCES Tasks(Id),
    SourceRevision INTEGER NOT NULL CHECK (SourceRevision >= 0),
    StartIndex     INTEGER NOT NULL CHECK (StartIndex >= 0),
    EndIndex       INTEGER NOT NULL CHECK (EndIndex >= 0),
    Quote          TEXT NOT NULL,
    CreatedAtUtc   TEXT NOT NULL,
    ContextBefore  TEXT NULL,
    ContextAfter   TEXT NULL,
    PRIMARY KEY (PageId, TaskId)
);
CREATE UNIQUE INDEX UX_PageTaskLinks_Task ON PageTaskLinks (TaskId);

-- Tags named in a page's front matter that exist as tags. Derived from the text whenever the
-- page row is written, so it always agrees with the page.
CREATE TABLE PageTags (
    PageId TEXT NOT NULL REFERENCES Pages(Id),
    TagId  TEXT NOT NULL REFERENCES Tags(Id),
    PRIMARY KEY (PageId, TagId)
);
CREATE INDEX IX_PageTags_Tag ON PageTags (TagId);

-- Search gains pages (kind 5). The index is rebuildable, so the tables are recreated with the
-- wider check and the normalization version reset, which makes the next start rebuild them.
DROP TABLE SearchIndex;
DROP TABLE SearchDocuments;
CREATE TABLE SearchDocuments (
    RowId      INTEGER PRIMARY KEY,
    TargetKind INTEGER NOT NULL CHECK (TargetKind IN (1,2,3,4,5)), -- 1=Task 2=Subtask 3=Diary 4=Pad 5=Page
    TargetId   TEXT NOT NULL,
    TaskId     TEXT NULL,                                          -- owning top-level task (NULL for pad items and pages)
    UNIQUE (TargetKind, TargetId),
    CHECK ((TargetKind IN (4,5)) = (TaskId IS NULL))
);
CREATE INDEX IX_SearchDocuments_Task ON SearchDocuments (TaskId) WHERE TaskId IS NOT NULL;
CREATE VIRTUAL TABLE SearchIndex USING fts5(
    Title,
    Body,
    tokenize = 'unicode61 remove_diacritics 0',
    prefix = '2 3'
);
UPDATE SearchIndexState SET NormalizationVersion = 0, RebuiltAtUtc = NULL WHERE Id = 1;
