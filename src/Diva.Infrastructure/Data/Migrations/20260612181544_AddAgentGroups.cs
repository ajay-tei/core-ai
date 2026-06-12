using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedGroupIdsJson",
                table: "PlatformApiKeys",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgentGroups",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    AgentIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    AllowedUserIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    AllowedRolesJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentGroups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentGroups_TenantId",
                table: "AgentGroups",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentGroups");

            migrationBuilder.DropColumn(
                name: "AllowedGroupIdsJson",
                table: "PlatformApiKeys");
        }
    }
}
