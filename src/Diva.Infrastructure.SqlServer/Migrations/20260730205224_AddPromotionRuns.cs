using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.SqlServer.Migrations
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
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    RootObjectType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RootLogicalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FromEnvironmentId = table.Column<int>(type: "int", nullable: false),
                    ToEnvironmentId = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PromotedVersionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
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
