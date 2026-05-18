using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFailureKeywords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FailureKeywords",
                table: "ScheduledTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FailureKeywords",
                table: "GroupScheduledTasks",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailureKeywords",
                table: "ScheduledTasks");

            migrationBuilder.DropColumn(
                name: "FailureKeywords",
                table: "GroupScheduledTasks");
        }
    }
}
