-- Per-stream remote transfer state is device-local. Local file bytes remain keyed by hash.
CREATE TABLE AttachmentStreamState (
    StreamId TEXT NOT NULL,
    Sha256 TEXT NOT NULL REFERENCES AttachmentLocalState(Sha256) ON DELETE CASCADE,
    Remote INTEGER NOT NULL DEFAULT 0 CHECK (Remote IN (0,1,2,3,4)),
    NextTryUtc TEXT NULL,
    Failures INTEGER NOT NULL DEFAULT 0 CHECK (Failures >= 0),
    PRIMARY KEY (StreamId, Sha256)
);

-- The old hash-only status may have come from a different stream. Recheck it instead of
-- assuming the account stream has bytes, and leave every shared stream unknown as well.
INSERT INTO AttachmentStreamState (StreamId, Sha256, Remote, NextTryUtc, Failures)
SELECT DISTINCT state.AccountId, local.Sha256, 0, NULL, 0
FROM AttachmentLocalState local
JOIN Attachments attachment ON attachment.Sha256 = local.Sha256 AND attachment.DeletedAtUtc IS NULL
JOIN Spaces space ON space.Id = attachment.SpaceId AND space.Kind = 1 AND space.DeletedAtUtc IS NULL
CROSS JOIN SyncState state
WHERE state.Id = 1 AND state.AccountId IS NOT NULL;
