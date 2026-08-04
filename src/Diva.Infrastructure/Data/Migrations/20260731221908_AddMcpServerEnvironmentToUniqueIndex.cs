using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpServerEnvironmentToUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantMcpServers_TenantId_Name",
                table: "TenantMcpServers");

            migrationBuilder.CreateIndex(
                name: "IX_TenantMcpServers_TenantId_Name_EnvironmentId",
                table: "TenantMcpServers",
                columns: new[] { "TenantId", "Name", "EnvironmentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantMcpServers_TenantId_Name_EnvironmentId",
                table: "TenantMcpServers");

            migrationBuilder.CreateIndex(
                name: "IX_TenantMcpServers_TenantId_Name",
                table: "TenantMcpServers",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }
    }
}
