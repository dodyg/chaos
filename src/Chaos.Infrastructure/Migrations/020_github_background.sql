CREATE TABLE GitHubConnectionBackoff (
    ConnectionId TEXT PRIMARY KEY NOT NULL REFERENCES GitHubConnections(Id) ON DELETE CASCADE,
    ErrorAttempts INTEGER NOT NULL CHECK (ErrorAttempts > 0),
    NextAttemptUtc TEXT NOT NULL
);
