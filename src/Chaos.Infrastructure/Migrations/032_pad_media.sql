-- Chaos migration 032 (PLAN2 P10, pad drawings). The plan numbered this 031, but 030 went to the
-- activity log (P6a) and 031 to attachments (P9), so this is the next free number.

-- A pad item is either typed text (0) or a drawing (1). Existing rows are text.
ALTER TABLE ScratchPadItems ADD COLUMN Kind INTEGER NOT NULL DEFAULT 0 CHECK (Kind IN (0,1));

-- The strokes of a drawing item, one row per drawing, merged whole (latest wins). Width and Height
-- are the drawing's units (the canvas size strokes were drawn on). PreviewAttachmentId points at
-- the PNG preview attachment (owner kind 3, the pad item); it is null until editing first stops.
-- Revision is this device's autosave counter (like ScratchPadItems.DraftVersion): a delayed or
-- repeated save with an older revision never replaces newer strokes. The row has no tombstone of
-- its own: it lives and is purged with its pad item.
CREATE TABLE PadDrawings (
    PadItemId           TEXT NOT NULL PRIMARY KEY REFERENCES ScratchPadItems(Id),
    StrokesJson         TEXT NOT NULL,
    Width               INTEGER NOT NULL CHECK (Width BETWEEN 1 AND 20000),
    Height              INTEGER NOT NULL CHECK (Height BETWEEN 1 AND 20000),
    PreviewAttachmentId TEXT NULL,
    Revision            INTEGER NOT NULL DEFAULT 0 CHECK (Revision >= 0),
    Version             INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc        TEXT NOT NULL,
    UpdatedAtUtc        TEXT NOT NULL
);

-- A task made from part of a drawing keeps the rectangle in drawing units: {"x":..,"y":..,"w":..,"h":..}.
-- Text links leave it null; drawing links store an empty Quote and zero offsets.
ALTER TABLE ScratchTaskLinks ADD COLUMN RegionJson TEXT NULL;
