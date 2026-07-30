using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PromotionRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantId = table.Column<int>(type: "INTEGER", nullable: false),
                    RootObjectType = table.Column<string>(type: "TEXT", nullable: false),
                    RootLogicalId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromEnvironmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    ToEnvironmentId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PromotedVersionsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromotionRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PromotionRuns_TenantId_RootLogicalId",
                table: "PromotionRuns",
                columns: new[] { "TenantId", "RootLogicalId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PromotionRuns");
        }
    }
}
