using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLastRunStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastRunStatus",
                table: "ScheduledTasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastRunStatus",
                table: "ScheduledTasks");
        }
    }
}
