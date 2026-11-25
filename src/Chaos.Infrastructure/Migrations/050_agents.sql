-- Q17 agents (PLAN3 12.3, 12.4, 12.6): the claim and agent groups of a task, local agents and the lists they may use,
-- task proposals (written from Q20 on by propose_tasks), the actor on undo entries, and an activity log whose Source
-- gains 'agent' and whose row names its actor (the per-agent hourly counters read it).

ALTER TABLE Tasks ADD COLUMN ClaimedById TEXT NULL;
ALTER TABLE Tasks ADD COLUMN ClaimedAtUtc TEXT NULL;
ALTER TABLE Tasks ADD COLUMN ClaimExpiresAtUtc TEXT NULL;
ALTER TABLE Tasks ADD COLUMN AgentReady INTEGER NOT NULL DEFAULT 0 CHECK (AgentReady IN (0,1));
ALTER TABLE Tasks ADD COLUMN AgentAssigneeId TEXT NULL;
CREATE INDEX IX_Tasks_LiveClaim ON Tasks (ClaimExpiresAtUtc) WHERE ClaimedById IS NOT NULL AND DeletedAtUtc IS NULL;
CREATE INDEX IX_Tasks_ReadyForAgent ON Tasks (ContainerId) WHERE AgentReady = 1 AND DeletedAtUtc IS NULL;

-- The name and pause state travel in the account stream; tokens (LocalAgentTokens) never leave the device.
CREATE TABLE LocalAgents (
    Id           TEXT NOT NULL PRIMARY KEY,
    Name         TEXT NOT NULL CHECK (length(Name) BETWEEN 1 AND 100),
    PausedAtUtc  TEXT NULL,
    DeletedAtUtc TEXT NULL,
    Version      INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);

-- Access to the Personal space is off until the person ticks a list: one row per (agent, list). No foreign keys, as
-- for other synced references: a revoked grant stays as a tombstone, and must not stop the list or the agent from being
-- purged, nor a grant arriving by sync before its list.
CREATE TABLE LocalAgentAccess (
    AgentId      TEXT NOT NULL,
    ContainerId  TEXT NOT NULL,
    GrantedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT NULL,
    Version      INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (AgentId, ContainerId)
);

-- Device-only. No foreign key: a removed agent's revoked tokens must not stop its row from being purged; a token is
-- honoured only while its agent is live (checked where tokens are used).
CREATE TABLE LocalAgentTokens (
    Id            TEXT NOT NULL PRIMARY KEY,
    AgentId       TEXT NOT NULL,
    Name          TEXT NOT NULL CHECK (length(Name) BETWEEN 1 AND 100),
    TokenHash     TEXT NOT NULL CHECK (length(TokenHash) = 64),
    CreatedAtUtc  TEXT NOT NULL,
    LastUsedAtUtc TEXT NULL,
    RevokedAtUtc  TEXT NULL
);
CREATE INDEX IX_LocalAgentTokens_Agent ON LocalAgentTokens (AgentId) WHERE RevokedAtUtc IS NULL;

-- State: 0 pending, 1 accepted, 2 dismissed.
CREATE TABLE TaskProposals (
    Id              TEXT NOT NULL PRIMARY KEY,
    SpaceId         TEXT NOT NULL,
    AgentId         TEXT NOT NULL,
    SourcePadItemId TEXT NULL,
    Title           TEXT NOT NULL CHECK (length(Title) BETWEEN 1 AND 500),
    NotesMarkdown   TEXT NOT NULL DEFAULT '',
    QuickAddText    TEXT NULL,
    State           INTEGER NOT NULL DEFAULT 0 CHECK (State BETWEEN 0 AND 2),
    CreatedTaskId   TEXT NULL,
    DecidedAtUtc    TEXT NULL,
    Version         INTEGER NOT NULL CHECK (Version >= 1),
    CreatedAtUtc    TEXT NOT NULL,
    UpdatedAtUtc    TEXT NOT NULL,
    DeletedAtUtc    TEXT NULL
);
CREATE INDEX IX_TaskProposals_SpaceState ON TaskProposals (SpaceId, State) WHERE DeletedAtUtc IS NULL;

ALTER TABLE UndoEntries ADD COLUMN ActorId TEXT NULL;

-- ActivityLog rebuild (030): 'agent' joins the Source CHECK and the row names its actor. Both tables go: the entities
-- reference the log. Seq values are kept, and AUTOINCREMENT continues from the old counter, so a reader's cursor stays valid.
CREATE TABLE ActivityLog_new (
    Seq            INTEGER PRIMARY KEY AUTOINCREMENT,
    OperationId    TEXT NOT NULL,
    OccurredAtUtc  TEXT NOT NULL,
    Source         TEXT NOT NULL CHECK (Source IN ('user','schedule','sync','import','undo','system','agent')),
    Action         TEXT NOT NULL,
    Outcome        TEXT NOT NULL CHECK (Outcome IN ('ok','failed')),
    JobRunId       TEXT NULL,
    ActorId        TEXT NULL,
    DetailJson     TEXT NULL,
    DetailVersion  INTEGER NOT NULL DEFAULT 1
);
INSERT INTO ActivityLog_new (Seq, OperationId, OccurredAtUtc, Source, Action, Outcome, JobRunId, ActorId, DetailJson, DetailVersion)
SELECT Seq, OperationId, OccurredAtUtc, Source, Action, Outcome, JobRunId, NULL, DetailJson, DetailVersion FROM ActivityLog;

CREATE TEMP TABLE ActivityLogSequence AS SELECT seq FROM sqlite_sequence WHERE name = 'ActivityLog';

CREATE TABLE ActivityLogEntities_new (
    Seq        INTEGER NOT NULL REFERENCES ActivityLog_new(Seq) ON DELETE CASCADE,
    Kind       TEXT NOT NULL,
    EntityKey  TEXT NOT NULL,
    Op         TEXT NOT NULL CHECK (Op IN ('upsert','delete')),
    Version    INTEGER NULL,
    PRIMARY KEY (Seq, Kind, EntityKey)
);
INSERT INTO ActivityLogEntities_new (Seq, Kind, EntityKey, Op, Version)
SELECT Seq, Kind, EntityKey, Op, Version FROM ActivityLogEntities;

DROP TABLE ActivityLogEntities;
DROP TABLE ActivityLog;
ALTER TABLE ActivityLog_new RENAME TO ActivityLog;
ALTER TABLE ActivityLogEntities_new RENAME TO ActivityLogEntities;
CREATE INDEX IX_ActivityLog_OccurredAt ON ActivityLog (OccurredAtUtc);
CREATE INDEX IX_ActivityLog_ActorAction ON ActivityLog (ActorId, OccurredAtUtc) WHERE Source = 'agent';

-- The counter: the copied rows carry the old maximum, but a pruned log may have counted higher still, and a log pruned
-- empty copies no row at all, so the new table would have no sqlite_sequence row and Seq would start over at 1.
-- One row, holding the larger of the two.
CREATE TEMP TABLE ActivityLogCounter AS
SELECT MAX(COALESCE((SELECT seq FROM ActivityLogSequence), 0),
           COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'ActivityLog'), 0),
           COALESCE((SELECT MAX(Seq) FROM ActivityLog), 0)) AS seq;
DELETE FROM sqlite_sequence WHERE name = 'ActivityLog';
INSERT INTO sqlite_sequence (name, seq) SELECT 'ActivityLog', seq FROM ActivityLogCounter WHERE seq > 0;
DROP TABLE ActivityLogCounter;
DROP TABLE ActivityLogSequence;
