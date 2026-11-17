-- Chaos migration 031 (PLAN2 P9, attachments). 030 went to the activity log (P6a), so this is
-- the next free number.

-- One row per attached file. The bytes are not here: they live in the attachment file store,
-- addressed by Sha256, so identical files are stored once. OwnerKind/OwnerId point at a task (1),
-- diary entry (2), pad note (3) or page (4); the owner is a different table per kind, so there is
-- no foreign key, and deleting an owner for good removes its attachments with it (DataPurge and
-- the sync purge rules). FileName is the sanitized display name; OriginalFileName is as typed.
CREATE TABLE Attachments (
    Id               TEXT NOT NULL PRIMARY KEY,
    SpaceId          TEXT NOT NULL REFERENCES Spaces(Id),
    OwnerKind        INTEGER NOT NULL CHECK (OwnerKind IN (1,2,3,4)), -- 1=Task 2=Diary 3=Pad 4=Page
    OwnerId          TEXT NOT NULL,
    FileName         TEXT NOT NULL,
    OriginalFileName TEXT NOT NULL,
    MediaType        TEXT NOT NULL,
    ByteSize         INTEGER NOT NULL CHECK (ByteSize >= 0),
    Sha256           TEXT NOT NULL CHECK (length(Sha256) = 64),
    Width            INTEGER NULL CHECK (Width IS NULL OR Width > 0),
    Height           INTEGER NULL CHECK (Height IS NULL OR Height > 0),
    DurationMs       INTEGER NULL CHECK (DurationMs IS NULL OR DurationMs >= 0),
    Version          INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc     TEXT NOT NULL,
    UpdatedAtUtc     TEXT NOT NULL,
    DeletedAtUtc     TEXT NULL
);
CREATE INDEX IX_Attachments_Owner ON Attachments (OwnerKind, OwnerId, CreatedAtUtc) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_Attachments_Space ON Attachments (SpaceId) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_Attachments_Sha256 ON Attachments (Sha256);

-- Whether this computer holds the bytes of a hash (device-local, never exported or synced).
-- State: 0 Present, 1 Missing, 2 Downloading, 3 Failed.
CREATE TABLE AttachmentLocalState (
    Sha256       TEXT NOT NULL PRIMARY KEY CHECK (length(Sha256) = 64),
    State        INTEGER NOT NULL DEFAULT 0 CHECK (State IN (0,1,2,3)),
    PinnedReason TEXT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
