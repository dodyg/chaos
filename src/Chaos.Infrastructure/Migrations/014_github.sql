-- Credentials belong to GitHub CLI; no credential column is permitted here.
CREATE TABLE GitHubConnections (
    Id TEXT PRIMARY KEY NOT NULL,
    SpaceId TEXT NOT NULL REFERENCES Spaces(Id),
    ProfileId TEXT NOT NULL REFERENCES Profiles(Id),
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
    DeletedAtUtc TEXT
);
CREATE UNIQUE INDEX UX_GitHubConnections_ActivePerSpace ON GitHubConnections(SpaceId) WHERE DeletedAtUtc IS NULL;

CREATE TABLE GitHubRepositories (
    Id TEXT PRIMARY KEY NOT NULL,
    ConnectionId TEXT NOT NULL REFERENCES GitHubConnections(Id),
    RepositoryId INTEGER NOT NULL CHECK (RepositoryId > 0),
    Owner TEXT NOT NULL,
    Name TEXT NOT NULL,
    FullName TEXT NOT NULL,
    HtmlUrl TEXT NOT NULL,
    IsPrivate INTEGER NOT NULL CHECK (IsPrivate IN (0,1)),
    TargetContainerId TEXT NOT NULL REFERENCES Containers(Id),
    TargetSectionId TEXT,
    FilterJson TEXT NOT NULL CHECK (json_valid(FilterJson)),
    MilestonesAsProjects INTEGER NOT NULL DEFAULT 0 CHECK (MilestonesAsProjects IN (0,1)),
    State INTEGER NOT NULL DEFAULT 0 CHECK (State BETWEEN 0 AND 2),
    IssuesCursorUtc TEXT,
    CommentsCursorUtc TEXT,
    IssuesEtag TEXT,
    CommentsEtag TEXT,
    LabelsEtag TEXT,
    MilestonesEtag TEXT,
    InitialImportDoneUtc TEXT,
    LastSyncedUtc TEXT,
    LastErrorKind INTEGER,
    LastErrorMessage TEXT,
    Version INTEGER NOT NULL DEFAULT 1 CHECK (Version > 0),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    DeletedAtUtc TEXT,
    UNIQUE (Id, ConnectionId),
    FOREIGN KEY (TargetSectionId, TargetContainerId) REFERENCES Sections(Id, ContainerId)
);
CREATE UNIQUE INDEX UX_GitHubRepositories_ActiveRepository ON GitHubRepositories(ConnectionId, RepositoryId) WHERE DeletedAtUtc IS NULL;

CREATE TABLE GitHubIssueLinks (
    Id TEXT PRIMARY KEY NOT NULL,
    RepositorySubscriptionId TEXT NOT NULL,
    ConnectionId TEXT NOT NULL REFERENCES GitHubConnections(Id),
    TaskId TEXT REFERENCES Tasks(Id),
    IssueId INTEGER NOT NULL CHECK (IssueId > 0),
    IssueNodeId TEXT NOT NULL,
    IssueNumber INTEGER NOT NULL CHECK (IssueNumber > 0),
    HtmlUrl TEXT NOT NULL,
    State INTEGER NOT NULL DEFAULT 0 CHECK (State BETWEEN 0 AND 4),
    BaseJson TEXT NOT NULL CHECK (json_valid(BaseJson)),
    BaseRemoteUpdatedUtc TEXT NOT NULL,
    IssueEtag TEXT,
    LocalAssigneeIntent INTEGER CHECK (LocalAssigneeIntent IN (0,1)),
    PlacementManaged INTEGER NOT NULL DEFAULT 1 CHECK (PlacementManaged IN (0,1)),
    LocalDirty INTEGER NOT NULL DEFAULT 0 CHECK (LocalDirty IN (0,1)),
    LocalDirtySinceUtc TEXT,
    LastSyncedUtc TEXT,
    ConflictNoticeUtc TEXT,
    LastErrorKind INTEGER,
    LastErrorMessage TEXT,
    ErrorAttempts INTEGER NOT NULL DEFAULT 0 CHECK (ErrorAttempts >= 0),
    NextAttemptUtc TEXT,
    Version INTEGER NOT NULL DEFAULT 1 CHECK (Version > 0),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,
    UNIQUE (ConnectionId, IssueId),
    FOREIGN KEY (RepositorySubscriptionId, ConnectionId) REFERENCES GitHubRepositories(Id, ConnectionId)
);
CREATE UNIQUE INDEX UX_GitHubIssueLinks_Task ON GitHubIssueLinks(TaskId) WHERE TaskId IS NOT NULL;
CREATE INDEX IX_GitHubIssueLinks_RepositoryState ON GitHubIssueLinks(RepositorySubscriptionId, State);
CREATE INDEX IX_GitHubIssueLinks_Dirty ON GitHubIssueLinks(LocalDirty) WHERE LocalDirty = 1;
CREATE INDEX IX_GitHubIssueLinks_Retry ON GitHubIssueLinks(NextAttemptUtc) WHERE NextAttemptUtc IS NOT NULL;

CREATE TABLE GitHubChecklistItems (
    SubtaskId TEXT PRIMARY KEY NOT NULL REFERENCES Subtasks(Id),
    IssueLinkId TEXT NOT NULL REFERENCES GitHubIssueLinks(Id),
    BaseOrdinal INTEGER NOT NULL CHECK (BaseOrdinal >= 0),
    BaseText TEXT NOT NULL,
    BaseChecked INTEGER NOT NULL CHECK (BaseChecked IN (0,1))
);
CREATE INDEX IX_GitHubChecklistItems_Link ON GitHubChecklistItems(IssueLinkId);

CREATE TABLE GitHubCommentLinks (
    DiaryEntryId TEXT PRIMARY KEY NOT NULL REFERENCES DiaryEntries(Id),
    IssueLinkId TEXT NOT NULL REFERENCES GitHubIssueLinks(Id),
    CommentId INTEGER CHECK (CommentId > 0),
    AuthorLogin TEXT,
    HtmlUrl TEXT,
    Origin INTEGER NOT NULL CHECK (Origin IN (0,1)),
    State INTEGER NOT NULL CHECK (State BETWEEN 0 AND 5),
    BaseBody TEXT,
    BaseRemoteUpdatedUtc TEXT,
    LastErrorKind INTEGER,
    LastErrorMessage TEXT,
    ErrorAttempts INTEGER NOT NULL DEFAULT 0 CHECK (ErrorAttempts >= 0),
    NextAttemptUtc TEXT
);
CREATE UNIQUE INDEX UX_GitHubCommentLinks_Comment ON GitHubCommentLinks(IssueLinkId, CommentId) WHERE CommentId IS NOT NULL;

CREATE TABLE GitHubLabelTags (
    RepositorySubscriptionId TEXT NOT NULL REFERENCES GitHubRepositories(Id),
    LabelId INTEGER NOT NULL CHECK (LabelId > 0),
    LabelName TEXT NOT NULL,
    LabelColor TEXT NOT NULL,
    TagId TEXT NOT NULL REFERENCES Tags(Id),
    PRIMARY KEY (RepositorySubscriptionId, LabelId)
);

CREATE TABLE GitHubMilestoneProjects (
    RepositorySubscriptionId TEXT NOT NULL REFERENCES GitHubRepositories(Id),
    MilestoneNumber INTEGER NOT NULL CHECK (MilestoneNumber > 0),
    MilestoneId INTEGER NOT NULL CHECK (MilestoneId > 0),
    ContainerId TEXT NOT NULL REFERENCES Containers(Id),
    BaseTitle TEXT NOT NULL,
    BaseDueOn TEXT,
    PRIMARY KEY (RepositorySubscriptionId, MilestoneNumber)
);

CREATE TABLE GitHubSyncRuns (
    Id TEXT PRIMARY KEY NOT NULL,
    ConnectionId TEXT NOT NULL REFERENCES GitHubConnections(Id),
    StartedAtUtc TEXT NOT NULL,
    FinishedAtUtc TEXT,
    RequestCount INTEGER NOT NULL DEFAULT 0 CHECK (RequestCount >= 0),
    NotModifiedCount INTEGER NOT NULL DEFAULT 0 CHECK (NotModifiedCount >= 0),
    CreatedCount INTEGER NOT NULL DEFAULT 0 CHECK (CreatedCount >= 0),
    UpdatedCount INTEGER NOT NULL DEFAULT 0 CHECK (UpdatedCount >= 0),
    PushedCount INTEGER NOT NULL DEFAULT 0 CHECK (PushedCount >= 0),
    ConflictCount INTEGER NOT NULL DEFAULT 0 CHECK (ConflictCount >= 0),
    ErrorKind INTEGER
);
CREATE INDEX IX_GitHubSyncRuns_ConnectionStarted ON GitHubSyncRuns(ConnectionId, StartedAtUtc);

CREATE TRIGGER GitHubDirtyTask AFTER UPDATE OF Title, Notes, Status, DeletedAtUtc ON Tasks BEGIN UPDATE GitHubIssueLinks SET LocalDirty = 1, LocalDirtySinceUtc = COALESCE(LocalDirtySinceUtc, NEW.UpdatedAtUtc) WHERE TaskId = NEW.Id AND State = 0; END;
CREATE TRIGGER GitHubDirtySubtaskInsert AFTER INSERT ON Subtasks BEGIN UPDATE GitHubIssueLinks SET LocalDirty = 1, LocalDirtySinceUtc = COALESCE(LocalDirtySinceUtc, NEW.UpdatedAtUtc) WHERE TaskId = NEW.TaskId AND State = 0; END;
CREATE TRIGGER GitHubDirtySubtaskUpdate AFTER UPDATE OF Title, Status, DeletedAtUtc ON Subtasks BEGIN UPDATE GitHubIssueLinks SET LocalDirty = 1, LocalDirtySinceUtc = COALESCE(LocalDirtySinceUtc, NEW.UpdatedAtUtc) WHERE TaskId = NEW.TaskId AND State = 0; END;
CREATE TRIGGER GitHubDirtyTagInsert AFTER INSERT ON TaskTags BEGIN UPDATE GitHubIssueLinks SET LocalDirty = 1, LocalDirtySinceUtc = COALESCE(LocalDirtySinceUtc, strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z') WHERE TaskId = NEW.TaskId AND State = 0; END;
CREATE TRIGGER GitHubDirtyTagDelete AFTER DELETE ON TaskTags BEGIN UPDATE GitHubIssueLinks SET LocalDirty = 1, LocalDirtySinceUtc = COALESCE(LocalDirtySinceUtc, strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z') WHERE TaskId = OLD.TaskId AND State = 0; END;
CREATE TRIGGER GitHubDirtyCommentInsert AFTER INSERT ON GitHubCommentLinks WHEN NEW.State IN (1,2,3) BEGIN UPDATE GitHubIssueLinks SET LocalDirty = 1, LocalDirtySinceUtc = COALESCE(LocalDirtySinceUtc, strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z') WHERE Id = NEW.IssueLinkId AND State = 0; END;
CREATE TRIGGER GitHubDirtyCommentUpdate AFTER UPDATE ON GitHubCommentLinks WHEN NEW.State IN (1,2,3) BEGIN UPDATE GitHubIssueLinks SET LocalDirty = 1, LocalDirtySinceUtc = COALESCE(LocalDirtySinceUtc, strftime('%Y-%m-%dT%H:%M:%f','now') || '0000Z') WHERE Id = NEW.IssueLinkId AND State = 0; END;
CREATE TRIGGER GitHubDirtyDiary AFTER UPDATE OF RawMarkdown, DeletedAtUtc ON DiaryEntries BEGIN UPDATE GitHubCommentLinks SET State = CASE WHEN NEW.DeletedAtUtc IS NOT NULL THEN 3 WHEN State = 1 THEN 1 ELSE 2 END WHERE DiaryEntryId = NEW.Id AND State IN (0,1,2) AND EXISTS (SELECT 1 FROM GitHubIssueLinks l WHERE l.Id = IssueLinkId AND l.State = 0); END;
