using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedulerNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NotifyEmails",
                table: "ScheduledTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotifyOn",
                table: "ScheduledTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "ScheduledTaskRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IterationCount",
                table: "ScheduledTaskRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "ScheduledTaskRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotifyEmails",
                table: "GroupScheduledTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NotifyOn",
                table: "GroupScheduledTasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InputTokens",
                table: "GroupScheduledTaskRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IterationCount",
                table: "GroupScheduledTaskRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutputTokens",
                table: "GroupScheduledTaskRuns",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantNotificationSettings",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GlobalNotifyEmails = table.Column<string>(type: "TEXT", nullable: true),
                    GlobalNotifyOn = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantNotificationSettings", x => x.TenantId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantNotificationSettings");

            migrationBuilder.DropColumn(
                name: "NotifyEmails",
                table: "ScheduledTasks");

            migrationBuilder.DropColumn(
                name: "NotifyOn",
                table: "ScheduledTasks");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "ScheduledTaskRuns");

            migrationBuilder.DropColumn(
                name: "IterationCount",
                table: "ScheduledTaskRuns");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "ScheduledTaskRuns");

            migrationBuilder.DropColumn(
                name: "NotifyEmails",
                table: "GroupScheduledTasks");

            migrationBuilder.DropColumn(
                name: "NotifyOn",
                table: "GroupScheduledTasks");

            migrationBuilder.DropColumn(
                name: "InputTokens",
                table: "GroupScheduledTaskRuns");

            migrationBuilder.DropColumn(
                name: "IterationCount",
                table: "GroupScheduledTaskRuns");

            migrationBuilder.DropColumn(
                name: "OutputTokens",
                table: "GroupScheduledTaskRuns");
        }
    }
}
