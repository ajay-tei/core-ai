using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpCredentialEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_McpCredentials_TenantId_Name",
                table: "McpCredentials");

            migrationBuilder.AddColumn<int>(
                name: "EnvironmentId",
                table: "McpCredentials",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpCredentials_EnvironmentId",
                table: "McpCredentials",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_McpCredentials_TenantId_Name_EnvironmentId",
                table: "McpCredentials",
                columns: new[] { "TenantId", "Name", "EnvironmentId" },
                unique: true,
                filter: "[EnvironmentId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_McpCredentials_TenantEnvironments_EnvironmentId",
                table: "McpCredentials",
                column: "EnvironmentId",
                principalTable: "TenantEnvironments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_McpCredentials_TenantEnvironments_EnvironmentId",
                table: "McpCredentials");

            migrationBuilder.DropIndex(
                name: "IX_McpCredentials_EnvironmentId",
                table: "McpCredentials");

            migrationBuilder.DropIndex(
                name: "IX_McpCredentials_TenantId_Name_EnvironmentId",
                table: "McpCredentials");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "McpCredentials");

            migrationBuilder.CreateIndex(
                name: "IX_McpCredentials_TenantId_Name",
                table: "McpCredentials",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }
    }
}
