using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chaos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class S012_Agents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PausedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClientId = table.Column<string>(type: "text", nullable: true),
                    RateWindowStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AddsInWindow = table.Column<int>(type: "integer", nullable: false),
                    ClaimsInWindow = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                    table.CheckConstraint("CK_Agents_Counters", "\"AddsInWindow\" BETWEEN 0 AND 60 AND \"ClaimsInWindow\" BETWEEN 0 AND 30");
                    table.CheckConstraint("CK_Agents_Name", "length(\"Name\") BETWEEN 1 AND 100");
                    table.ForeignKey(
                        name: "FK_Agents_Accounts_OwnerAccountId",
                        column: x => x.OwnerAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_Owner",
                table: "Agents",
                columns: new[] { "OwnerAccountId", "RemovedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Agents");
        }
    }
}
