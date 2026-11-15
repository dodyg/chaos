-- Chaos migration 025 (PLAN2 P2, SPEC Kanban): per-target board settings. One row per list,
-- project (and, from P3, custom view) that has been shown as a board with non-default
-- settings. The ID is deterministic (boardsetting:{targetKind}:{targetId}, PLAN2 §7.5 style),
-- so two devices that configure the same target offline derive the same row and no duplicate
-- of UX_BoardSettings_Target can arise.

CREATE TABLE BoardSettings (
    Id               TEXT NOT NULL PRIMARY KEY,
    TargetKind       INTEGER NOT NULL CHECK (TargetKind IN (1, 2)), -- 1=Container 2=View
    TargetId         TEXT NOT NULL,
    GroupBy          INTEGER NOT NULL DEFAULT 0 CHECK (GroupBy BETWEEN 0 AND 3),        -- 0=Status 1=Section 2=Priority 3=TagGroup
    SwimlaneBy       INTEGER NOT NULL DEFAULT 0 CHECK (SwimlaneBy BETWEEN 0 AND 4),     -- 0=None 1=Priority 2=TagGroup 3=Category 4=Container
    ColumnTagId      TEXT NULL,   -- the parent tag whose children are the columns (GroupBy = 3)
    SwimlaneTagId    TEXT NULL,   -- the parent tag whose children are the lanes (SwimlaneBy = 2)
    WipLimitsJson    TEXT NOT NULL DEFAULT '{}',
    HiddenColumnsJson TEXT NOT NULL DEFAULT '[]',
    Version          INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc     TEXT NOT NULL,
    UpdatedAtUtc     TEXT NOT NULL,
    DeletedAtUtc     TEXT NULL,
    CHECK (GroupBy <> 3 OR ColumnTagId IS NOT NULL),
    CHECK (SwimlaneBy <> 2 OR SwimlaneTagId IS NOT NULL)
);
CREATE UNIQUE INDEX UX_BoardSettings_Target ON BoardSettings (TargetKind, TargetId) WHERE DeletedAtUtc IS NULL;
