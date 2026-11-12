-- Machine-local progress only: never credentials, issue content, or archive user data.
CREATE TABLE GitHubSyncCheckpoints (
    RepositorySubscriptionId TEXT NOT NULL REFERENCES GitHubRepositories(Id),
    Kind TEXT NOT NULL CHECK (Kind IN ('issues','comments','comment-check','initial-import-backup')),
    RepositoryVersion INTEGER NOT NULL CHECK (RepositoryVersion > 0),
    PayloadJson TEXT NOT NULL CHECK (json_valid(PayloadJson) AND length(PayloadJson) <= 65536),
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (RepositorySubscriptionId, Kind)
);
