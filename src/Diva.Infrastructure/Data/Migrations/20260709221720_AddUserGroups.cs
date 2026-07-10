using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentGroupUserGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentGroupId = table.Column<string>(type: "TEXT", nullable: false),
                    UserGroupId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentGroupUserGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentGroupUserGroups_AgentGroups_AgentGroupId",
                        column: x => x.AgentGroupId,
                        principalTable: "AgentGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentGroupUserGroups_UserGroups_UserGroupId",
                        column: x => x.UserGroupId,
                        principalTable: "UserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "McpServerUserGroupCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    McpServerId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserGroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    CredentialRef = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpServerUserGroupCredentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpServerUserGroupCredentials_TenantMcpServers_McpServerId",
                        column: x => x.McpServerId,
                        principalTable: "TenantMcpServers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_McpServerUserGroupCredentials_UserGroups_UserGroupId",
                        column: x => x.UserGroupId,
                        principalTable: "UserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserGroupMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserGroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserGroupMembers_UserGroups_UserGroupId",
                        column: x => x.UserGroupId,
                        principalTable: "UserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserGroupRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserGroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserGroupRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserGroupRoles_UserGroups_UserGroupId",
                        column: x => x.UserGroupId,
                        principalTable: "UserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentGroupUserGroups_AgentGroupId_UserGroupId",
                table: "AgentGroupUserGroups",
                columns: new[] { "AgentGroupId", "UserGroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentGroupUserGroups_TenantId",
                table: "AgentGroupUserGroups",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentGroupUserGroups_UserGroupId",
                table: "AgentGroupUserGroups",
                column: "UserGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_McpServerUserGroupCredentials_McpServerId_UserGroupId",
                table: "McpServerUserGroupCredentials",
                columns: new[] { "McpServerId", "UserGroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpServerUserGroupCredentials_TenantId",
                table: "McpServerUserGroupCredentials",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_McpServerUserGroupCredentials_UserGroupId",
                table: "McpServerUserGroupCredentials",
                column: "UserGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupMembers_TenantId_Email",
                table: "UserGroupMembers",
                columns: new[] { "TenantId", "Email" });

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupMembers_TenantId_UserId",
                table: "UserGroupMembers",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupMembers_UserGroupId_UserId",
                table: "UserGroupMembers",
                columns: new[] { "UserGroupId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupRoles_TenantId",
                table: "UserGroupRoles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_UserGroupRoles_UserGroupId_Role",
                table: "UserGroupRoles",
                columns: new[] { "UserGroupId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserGroups_TenantId_Name",
                table: "UserGroups",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentGroupUserGroups");

            migrationBuilder.DropTable(
                name: "McpServerUserGroupCredentials");

            migrationBuilder.DropTable(
                name: "UserGroupMembers");

            migrationBuilder.DropTable(
                name: "UserGroupRoles");

            migrationBuilder.DropTable(
                name: "UserGroups");
        }
    }
}
