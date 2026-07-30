using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyWidgetEnvironment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EnvironmentId",
                table: "WidgetConfigs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EnvironmentId",
                table: "PlatformApiKeys",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WidgetConfigs_EnvironmentId",
                table: "WidgetConfigs",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformApiKeys_EnvironmentId",
                table: "PlatformApiKeys",
                column: "EnvironmentId");

            migrationBuilder.AddForeignKey(
                name: "FK_PlatformApiKeys_TenantEnvironments_EnvironmentId",
                table: "PlatformApiKeys",
                column: "EnvironmentId",
                principalTable: "TenantEnvironments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WidgetConfigs_TenantEnvironments_EnvironmentId",
                table: "WidgetConfigs",
                column: "EnvironmentId",
                principalTable: "TenantEnvironments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlatformApiKeys_TenantEnvironments_EnvironmentId",
                table: "PlatformApiKeys");

            migrationBuilder.DropForeignKey(
                name: "FK_WidgetConfigs_TenantEnvironments_EnvironmentId",
                table: "WidgetConfigs");

            migrationBuilder.DropIndex(
                name: "IX_WidgetConfigs_EnvironmentId",
                table: "WidgetConfigs");

            migrationBuilder.DropIndex(
                name: "IX_PlatformApiKeys_EnvironmentId",
                table: "PlatformApiKeys");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "WidgetConfigs");

            migrationBuilder.DropColumn(
                name: "EnvironmentId",
                table: "PlatformApiKeys");
        }
    }
}
