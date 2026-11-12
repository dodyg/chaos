-- Extend device-local progress kinds without changing applied migration checksums.
ALTER TABLE GitHubSyncCheckpoints RENAME TO GitHubSyncCheckpoints_PreRefresh;
CREATE TABLE GitHubSyncCheckpoints (
    RepositorySubscriptionId TEXT NOT NULL REFERENCES GitHubRepositories(Id),
    Kind TEXT NOT NULL CHECK (Kind IN ('issues','comments','comment-check','initial-import-backup','labels','milestones','reconcile')),
    RepositoryVersion INTEGER NOT NULL CHECK (RepositoryVersion > 0),
    PayloadJson TEXT NOT NULL CHECK (json_valid(PayloadJson) AND length(PayloadJson) <= 65536),
    UpdatedAtUtc TEXT NOT NULL,
    PRIMARY KEY (RepositorySubscriptionId, Kind)
);
INSERT INTO GitHubSyncCheckpoints SELECT * FROM GitHubSyncCheckpoints_PreRefresh;
DROP TABLE GitHubSyncCheckpoints_PreRefresh;

-- Any local placement change relinquishes automatic placement. Inbound moves explicitly
-- restore the already-read managed flag in the same transaction after updating the task.
CREATE TRIGGER GitHub_TaskPlacementMoved AFTER UPDATE OF ContainerId,SectionId ON Tasks WHEN OLD.ContainerId IS NOT NEW.ContainerId OR OLD.SectionId IS NOT NEW.SectionId BEGIN UPDATE GitHubIssueLinks SET PlacementManaged=0 WHERE TaskId=NEW.Id AND State<>4; END;
