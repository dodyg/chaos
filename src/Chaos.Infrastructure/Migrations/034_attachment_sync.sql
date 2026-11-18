-- Chaos migration 034 (PLAN2 P16): moving attachment bytes to and from the online service. 034 was
-- reserved for P17, which takes the next free number when it lands.

-- Device-local transfer state per hash (the row already says whether this computer holds the bytes).
-- Remote: 0 not known to be on the service, 1 on the service, 2 blocked by the malware check,
-- 3 upload failed, 4 over quota. NextTryUtc backs a failed or refused transfer off; Failures counts them.
ALTER TABLE AttachmentLocalState ADD COLUMN Remote INTEGER NOT NULL DEFAULT 0 CHECK (Remote IN (0,1,2,3,4));
ALTER TABLE AttachmentLocalState ADD COLUMN NextTryUtc TEXT NULL;
ALTER TABLE AttachmentLocalState ADD COLUMN Failures INTEGER NOT NULL DEFAULT 0 CHECK (Failures >= 0);

-- Lists and projects the person marked "Keep available offline" on this computer. Device-local: it
-- never syncs, so each computer decides how much to keep.
CREATE TABLE OfflineContainers (
    ContainerId  TEXT NOT NULL PRIMARY KEY REFERENCES Containers(Id) ON DELETE CASCADE,
    UpdatedAtUtc TEXT NOT NULL
);
