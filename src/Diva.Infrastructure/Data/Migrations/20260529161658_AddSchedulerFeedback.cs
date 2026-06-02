using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSchedulerFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SchedulerFeedbacks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", nullable: false),
                    ScheduledTaskId = table.Column<string>(type: "TEXT", nullable: false),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    ThumbsRating = table.Column<int>(type: "INTEGER", nullable: true),
                    StarRating = table.Column<int>(type: "INTEGER", nullable: true),
                    Category = table.Column<string>(type: "TEXT", nullable: true),
                    CorrectionText = table.Column<string>(type: "TEXT", nullable: true),
                    SubmitterName = table.Column<string>(type: "TEXT", nullable: true),
                    SubmitterEmail = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReviewNotes = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchedulerFeedbacks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SchedulerFeedbacks_RunId",
                table: "SchedulerFeedbacks",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_SchedulerFeedbacks_TenantId_Status_SubmittedAt",
                table: "SchedulerFeedbacks",
                columns: new[] { "TenantId", "Status", "SubmittedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SchedulerFeedbacks");
        }
    }
}
