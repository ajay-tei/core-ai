using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmConfigEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantLlmConfigs_TenantId_Name",
                table: "TenantLlmConfigs");

            migrationBuilder.DropIndex(
                name: "IX_GroupLlmConfigs_GroupId_Name",
                table: "GroupLlmConfigs");

            migrationBuilder.AddColumn<int>(
                name: "EnvironmentId",
                table: "TenantLlmConfigs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EnvironmentId",
                table: "GroupLlmConfigs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantLlmConfigs_EnvironmentId",
                table: "TenantLlmConfigs",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLlmConfigs_TenantId_Name_EnvironmentId",
                table: "TenantLlmConfigs",
                columns: new[] { "TenantId", "Name", "EnvironmentId" },
                unique: true,
                filter: "[Name] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GroupLlmConfigs_EnvironmentId",
                table: "GroupLlmConfigs",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupLlmConfigs_GroupId_Name_EnvironmentId",
                table: "GroupLlmConfigs",
                columns: new[] { "GroupId", "Name", "EnvironmentId" },
                unique: true,
                filter: "[Name] IS NOT NULL AND [EnvironmentId] IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_GroupLlmConfigs_TenantEnvironments_EnvironmentId",
                table: "GroupLlmConfigs",
                column: "EnvironmentId",
                principalTable: "TenantEnvironments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_TenantLlmConfigs_TenantEnvironments_EnvironmentId",
                table: "TenantLlmConfigs",
                column: "EnvironmentId",
                principalTable: "TenantEnvironments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GroupLlmConfigs_TenantEnvironments_EnvironmentId",
                table: "GroupLlmConfigs");

            migrationBuilder.DropForeignKey(
                name: "FK_TenantLlmConfigs_TenantEnvironments_EnvironmentId",
                table: "TenantLlmConfigs");

            migrationBuilder.DropIndex(
                name: "IX_TenantLlmConfigs_EnvironmentId",
                table: "TenantLlmConfigs");

            migrationBuilder.DropIndex(
                name: "IX_TenantLlmConfigs_TenantId_Name_EnvironmentId",
                table: "TenantLlmConfigs");

            migrationBuilder.DropIndex(
                name: "IX_GroupLlmConfigs_EnvironmentId",
                table: "GroupLlmConfigs");

            migrationBuilder.DropIndex(
                name: "IX_GroupLlmConfigs_GroupId_Name_EnvironmentId",
                table: "GroupLlmConfigs");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "TenantLlmConfigs");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "GroupLlmConfigs");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLlmConfigs_TenantId_Name",
                table: "TenantLlmConfigs",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "[Name] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GroupLlmConfigs_GroupId_Name",
                table: "GroupLlmConfigs",
                columns: new[] { "GroupId", "Name" },
                unique: true,
                filter: "[Name] IS NOT NULL");
        }
    }
}
