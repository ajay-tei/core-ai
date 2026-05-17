using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPreferencesAndMemoryUserId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add UserId column to AgentMemories (nullable — existing rows stay null)
            migrationBuilder.AddColumn<string>(
                name: "UserId",
                table: "AgentMemories",
                type: "TEXT",
                nullable: true);

            // Create UserPreferences table
            migrationBuilder.CreateTable(
                name: "UserPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPreferences", x => x.Id);
                });

            // Indexes for AgentMemories
            migrationBuilder.CreateIndex(
                name: "IX_AgentMemories_TenantId_AgentId_MemoryType",
                table: "AgentMemories",
                columns: new[] { "TenantId", "AgentId", "MemoryType" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemories_TenantId_UserId",
                table: "AgentMemories",
                columns: new[] { "TenantId", "UserId" },
                filter: "\"UserId\" IS NOT NULL");

            // Indexes for UserPreferences
            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_TenantId_UserId",
                table: "UserPreferences",
                columns: new[] { "TenantId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserPreferences_TenantId_UserId_Category_Key",
                table: "UserPreferences",
                columns: new[] { "TenantId", "UserId", "Category", "Key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UserPreferences");

            migrationBuilder.DropIndex(name: "IX_AgentMemories_TenantId_UserId", table: "AgentMemories");
            migrationBuilder.DropIndex(name: "IX_AgentMemories_TenantId_AgentId_MemoryType", table: "AgentMemories");

            migrationBuilder.DropColumn(name: "UserId", table: "AgentMemories");
        }
    }
}
