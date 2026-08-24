using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddEnableOneHourCacheToAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableOneHourCache",
                table: "GroupAgentTemplates",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableOneHourCache",
                table: "AgentDefinitions",
                type: "bit",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableOneHourCache",
                table: "GroupAgentTemplates");

            migrationBuilder.DropColumn(
                name: "EnableOneHourCache",
                table: "AgentDefinitions");
        }
    }
}
