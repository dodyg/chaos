-- Q8: device-local notices and account-synced preferences for each shared space.
ALTER TABLE DiaryEntries ADD COLUMN RemovedByOwner INTEGER NOT NULL DEFAULT 0 CHECK (RemovedByOwner IN (0,1));
CREATE TABLE NotificationJobs_new (
    Id TEXT NOT NULL PRIMARY KEY, ProfileId TEXT NOT NULL REFERENCES Profiles(Id), TaskId TEXT NOT NULL REFERENCES Tasks(Id),
    SourceKind INTEGER NOT NULL CHECK (SourceKind IN (0,1,2)), SourceId TEXT NOT NULL,
    Generation INTEGER NOT NULL CHECK (Generation >= 1), Sequence INTEGER NOT NULL CHECK (Sequence >= 1),
    Reason INTEGER NOT NULL CHECK (Reason IN (0,1,2)), TargetAtUtc TEXT NOT NULL, NotBeforeUtc TEXT NULL,
    DeliverAtUtc TEXT NOT NULL, State INTEGER NOT NULL CHECK (State BETWEEN 0 AND 4), DeliveredAtUtc TEXT NULL,
    Presentation INTEGER NOT NULL DEFAULT 0 CHECK (Presentation BETWEEN 0 AND 3), AcknowledgedAtUtc TEXT NULL,
    ActionTaken TEXT NULL, OsState INTEGER NOT NULL DEFAULT 0 CHECK (OsState IN (0,1,2)), OsNotificationId TEXT NULL,
    OsAttempts INTEGER NOT NULL DEFAULT 0 CHECK (OsAttempts >= 0), OsLastAttemptUtc TEXT NULL, OsError TEXT NULL,
    CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL,
    CHECK ((State IN (1,2,3)) = (DeliveredAtUtc IS NOT NULL)), CHECK ((State = 2) = (AcknowledgedAtUtc IS NOT NULL))
);
INSERT INTO NotificationJobs_new SELECT * FROM NotificationJobs;
DROP TABLE NotificationJobs;
ALTER TABLE NotificationJobs_new RENAME TO NotificationJobs;
CREATE UNIQUE INDEX UX_NotificationJobs_Key ON NotificationJobs (SourceKind, SourceId, Generation, Sequence);
CREATE UNIQUE INDEX UX_NotificationJobs_PendingSource ON NotificationJobs (SourceKind, SourceId) WHERE State = 0;
CREATE INDEX IX_NotificationJobs_StateDeliver ON NotificationJobs (State, DeliverAtUtc);
CREATE INDEX IX_NotificationJobs_Task ON NotificationJobs (TaskId);
CREATE INDEX IX_NotificationJobs_Delivered ON NotificationJobs (ProfileId, DeliveredAtUtc) WHERE DeliveredAtUtc IS NOT NULL;
CREATE TABLE CollaborationNotices (
    Id TEXT NOT NULL PRIMARY KEY,
    Kind TEXT NOT NULL,
    SpaceId TEXT NOT NULL,
    EntityKind TEXT NOT NULL,
    EntityId TEXT NOT NULL,
    ActorId TEXT NOT NULL,
    CreatedAtUtc TEXT NOT NULL,
    NotifyAtUtc TEXT NOT NULL,
    DeliveredAtUtc TEXT NULL,
    ReadAtUtc TEXT NULL,
    UNIQUE (Kind, EntityKind, EntityId, ActorId)
);
CREATE INDEX IX_CollaborationNotices_Delivery ON CollaborationNotices (NotifyAtUtc) WHERE DeliveredAtUtc IS NULL;

CREATE TABLE SpaceNotificationSettings (
    Id TEXT NOT NULL PRIMARY KEY,
    ProfileId TEXT NOT NULL REFERENCES Profiles(Id),
    SpaceId TEXT NOT NULL,
    Assigned INTEGER NOT NULL DEFAULT 1 CHECK (Assigned IN (0,1)),
    Mentioned INTEGER NOT NULL DEFAULT 1 CHECK (Mentioned IN (0,1)),
    DueSoon INTEGER NOT NULL DEFAULT 0 CHECK (DueSoon IN (0,1)),
    Delegations INTEGER NOT NULL DEFAULT 1 CHECK (Delegations IN (0,1)),
    "Nothing" INTEGER NOT NULL DEFAULT 0 CHECK ("Nothing" IN (0,1)),
    OwnerDefaultReminder INTEGER NOT NULL DEFAULT 1 CHECK (OwnerDefaultReminder IN (0,1)),
    Version INTEGER NOT NULL DEFAULT 1 CHECK (Version >= 1),
    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL
);
