using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chaos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class S003_Blobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlobUploads",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    ByteSize = table.Column<long>(type: "bigint", nullable: false),
                    PartCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlobUploads", x => new { x.AccountId, x.Sha256 });
                    table.CheckConstraint("CK_BlobUploads_ByteSize", "\"ByteSize\" >= 0 AND \"PartCount\" >= 0");
                    table.CheckConstraint("CK_BlobUploads_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_BlobUploads_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Blobs",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    ByteSize = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    StorageKey = table.Column<string>(type: "text", nullable: false),
                    WrappedKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UnreferencedSince = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Blobs", x => new { x.AccountId, x.Sha256 });
                    table.CheckConstraint("CK_Blobs_ByteSize", "\"ByteSize\" >= 0");
                    table.CheckConstraint("CK_Blobs_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_Blobs_State", "\"State\" IN ('Clean', 'Blocked')");
                    table.ForeignKey(
                        name: "FK_Blobs_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BlobParts",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sha256 = table.Column<string>(type: "text", nullable: false),
                    PartIndex = table.Column<int>(type: "integer", nullable: false),
                    ByteSize = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlobParts", x => new { x.AccountId, x.Sha256, x.PartIndex });
                    table.CheckConstraint("CK_BlobParts_PartIndex", "\"PartIndex\" >= 0 AND \"ByteSize\" > 0");
                    table.ForeignKey(
                        name: "FK_BlobParts_BlobUploads_AccountId_Sha256",
                        columns: x => new { x.AccountId, x.Sha256 },
                        principalTable: "BlobUploads",
                        principalColumns: new[] { "AccountId", "Sha256" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BlobUploads_UpdatedAt",
                table: "BlobUploads",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Blobs_UnreferencedSince",
                table: "Blobs",
                column: "UnreferencedSince");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlobParts");

            migrationBuilder.DropTable(
                name: "Blobs");

            migrationBuilder.DropTable(
                name: "BlobUploads");
        }
    }
}
