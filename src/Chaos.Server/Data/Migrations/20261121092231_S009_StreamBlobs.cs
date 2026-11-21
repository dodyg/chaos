using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chaos.Server.Data.Migrations;

/// <inheritdoc />
public partial class S009_StreamBlobs : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE "Blobs" DROP CONSTRAINT "FK_Blobs_Accounts_AccountId";
            ALTER TABLE "Blobs" DROP CONSTRAINT "PK_Blobs";
            ALTER TABLE "Blobs" ADD COLUMN "StreamId" uuid NULL;
            ALTER TABLE "Blobs" ADD COLUMN "ObjectId" uuid NULL;
            UPDATE "Blobs" SET "StreamId" = "AccountId", "ObjectId" = gen_random_uuid();

            CREATE TABLE "BlobObjects" (
                "Id" uuid NOT NULL PRIMARY KEY,
                "AccountId" uuid NOT NULL,
                "ByteSize" bigint NOT NULL,
                "State" text NOT NULL,
                "StorageKey" text NOT NULL,
                CONSTRAINT "CK_BlobObjects_ByteSize" CHECK ("ByteSize" >= 0),
                CONSTRAINT "CK_BlobObjects_State" CHECK ("State" IN ('Clean', 'Blocked'))
            );
            INSERT INTO "BlobObjects" ("Id", "AccountId", "ByteSize", "State", "StorageKey")
            SELECT "ObjectId", "AccountId", "ByteSize", "State", "StorageKey" FROM "Blobs";

            -- A space reference may only be backfilled from the custodian that S007 recorded.
            INSERT INTO "Blobs" ("StreamId", "AccountId", "ObjectId", "Sha256", "ByteSize", "State", "Detail", "StorageKey", "WrappedKey", "CreatedAt", "UnreferencedSince")
            SELECT DISTINCT ON (binding."StreamId", binding."Sha256")
                binding."StreamId", blob."AccountId", blob."ObjectId", blob."Sha256", blob."ByteSize", blob."State",
                blob."Detail", blob."StorageKey", blob."WrappedKey", blob."CreatedAt", NULL
            FROM "AttachmentBlobBindings" binding
            JOIN "Blobs" blob ON blob."StreamId" = binding."CustodianAccountId" AND blob."Sha256" = binding."Sha256"
            JOIN "SyncEntities" entity ON entity."StreamId" = binding."StreamId" AND entity."Kind" = 'attachment' AND entity."EntityKey" = binding."AttachmentId"
            WHERE entity."PurgedAt" IS NULL AND entity."MovedTo" IS NULL
              AND entity."Groups" -> 'deleted' -> 'values' ->> 'DeletedAtUtc' IS NULL
              AND entity."Groups" -> 'file' -> 'values' ->> 'Sha256' = binding."Sha256"
            ORDER BY binding."StreamId", binding."Sha256", CASE WHEN blob."State" = 'Blocked' THEN 0 ELSE 1 END, blob."AccountId";

            ALTER TABLE "Blobs" ALTER COLUMN "StreamId" SET NOT NULL;
            ALTER TABLE "Blobs" ALTER COLUMN "ObjectId" SET NOT NULL;
            ALTER TABLE "Blobs" ADD CONSTRAINT "PK_Blobs" PRIMARY KEY ("StreamId", "Sha256");
            CREATE INDEX "IX_Blobs_ObjectId" ON "Blobs" ("ObjectId");
            CREATE INDEX "IX_BlobObjects_AccountId" ON "BlobObjects" ("AccountId");
            ALTER TABLE "Blobs" ADD CONSTRAINT "FK_Blobs_BlobObjects_ObjectId" FOREIGN KEY ("ObjectId") REFERENCES "BlobObjects" ("Id") ON DELETE RESTRICT;
            ALTER TABLE "Blobs" ADD CONSTRAINT "FK_Blobs_SyncStreams_StreamId" FOREIGN KEY ("StreamId") REFERENCES "SyncStreams" ("Id") ON DELETE CASCADE;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
                IF EXISTS (SELECT 1 FROM "Blobs" WHERE "StreamId" <> "AccountId")
                    OR EXISTS (SELECT 1 FROM "Blobs" b WHERE NOT EXISTS (SELECT 1 FROM "Accounts" a WHERE a."Id" = b."AccountId")) THEN
                    RAISE EXCEPTION 'Cannot downgrade while shared blob references or deleted uploaders exist';
                END IF;
            END $$;
            ALTER TABLE "Blobs" DROP CONSTRAINT "FK_Blobs_BlobObjects_ObjectId";
            ALTER TABLE "Blobs" DROP CONSTRAINT "FK_Blobs_SyncStreams_StreamId";
            ALTER TABLE "Blobs" DROP CONSTRAINT "PK_Blobs";
            DROP INDEX "IX_Blobs_ObjectId";
            DROP TABLE "BlobObjects";
            ALTER TABLE "Blobs" DROP COLUMN "StreamId";
            ALTER TABLE "Blobs" DROP COLUMN "ObjectId";
            ALTER TABLE "Blobs" ADD CONSTRAINT "PK_Blobs" PRIMARY KEY ("AccountId", "Sha256");
            ALTER TABLE "Blobs" ADD CONSTRAINT "FK_Blobs_Accounts_AccountId" FOREIGN KEY ("AccountId") REFERENCES "Accounts" ("Id") ON DELETE CASCADE;
            """);
    }
}
