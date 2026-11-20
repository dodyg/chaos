using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chaos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class S007_AttachmentBlobBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AttachmentBlobBindings",
                columns: table => new
                {
                    StreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttachmentId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    CustodianAccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttachmentBlobBindings", x => new { x.StreamId, x.AttachmentId });
                    table.CheckConstraint("CK_AttachmentBlobBindings_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_AttachmentBlobBindings_Accounts_CustodianAccountId",
                        column: x => x.CustodianAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AttachmentBlobBindings_SyncStreams_StreamId",
                        column: x => x.StreamId,
                        principalTable: "SyncStreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttachmentBlobBindings_Custodian",
                table: "AttachmentBlobBindings",
                columns: new[] { "CustodianAccountId", "Sha256" });

            // Existing space-stream attachments: the custodian is the account whose latest logged change wrote the
            // current hash. Nothing else is trusted; an attachment whose change was pruned stays unbound and unreadable
            // to other members until its file group is written again.
            migrationBuilder.Sql("""
                INSERT INTO "AttachmentBlobBindings" ("StreamId", "AttachmentId", "Sha256", "CustodianAccountId")
                SELECT e."StreamId", e."EntityKey", e."Groups" -> 'file' -> 'values' ->> 'Sha256', writer."Id"
                FROM "SyncEntities" e
                JOIN "SyncStreams" s ON s."Id" = e."StreamId" AND s."Kind" = 2
                JOIN LATERAL (
                    SELECT c."ActorId" FROM "SyncChanges" c
                    WHERE c."StreamId" = e."StreamId" AND c."Kind" = 'attachment' AND c."EntityKey" = e."EntityKey"
                      AND c."Op" = 'upsert' AND c."Groups" ? 'file'
                      AND c."Groups" -> 'file' -> 'values' ->> 'Sha256' = e."Groups" -> 'file' -> 'values' ->> 'Sha256'
                    ORDER BY c."Seq" DESC LIMIT 1
                ) latest ON TRUE
                JOIN "Accounts" writer ON writer."ProfileId" = latest."ActorId"
                WHERE e."Kind" = 'attachment' AND e."PurgedAt" IS NULL AND e."MovedTo" IS NULL
                  AND e."Groups" -> 'file' -> 'values' ->> 'Sha256' ~ '^[0-9a-f]{64}$';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttachmentBlobBindings");
        }
    }
}
