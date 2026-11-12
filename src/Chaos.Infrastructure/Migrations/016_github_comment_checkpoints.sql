-- Per-issue machine-local progress stores only pagination metadata and bounded comment IDs.
CREATE TABLE GitHubCommentCheckpoints (
    IssueLinkId TEXT PRIMARY KEY NOT NULL REFERENCES GitHubIssueLinks(Id),
    RepositoryVersion INTEGER NOT NULL CHECK (RepositoryVersion > 0),
    PayloadJson TEXT NOT NULL CHECK (json_valid(PayloadJson) AND length(PayloadJson) <= 65536),
    UpdatedAtUtc TEXT NOT NULL
);
