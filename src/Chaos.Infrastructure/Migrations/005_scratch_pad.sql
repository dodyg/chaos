-- Chaos migration 005 (PLAN §15 WP5): the Personal scratch pad. ScratchPadItems and
-- ScratchTaskLinks already exist (migration 001); this adds pad tagging and lookup indexes.

-- Tags on pad items (PLAN §5 ScratchPadTags): composite key and real foreign keys, like
-- TaskTags and SubtaskTags, never an unconstrained polymorphic target.
CREATE TABLE ScratchPadTags (
    PadItemId TEXT NOT NULL REFERENCES ScratchPadItems(Id),
    TagId     TEXT NOT NULL REFERENCES Tags(Id),
    PRIMARY KEY (PadItemId, TagId)
);
CREATE INDEX IX_ScratchPadTags_Tag ON ScratchPadTags (TagId);

-- A task's backlink is found by task ("From the scratch pad"); a task is made from at most
-- one pad selection, so the task side is unique.
CREATE UNIQUE INDEX UX_ScratchTaskLinks_Task ON ScratchTaskLinks (TaskId);

-- The pad lists items of one space by order, archived ones separately.
CREATE INDEX IX_ScratchPadItems_SpaceArchived ON ScratchPadItems (SpaceId, ArchivedAtUtc, Position) WHERE DeletedAtUtc IS NULL;

-- A few characters on each side of the selected text when the task was made. Once the note
-- changes, the backlink prefers the copy of the quote whose surroundings still match, so a
-- repeated phrase is not confused with the original spot.
ALTER TABLE ScratchTaskLinks ADD COLUMN ContextBefore TEXT NULL;
ALTER TABLE ScratchTaskLinks ADD COLUMN ContextAfter TEXT NULL;
