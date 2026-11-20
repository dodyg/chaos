using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chaos.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class S006_Spaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SyncChanges_Op",
                table: "SyncChanges");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AuditEvents_Kind",
                table: "AuditEvents");

            migrationBuilder.AddColumn<long>(
                name: "MembershipVersion",
                table: "Accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "SpaceInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: true),
                    Role = table.Column<short>(type: "smallint", nullable: false),
                    CreatedByMemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MaxUses = table.Column<int>(type: "integer", nullable: true),
                    Uses = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpaceInvites", x => x.Id);
                    table.CheckConstraint("CK_SpaceInvites_Email_Lower", "\"Email\" IS NULL OR \"Email\" = lower(\"Email\")");
                    table.CheckConstraint("CK_SpaceInvites_MaxUses", "\"MaxUses\" IS NULL OR \"MaxUses\" > 0");
                    table.CheckConstraint("CK_SpaceInvites_Role", "\"Role\" IN (2, 3)");
                    table.CheckConstraint("CK_SpaceInvites_Uses", "\"Uses\" >= 0");
                    table.ForeignKey(
                        name: "FK_SpaceInvites_SyncStreams_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "SyncStreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpaceMembers",
                columns: table => new
                {
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberKind = table.Column<short>(type: "smallint", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Role = table.Column<short>(type: "smallint", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    InvitedByMemberId = table.Column<Guid>(type: "uuid", nullable: true),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AllowedContainers = table.Column<string>(type: "jsonb", nullable: true),
                    GitHubLogin = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpaceMembers", x => new { x.SpaceId, x.MemberId });
                    table.CheckConstraint("CK_SpaceMembers_KindRole", "(\"MemberKind\" = 1 AND \"Role\" IN (1, 2, 3)) OR (\"MemberKind\" = 2 AND \"Role\" = 4)");
                    table.CheckConstraint("CK_SpaceMembers_MemberKind", "\"MemberKind\" IN (1, 2)");
                    table.CheckConstraint("CK_SpaceMembers_Role", "\"Role\" IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_SpaceMembers_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SpaceMembers_SyncStreams_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "SyncStreams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_SyncChanges_Op",
                table: "SyncChanges",
                sql: "\"Op\" IN ('upsert', 'purge', 'moved')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AuditEvents_Kind",
                table: "AuditEvents",
                sql: "\"Kind\" IN ('sign-in', 'device-added', 'device-revoked', 'account-deleted', 'quota', 'space-created', 'space-deleted', 'space-restored', 'member-added', 'member-removed', 'member-role', 'invite-created', 'invite-used', 'invite-revoked', 'agent-created', 'agent-paused', 'agent-removed', 'agent-reverted', 'move')");

            migrationBuilder.CreateIndex(
                name: "UX_Accounts_ProfileId",
                table: "Accounts",
                column: "ProfileId",
                unique: true,
                filter: "\"ProfileId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SpaceInvites_SpaceId",
                table: "SpaceInvites",
                column: "SpaceId");

            migrationBuilder.CreateIndex(
                name: "UX_SpaceInvites_TokenHash",
                table: "SpaceInvites",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpaceMembers_AccountId_Live",
                table: "SpaceMembers",
                column: "AccountId",
                filter: "\"RemovedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SpaceInvites");

            migrationBuilder.DropTable(
                name: "SpaceMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_SyncChanges_Op",
                table: "SyncChanges");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AuditEvents_Kind",
                table: "AuditEvents");

            migrationBuilder.DropIndex(
                name: "UX_Accounts_ProfileId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MembershipVersion",
                table: "Accounts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SyncChanges_Op",
                table: "SyncChanges",
                sql: "\"Op\" IN ('upsert', 'purge')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AuditEvents_Kind",
                table: "AuditEvents",
                sql: "\"Kind\" IN ('sign-in', 'device-added', 'device-revoked', 'account-deleted', 'quota')");
        }
    }
}
