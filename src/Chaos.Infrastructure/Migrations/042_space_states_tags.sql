CREATE TABLE SpaceStates (
    Id TEXT NOT NULL PRIMARY KEY,
    SpaceId TEXT NOT NULL REFERENCES Spaces(Id),
    Name TEXT NOT NULL CHECK (length(trim(Name)) > 0),
    NormalizedName TEXT NOT NULL CHECK (length(NormalizedName) > 0),
    BaseStatus INTEGER NOT NULL CHECK (BaseStatus BETWEEN 0 AND 3),
    Position INTEGER NOT NULL DEFAULT 0 CHECK (Position >= 0),
    Version INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL
);
CREATE UNIQUE INDEX UX_SpaceStates_ActiveName ON SpaceStates (SpaceId, NormalizedName) WHERE DeletedAtUtc IS NULL;
ALTER TABLE Tasks ADD COLUMN CustomStateId TEXT NULL REFERENCES SpaceStates(Id);

CREATE TABLE Tags_new (
    Id TEXT NOT NULL PRIMARY KEY,
    OwnerProfileId TEXT NULL REFERENCES Profiles(Id),
    SpaceId TEXT NULL REFERENCES Spaces(Id),
    DisplayName TEXT NOT NULL CHECK (length(trim(DisplayName)) > 0),
    NormalizedName TEXT NOT NULL CHECK (length(NormalizedName) > 0),
    ParentTagId TEXT NULL REFERENCES Tags(Id),
    ColorKey TEXT NULL CHECK (ColorKey IS NULL OR length(ColorKey) BETWEEN 1 AND 16),
    Position INTEGER NOT NULL DEFAULT 0 CHECK (Position >= 0),
    Version INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL,
    CHECK ((SpaceId IS NULL) <> (OwnerProfileId IS NULL))
);
INSERT INTO Tags_new (Id, OwnerProfileId, DisplayName, NormalizedName, ParentTagId, ColorKey, Position, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
SELECT Id, OwnerProfileId, DisplayName, NormalizedName, ParentTagId, ColorKey, Position, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc FROM Tags;
DROP TABLE Tags;
ALTER TABLE Tags_new RENAME TO Tags;
CREATE UNIQUE INDEX UX_Tags_ActiveSiblingName ON Tags (COALESCE(SpaceId, OwnerProfileId), COALESCE(ParentTagId, ''), NormalizedName) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_Tags_Parent ON Tags (ParentTagId) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_Tags_OwnerParentPosition ON Tags (OwnerProfileId, ParentTagId, Position) WHERE DeletedAtUtc IS NULL;
CREATE TRIGGER TR_Tags_NoSelfParent_Insert BEFORE INSERT ON Tags WHEN NEW.ParentTagId = NEW.Id BEGIN SELECT RAISE(ABORT, 'A tag cannot be its own parent.'); END;
CREATE TRIGGER TR_Tags_NoSelfParent_Update BEFORE UPDATE OF ParentTagId ON Tags WHEN NEW.ParentTagId = NEW.Id BEGIN SELECT RAISE(ABORT, 'A tag cannot be its own parent.'); END;
