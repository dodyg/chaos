using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chaos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class S005_Streams : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncChanges_Accounts_AccountId",
                table: "SyncChanges");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncEntities_Accounts_AccountId",
                table: "SyncEntities");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Accounts_ChangesPrunedThroughSeq",
                table: "Accounts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Accounts_NextSeq",
                table: "Accounts");

            migrationBuilder.RenameColumn(
                name: "AccountId",
                table: "SyncEntities",
                newName: "StreamId");

            migrationBuilder.RenameColumn(
                name: "AccountId",
                table: "SyncChanges",
                newName: "StreamId");

            migrationBuilder.AddColumn<Guid>(
                name: "MovedTo",
                table: "SyncEntities",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActorId",
                table: "SyncChanges",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousGroups",
                table: "SyncChanges",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SyncStreams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<short>(type: "smallint", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    NextSeq = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    ChangesPrunedThroughSeq = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PurgeAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncStreams", x => x.Id);
                    table.CheckConstraint("CK_SyncStreams_Identity", "(\"Kind\" = 1 AND \"AccountId\" IS NOT NULL AND \"AccountId\" = \"Id\" AND \"SpaceId\" IS NULL) OR (\"Kind\" = 2 AND \"SpaceId\" IS NOT NULL AND \"SpaceId\" = \"Id\" AND \"AccountId\" IS NULL)");
                    table.CheckConstraint("CK_SyncStreams_NextSeq", "\"NextSeq\" >= 1");
                    table.CheckConstraint("CK_SyncStreams_Prune", "\"ChangesPrunedThroughSeq\" >= 0");
                    table.ForeignKey(
                        name: "FK_SyncStreams_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO "SyncStreams" ("Id", "Kind", "AccountId", "NextSeq", "ChangesPrunedThroughSeq", "CreatedAt")
                SELECT "Id", 1, "Id", "NextSeq", "ChangesPrunedThroughSeq", "CreatedAt" FROM "Accounts";
                """);

            migrationBuilder.DropColumn(
                name: "ChangesPrunedThroughSeq",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "NextSeq",
                table: "Accounts");

            migrationBuilder.CreateIndex(
                name: "IX_SyncEntities_Kind_EntityKey",
                table: "SyncEntities",
                columns: new[] { "Kind", "EntityKey" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncStreams_AccountId",
                table: "SyncStreams",
                column: "AccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncChanges_SyncStreams_StreamId",
                table: "SyncChanges",
                column: "StreamId",
                principalTable: "SyncStreams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncEntities_SyncStreams_StreamId",
                table: "SyncEntities",
                column: "StreamId",
                principalTable: "SyncStreams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SyncChanges_SyncStreams_StreamId",
                table: "SyncChanges");

            migrationBuilder.DropForeignKey(
                name: "FK_SyncEntities_SyncStreams_StreamId",
                table: "SyncEntities");

            migrationBuilder.DropIndex(
                name: "IX_SyncEntities_Kind_EntityKey",
                table: "SyncEntities");

            migrationBuilder.DropColumn(
                name: "MovedTo",
                table: "SyncEntities");

            migrationBuilder.DropColumn(
                name: "ActorId",
                table: "SyncChanges");

            migrationBuilder.DropColumn(
                name: "PreviousGroups",
                table: "SyncChanges");

            migrationBuilder.RenameColumn(
                name: "StreamId",
                table: "SyncEntities",
                newName: "AccountId");

            migrationBuilder.RenameColumn(
                name: "StreamId",
                table: "SyncChanges",
                newName: "AccountId");

            migrationBuilder.AddColumn<long>(
                name: "ChangesPrunedThroughSeq",
                table: "Accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "NextSeq",
                table: "Accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.Sql("""
                UPDATE "Accounts" a SET "NextSeq" = s."NextSeq", "ChangesPrunedThroughSeq" = s."ChangesPrunedThroughSeq"
                FROM "SyncStreams" s WHERE s."Id" = a."Id" AND s."Kind" = 1;
                """);
            migrationBuilder.DropTable(name: "SyncStreams");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Accounts_ChangesPrunedThroughSeq",
                table: "Accounts",
                sql: "\"ChangesPrunedThroughSeq\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Accounts_NextSeq",
                table: "Accounts",
                sql: "\"NextSeq\" >= 1");

            migrationBuilder.AddForeignKey(
                name: "FK_SyncChanges_Accounts_AccountId",
                table: "SyncChanges",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SyncEntities_Accounts_AccountId",
                table: "SyncEntities",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
