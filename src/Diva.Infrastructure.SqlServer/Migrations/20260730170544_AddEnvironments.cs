using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddEnvironments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EnvironmentId",
                table: "TenantMcpServers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LogicalId",
                table: "TenantMcpServers",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EnvironmentId",
                table: "ScheduledTasks",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LogicalId",
                table: "ScheduledTasks",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EnvironmentId",
                table: "AgentGroups",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LogicalId",
                table: "AgentGroups",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EnvironmentId",
                table: "AgentDefinitions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LogicalId",
                table: "AgentDefinitions",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantEnvironments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Rank = table.Column<int>(type: "int", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantEnvironments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantMcpServers_EnvironmentId",
                table: "TenantMcpServers",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantMcpServers_TenantId_EnvironmentId_LogicalId",
                table: "TenantMcpServers",
                columns: new[] { "TenantId", "EnvironmentId", "LogicalId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_EnvironmentId",
                table: "ScheduledTasks",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_TenantId_EnvironmentId_LogicalId",
                table: "ScheduledTasks",
                columns: new[] { "TenantId", "EnvironmentId", "LogicalId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentGroups_EnvironmentId",
                table: "AgentGroups",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentGroups_TenantId_EnvironmentId_LogicalId",
                table: "AgentGroups",
                columns: new[] { "TenantId", "EnvironmentId", "LogicalId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitions_EnvironmentId",
                table: "AgentDefinitions",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitions_TenantId_EnvironmentId_LogicalId",
                table: "AgentDefinitions",
                columns: new[] { "TenantId", "EnvironmentId", "LogicalId" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantEnvironments_TenantId_Slug",
                table: "TenantEnvironments",
                columns: new[] { "TenantId", "Slug" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentDefinitions_TenantEnvironments_EnvironmentId",
                table: "AgentDefinitions",
                column: "EnvironmentId",
                principalTable: "TenantEnvironments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentGroups_TenantEnvironments_EnvironmentId",
                table: "AgentGroups",
                column: "EnvironmentId",
                principalTable: "TenantEnvironments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ScheduledTasks_TenantEnvironments_EnvironmentId",
                table: "ScheduledTasks",
                column: "EnvironmentId",
                principalTable: "TenantEnvironments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TenantMcpServers_TenantEnvironments_EnvironmentId",
                table: "TenantMcpServers",
                column: "EnvironmentId",
                principalTable: "TenantEnvironments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentDefinitions_TenantEnvironments_EnvironmentId",
                table: "AgentDefinitions");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentGroups_TenantEnvironments_EnvironmentId",
                table: "AgentGroups");

            migrationBuilder.DropForeignKey(
                name: "FK_ScheduledTasks_TenantEnvironments_EnvironmentId",
                table: "ScheduledTasks");

            migrationBuilder.DropForeignKey(
                name: "FK_TenantMcpServers_TenantEnvironments_EnvironmentId",
                table: "TenantMcpServers");

            migrationBuilder.DropTable(
                name: "TenantEnvironments");

            migrationBuilder.DropIndex(
                name: "IX_TenantMcpServers_EnvironmentId",
                table: "TenantMcpServers");

            migrationBuilder.DropIndex(
                name: "IX_TenantMcpServers_TenantId_EnvironmentId_LogicalId",
                table: "TenantMcpServers");

            migrationBuilder.DropIndex(
                name: "IX_ScheduledTasks_EnvironmentId",
                table: "ScheduledTasks");

            migrationBuilder.DropIndex(
                name: "IX_ScheduledTasks_TenantId_EnvironmentId_LogicalId",
                table: "ScheduledTasks");

            migrationBuilder.DropIndex(
                name: "IX_AgentGroups_EnvironmentId",
                table: "AgentGroups");

            migrationBuilder.DropIndex(
                name: "IX_AgentGroups_TenantId_EnvironmentId_LogicalId",
                table: "AgentGroups");

            migrationBuilder.DropIndex(
                name: "IX_AgentDefinitions_EnvironmentId",
                table: "AgentDefinitions");

            migrationBuilder.DropIndex(
                name: "IX_AgentDefinitions_TenantId_EnvironmentId_LogicalId",
                table: "AgentDefinitions");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "TenantMcpServers");

            migrationBuilder.DropColumn(
                name: "LogicalId",
                table: "TenantMcpServers");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "ScheduledTasks");

            migrationBuilder.DropColumn(
                name: "LogicalId",
                table: "ScheduledTasks");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "AgentGroups");

            migrationBuilder.DropColumn(
                name: "LogicalId",
                table: "AgentGroups");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "AgentDefinitions");

            migrationBuilder.DropColumn(
                name: "LogicalId",
                table: "AgentDefinitions");
        }
    }
}
