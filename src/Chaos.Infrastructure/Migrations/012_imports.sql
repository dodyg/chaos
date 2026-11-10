-- Import bookkeeping (PLAN §14, work package 12).

-- One row per committed import. SourceKind 0 = Chaos JSON archive (merge), 1 = Org subset,
-- 2 = CSV. FileFingerprint is the SHA-256 of the imported bytes; BackupFileName names the
-- validated backup taken just before the import, which is how a bad import is undone.
CREATE TABLE ImportBatches (
    Id              TEXT NOT NULL PRIMARY KEY,
    ProfileId       TEXT NOT NULL REFERENCES Profiles(Id),
    SourceKind      INTEGER NOT NULL CHECK (SourceKind IN (0,1,2)),
    SourceName      TEXT NOT NULL,
    FileFingerprint TEXT NOT NULL CHECK (length(FileFingerprint) = 64),
    ImportedAtUtc   TEXT NOT NULL,
    CreatedCount    INTEGER NOT NULL CHECK (CreatedCount >= 0),
    SkippedCount    INTEGER NOT NULL CHECK (SkippedCount >= 0),
    BackupFileName  TEXT NULL
);
CREATE INDEX IX_ImportBatches_Imported ON ImportBatches (ImportedAtUtc DESC);

-- One row per imported top-level task. ItemFingerprint identifies the source item (its
-- stable source ID when the format has one, otherwise its normalized content), so importing
-- the same item again finds the task it already created instead of duplicating it.
CREATE TABLE ImportedItems (
    ItemFingerprint TEXT NOT NULL PRIMARY KEY CHECK (length(ItemFingerprint) = 64),
    BatchId         TEXT NOT NULL REFERENCES ImportBatches(Id),
    TaskId          TEXT NOT NULL REFERENCES Tasks(Id),
    SourceKey       TEXT NOT NULL
);
CREATE INDEX IX_ImportedItems_Task ON ImportedItems (TaskId);
CREATE INDEX IX_ImportedItems_Batch ON ImportedItems (BatchId);
