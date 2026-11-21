using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chaos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class S008_StreamUploadSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BlobParts_BlobUploads_AccountId_Sha256",
                table: "BlobParts");

            migrationBuilder.DropPrimaryKey(
                name: "PK_BlobUploads",
                table: "BlobUploads");

            migrationBuilder.RenameColumn(
                name: "AccountId",
                table: "BlobParts",
                newName: "StreamId");

            migrationBuilder.AddColumn<Guid>(
                name: "StreamId",
                table: "BlobUploads",
                type: "uuid",
                nullable: true);

            // Existing sessions belong to the account stream and keep their original part keys.
            migrationBuilder.Sql("UPDATE \"BlobUploads\" SET \"StreamId\" = \"AccountId\";");
            migrationBuilder.AlterColumn<Guid>(
                name: "StreamId",
                table: "BlobUploads",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_BlobUploads",
                table: "BlobUploads",
                columns: new[] { "StreamId", "Sha256" });

            migrationBuilder.CreateIndex(
                name: "IX_BlobUploads_AccountId",
                table: "BlobUploads",
                column: "AccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_BlobParts_BlobUploads_StreamId_Sha256",
                table: "BlobParts",
                columns: new[] { "StreamId", "Sha256" },
                principalTable: "BlobUploads",
                principalColumns: new[] { "StreamId", "Sha256" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_BlobUploads_SyncStreams_StreamId",
                table: "BlobUploads",
                column: "StreamId",
                principalTable: "SyncStreams",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM "BlobUploads" WHERE "StreamId" <> "AccountId") THEN
                        RAISE EXCEPTION 'Cannot downgrade while shared-stream uploads exist';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_BlobParts_BlobUploads_StreamId_Sha256",
                table: "BlobParts");

            migrationBuilder.DropForeignKey(
                name: "FK_BlobUploads_SyncStreams_StreamId",
                table: "BlobUploads");

            migrationBuilder.DropPrimaryKey(
                name: "PK_BlobUploads",
                table: "BlobUploads");

            migrationBuilder.DropIndex(
                name: "IX_BlobUploads_AccountId",
                table: "BlobUploads");

            migrationBuilder.DropColumn(
                name: "StreamId",
                table: "BlobUploads");

            migrationBuilder.RenameColumn(
                name: "StreamId",
                table: "BlobParts",
                newName: "AccountId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_BlobUploads",
                table: "BlobUploads",
                columns: new[] { "AccountId", "Sha256" });

            migrationBuilder.AddForeignKey(
                name: "FK_BlobParts_BlobUploads_AccountId_Sha256",
                table: "BlobParts",
                columns: new[] { "AccountId", "Sha256" },
                principalTable: "BlobUploads",
                principalColumns: new[] { "AccountId", "Sha256" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
