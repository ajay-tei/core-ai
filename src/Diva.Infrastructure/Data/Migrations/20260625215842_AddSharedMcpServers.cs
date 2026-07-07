using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedMcpServers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "McpServerRefsJson",
                table: "GroupAgentTemplates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "McpServerRefsJson",
                table: "AgentDefinitions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantMcpServers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Transport = table.Column<string>(type: "TEXT", nullable: false),
                    Command = table.Column<string>(type: "TEXT", nullable: true),
                    ArgsJson = table.Column<string>(type: "TEXT", nullable: true),
                    EnvJson = table.Column<string>(type: "TEXT", nullable: true),
                    Endpoint = table.Column<string>(type: "TEXT", nullable: true),
                    PassSsoToken = table.Column<bool>(type: "INTEGER", nullable: false),
                    PassTenantHeaders = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultCredentialRef = table.Column<string>(type: "TEXT", nullable: true),
                    ApiKeyCredentialMappingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantMcpServers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantMcpServers_TenantId_Name",
                table: "TenantMcpServers",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantMcpServers");

            migrationBuilder.DropColumn(
                name: "McpServerRefsJson",
                table: "GroupAgentTemplates");

            migrationBuilder.DropColumn(
                name: "McpServerRefsJson",
                table: "AgentDefinitions");
        }
    }
}
