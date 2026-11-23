-- Q13: delegation, follow-ups and nudges (PLAN3 9.11). One live delegation per task; the
-- delegator's follow-up lives in FollowUps (account stream) and names the delegation.
CREATE TABLE TaskDelegations (
    Id                TEXT NOT NULL PRIMARY KEY,
    TaskId            TEXT NOT NULL REFERENCES Tasks(Id),
    FromMemberId      TEXT NOT NULL,
    ToMemberId        TEXT NOT NULL,
    Message           TEXT NOT NULL DEFAULT '' CHECK (length(Message) <= 4000),
    DoneDefinition    TEXT NOT NULL DEFAULT '' CHECK (length(DoneDefinition) <= 4000),
    DueDate           TEXT NULL,
    ReviewRequired    INTEGER NOT NULL DEFAULT 0 CHECK (ReviewRequired IN (0,1)),
    State             INTEGER NOT NULL CHECK (State BETWEEN 0 AND 6),
    StateChangedAtUtc TEXT NOT NULL,
    Response          TEXT NULL CHECK (Response IS NULL OR length(Response) <= 4000),
    Version           INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc      TEXT NOT NULL,
    UpdatedAtUtc      TEXT NOT NULL,
    DeletedAtUtc      TEXT NULL,
    CHECK (FromMemberId <> ToMemberId)
);
CREATE UNIQUE INDEX UX_TaskDelegations_ActiveTask ON TaskDelegations (TaskId) WHERE DeletedAtUtc IS NULL AND State IN (0,1,2);
CREATE INDEX IX_TaskDelegations_From ON TaskDelegations (FromMemberId, State) WHERE DeletedAtUtc IS NULL;
CREATE INDEX IX_TaskDelegations_To ON TaskDelegations (ToMemberId, State) WHERE DeletedAtUtc IS NULL;

ALTER TABLE FollowUps ADD COLUMN DelegationId TEXT NULL REFERENCES TaskDelegations(Id);
CREATE INDEX IX_FollowUps_Delegation ON FollowUps (DelegationId) WHERE DelegationId IS NOT NULL;

-- A nudge is a record, never edited: who nudged whom about which delegation, with the text sent.
CREATE TABLE Nudges (
    Id           TEXT NOT NULL PRIMARY KEY,
    TaskId       TEXT NOT NULL REFERENCES Tasks(Id),
    DelegationId TEXT NOT NULL REFERENCES TaskDelegations(Id),
    FromMemberId TEXT NOT NULL,
    ToMemberId   TEXT NOT NULL,
    Text         TEXT NOT NULL CHECK (length(Text) <= 2000),
    SentAtUtc    TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
CREATE INDEX IX_Nudges_Delegation ON Nudges (DelegationId, SentAtUtc);
