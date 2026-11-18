using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chaos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class S002_Sync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ChangesPrunedThroughSeq",
                table: "Accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "SyncChanges",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<long>(type: "bigint", nullable: false),
                    ChangeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    EntityKey = table.Column<string>(type: "text", nullable: false),
                    Op = table.Column<string>(type: "text", nullable: false),
                    Groups = table.Column<string>(type: "jsonb", nullable: false),
                    OriginDevice = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncChanges", x => new { x.AccountId, x.Seq });
                    table.CheckConstraint("CK_SyncChanges_Op", "\"Op\" IN ('upsert', 'purge')");
                    table.CheckConstraint("CK_SyncChanges_Seq", "\"Seq\" >= 1");
                    table.ForeignKey(
                        name: "FK_SyncChanges_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyncEntities",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    EntityKey = table.Column<string>(type: "text", nullable: false),
                    Groups = table.Column<string>(type: "jsonb", nullable: false),
                    PurgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSeq = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncEntities", x => new { x.AccountId, x.Kind, x.EntityKey });
                    table.CheckConstraint("CK_SyncEntities_LastSeq", "\"LastSeq\" >= 0");
                    table.ForeignKey(
                        name: "FK_SyncEntities_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Accounts_ChangesPrunedThroughSeq",
                table: "Accounts",
                sql: "\"ChangesPrunedThroughSeq\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_SyncChanges_CreatedAt",
                table: "SyncChanges",
                columns: new[] { "AccountId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_SyncChanges_ChangeId",
                table: "SyncChanges",
                columns: new[] { "AccountId", "ChangeId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncChanges");

            migrationBuilder.DropTable(
                name: "SyncEntities");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Accounts_ChangesPrunedThroughSeq",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "ChangesPrunedThroughSeq",
                table: "Accounts");
        }
    }
}
