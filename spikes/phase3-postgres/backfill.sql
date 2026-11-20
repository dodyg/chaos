BEGIN;
CREATE TABLE "SyncStreams" (
 "Id" uuid PRIMARY KEY, "Kind" smallint NOT NULL, "AccountId" uuid REFERENCES "Accounts" ("Id"),
 "SpaceId" uuid, "NextSeq" bigint NOT NULL, "ChangesPrunedThroughSeq" bigint NOT NULL,
 "CreatedAt" timestamptz NOT NULL, "DeletedAt" timestamptz, "PurgeAfter" timestamptz,
 CHECK (("Kind" = 1 AND "AccountId" IS NOT NULL AND "AccountId" = "Id" AND "SpaceId" IS NULL) OR
        ("Kind" = 2 AND "SpaceId" IS NOT NULL AND "SpaceId" = "Id" AND "AccountId" IS NULL))
);
INSERT INTO "SyncStreams" ("Id", "Kind", "AccountId", "NextSeq", "ChangesPrunedThroughSeq", "CreatedAt")
 SELECT "Id", 1, "Id", "NextSeq", "ChangesPrunedThroughSeq", "CreatedAt" FROM "Accounts";
ALTER TABLE "SyncEntities" ADD COLUMN "StreamId" uuid, ADD COLUMN "MovedTo" uuid;
UPDATE "SyncEntities" SET "StreamId" = "AccountId";
ALTER TABLE "SyncEntities" ALTER COLUMN "StreamId" SET NOT NULL,
 DROP CONSTRAINT "PK_SyncEntities", DROP CONSTRAINT "FK_SyncEntities_Accounts_AccountId", DROP COLUMN "AccountId",
 ADD PRIMARY KEY ("StreamId", "Kind", "EntityKey"), ADD FOREIGN KEY ("StreamId") REFERENCES "SyncStreams" ("Id") ON DELETE CASCADE;
CREATE INDEX ON "SyncEntities" ("Kind", "EntityKey");
ALTER TABLE "SyncChanges" ADD COLUMN "StreamId" uuid, ADD COLUMN "ActorId" uuid, ADD COLUMN "PreviousGroups" jsonb;
UPDATE "SyncChanges" SET "StreamId" = "AccountId";
ALTER TABLE "SyncChanges" ALTER COLUMN "StreamId" SET NOT NULL,
 DROP CONSTRAINT "PK_SyncChanges", DROP CONSTRAINT "FK_SyncChanges_Accounts_AccountId", DROP COLUMN "AccountId",
 ADD PRIMARY KEY ("StreamId", "Seq"), ADD UNIQUE ("StreamId", "ChangeId"), ADD FOREIGN KEY ("StreamId") REFERENCES "SyncStreams" ("Id") ON DELETE CASCADE;
CREATE INDEX ON "SyncChanges" ("StreamId", "CreatedAt");
ALTER TABLE "Accounts" DROP COLUMN "NextSeq", DROP COLUMN "ChangesPrunedThroughSeq";
COMMIT;
