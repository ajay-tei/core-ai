using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase26_AgentMemoryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentMemories_TenantId_UserId",
                table: "AgentMemories");

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemories_ExpiresAt",
                table: "AgentMemories",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemories_TenantId_SessionId",
                table: "AgentMemories",
                columns: new[] { "TenantId", "SessionId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentMemories_ExpiresAt",
                table: "AgentMemories");

            migrationBuilder.DropIndex(
                name: "IX_AgentMemories_TenantId_SessionId",
                table: "AgentMemories");

            migrationBuilder.CreateIndex(
                name: "IX_AgentMemories_TenantId_UserId",
                table: "AgentMemories",
                columns: new[] { "TenantId", "UserId" },
                filter: "\"UserId\" IS NOT NULL");
        }
    }
}
