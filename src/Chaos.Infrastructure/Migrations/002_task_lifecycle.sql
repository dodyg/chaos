-- Chaos migration 002 (PLAN §15 WP2): reusable task templates.
-- A template holds a versioned JSON payload with task fields and subtask structure;
-- dates are stored as relative day offsets, never as absolute timestamps, so an
-- instantiated task gets fresh dates (PLAN §6 template rules).

CREATE TABLE TaskTemplates (
    Id             TEXT NOT NULL PRIMARY KEY,
    SpaceId        TEXT NOT NULL REFERENCES Spaces(Id),
    Name           TEXT NOT NULL CHECK (length(trim(Name)) > 0),
    NormalizedName TEXT NOT NULL,
    Payload        TEXT NOT NULL,
    Version        INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc   TEXT NOT NULL,
    UpdatedAtUtc   TEXT NOT NULL,
    DeletedAtUtc   TEXT NULL
);

-- Unique active template name per space (same partial-index pattern as Containers).
CREATE UNIQUE INDEX UX_TaskTemplates_ActiveNamePerSpace
    ON TaskTemplates (SpaceId, NormalizedName) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_TaskTemplates_Space ON TaskTemplates (SpaceId) WHERE DeletedAtUtc IS NULL;
