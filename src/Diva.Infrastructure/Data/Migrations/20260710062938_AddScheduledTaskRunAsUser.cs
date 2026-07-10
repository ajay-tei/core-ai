using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledTaskRunAsUser : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RunAsUserEmail",
                table: "ScheduledTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunAsUserId",
                table: "ScheduledTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RunAsUserLabel",
                table: "ScheduledTasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunAsUserEmail",
                table: "ScheduledTasks");

            migrationBuilder.DropColumn(
                name: "RunAsUserId",
                table: "ScheduledTasks");

            migrationBuilder.DropColumn(
                name: "RunAsUserLabel",
                table: "ScheduledTasks");
        }
    }
}
