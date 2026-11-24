-- Q15: GitHub in shared spaces (PLAN3 §10). Linked pull requests are kept per issue link, so views and the task can
-- show them; the runner refreshes them from GitHub. A comment posted for another member records the exact attribution
-- line it was sent with, so reading the comment back never copies that line into the diary. Assigning a member with a
-- GitHub login changes TaskAssignees, which must mark the link dirty on the runner like a tag change does.
CREATE TABLE GitHubPullRequestLinks (
    IssueLinkId        TEXT NOT NULL REFERENCES GitHubIssueLinks(Id),
    PullRequestId      TEXT NOT NULL,
    RepositoryFullName TEXT NOT NULL,
    Number             INTEGER NOT NULL CHECK (Number > 0),
    Title              TEXT NOT NULL,
    State              INTEGER NOT NULL CHECK (State BETWEEN 0 AND 3),
    ReviewState        INTEGER NOT NULL CHECK (ReviewState BETWEEN 0 AND 3),
    Url                TEXT NOT NULL,
    RefreshedAtUtc     TEXT NOT NULL,
    PRIMARY KEY (IssueLinkId, PullRequestId)
);
CREATE INDEX IX_GitHubPullRequestLinks_Link ON GitHubPullRequestLinks(IssueLinkId);
ALTER TABLE GitHubCommentLinks ADD COLUMN Attribution TEXT NULL;
CREATE TRIGGER GitHubDirtyAssigneeInsert AFTER INSERT ON TaskAssignees BEGIN UPDATE GitHubIssueLinks SET LocalDirty = 1, LocalDirtySinceUtc = COALESCE(LocalDirtySinceUtc, strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z') WHERE TaskId = NEW.TaskId AND State = 0; END;
CREATE TRIGGER GitHubDirtyAssigneeUpdate AFTER UPDATE OF DeletedAtUtc ON TaskAssignees BEGIN UPDATE GitHubIssueLinks SET LocalDirty = 1, LocalDirtySinceUtc = COALESCE(LocalDirtySinceUtc, strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z') WHERE TaskId = NEW.TaskId AND State = 0; END;
CREATE TRIGGER GitHubDirtyAssigneeDelete AFTER DELETE ON TaskAssignees BEGIN UPDATE GitHubIssueLinks SET LocalDirty = 1, LocalDirtySinceUtc = COALESCE(LocalDirtySinceUtc, strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z') WHERE TaskId = OLD.TaskId AND State = 0; END;
-- Every member's device receives the connection an owner made, and ProfileId names that owner, who has no Profiles row
-- here. The column stays; only the foreign key goes.
CREATE TABLE GitHubConnections_new (
    Id TEXT PRIMARY KEY NOT NULL,
    SpaceId TEXT NOT NULL REFERENCES Spaces(Id),
    ProfileId TEXT NOT NULL,
    WebBaseUrl TEXT NOT NULL CHECK (WebBaseUrl = 'https://github.com'),
    ApiBaseUrl TEXT NOT NULL CHECK (ApiBaseUrl = 'https://api.github.com'),
    AccountId INTEGER NOT NULL CHECK (AccountId > 0),
    AccountLogin TEXT NOT NULL,
    State INTEGER NOT NULL DEFAULT 0 CHECK (State BETWEEN 0 AND 2),
    PostDiaryByDefault INTEGER NOT NULL DEFAULT 1 CHECK (PostDiaryByDefault IN (0,1)),
    CreateLabelsForTags INTEGER NOT NULL DEFAULT 0 CHECK (CreateLabelsForTags IN (0,1)),
    RateLimitResetUtc TEXT,
    LastSyncStartedUtc TEXT,
    LastSyncCompletedUtc TEXT,
    LastErrorKind INTEGER,
    LastErrorMessage TEXT,
    Version INTEGER NOT NULL DEFAULT 1 CHECK (Version > 0),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT,
    RunnerDeviceId TEXT NULL,
    RunnerDeviceName TEXT NULL
);
INSERT INTO GitHubConnections_new SELECT * FROM GitHubConnections;
DROP TABLE GitHubConnections;
ALTER TABLE GitHubConnections_new RENAME TO GitHubConnections;
CREATE UNIQUE INDEX UX_GitHubConnections_ActivePerSpace ON GitHubConnections(SpaceId) WHERE DeletedAtUtc IS NULL;
