-- Chaos migration 026 (PLAN2 P3, SPEC custom views): saved views and their alert baseline.
--
-- SavedViews is a synced aggregate. DefinitionJson is the version-1 condition tree
-- (Chaos.Domain.Views.ViewDefinition) and LayoutJson the chosen layout; both are validated by
-- the application before they are written, so the database only guards their shape. Colour is
-- a key of the fixed palette that tags use. There is deliberately no unique index on Name: two
-- views may share a name, and a synced duplicate then needs no merge rule.
--
-- SavedViewMatches is device-local alert state (PLAN2 §11.2): the task ids a view matched when
-- its alerts were last evaluated on this device. LeftAtUtc is set when a task stops matching,
-- so a task that leaves and returns notifies again only after 24 hours.

CREATE TABLE SavedViews (
    Id             TEXT NOT NULL PRIMARY KEY,
    SpaceId        TEXT NOT NULL REFERENCES Spaces(Id),
    Name           TEXT NOT NULL CHECK (length(trim(Name)) > 0 AND length(Name) <= 80),
    Icon           TEXT NOT NULL DEFAULT 'list' CHECK (length(Icon) BETWEEN 1 AND 40),
    Color          TEXT NULL,
    Pinned         INTEGER NOT NULL DEFAULT 0 CHECK (Pinned IN (0, 1)),
    Position       INTEGER NOT NULL DEFAULT 0 CHECK (Position >= 0),
    DefinitionJson TEXT NOT NULL CHECK (length(DefinitionJson) <= 65536),
    LayoutJson     TEXT NOT NULL CHECK (length(LayoutJson) <= 16384),
    AlertsEnabled  INTEGER NOT NULL DEFAULT 0 CHECK (AlertsEnabled IN (0, 1)),
    Version        INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc   TEXT NOT NULL,
    UpdatedAtUtc   TEXT NOT NULL,
    DeletedAtUtc   TEXT NULL
);
CREATE INDEX IX_SavedViews_SpacePosition ON SavedViews (SpaceId, Position, Id) WHERE DeletedAtUtc IS NULL;

CREATE TABLE SavedViewMatches (
    ViewId       TEXT NOT NULL REFERENCES SavedViews(Id),
    TaskId       TEXT NOT NULL REFERENCES Tasks(Id),
    FirstSeenUtc TEXT NOT NULL,
    LeftAtUtc    TEXT NULL,
    PRIMARY KEY (ViewId, TaskId)
);
CREATE INDEX IX_SavedViewMatches_Task ON SavedViewMatches (TaskId);
