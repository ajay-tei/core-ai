using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PromotableObjects",
                columns: table => new
                {
                    LogicalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    ObjectType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OriginEnvironmentId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromotableObjects", x => x.LogicalId);
                    table.ForeignKey(
                        name: "FK_PromotableObjects_TenantEnvironments_OriginEnvironmentId",
                        column: x => x.OriginEnvironmentId,
                        principalTable: "TenantEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PromotableVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LogicalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    ContentHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PromotedFromVersionId = table.Column<int>(type: "int", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ChangeNote = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromotableVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PromotableVersions_PromotableObjects_LogicalId",
                        column: x => x.LogicalId,
                        principalTable: "PromotableObjects",
                        principalColumn: "LogicalId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PromotableVersions_PromotableVersions_PromotedFromVersionId",
                        column: x => x.PromotedFromVersionId,
                        principalTable: "PromotableVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EnvironmentDeployments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LogicalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    EnvironmentId = table.Column<int>(type: "int", nullable: false),
                    LiveVersionId = table.Column<int>(type: "int", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentDeployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnvironmentDeployments_PromotableObjects_LogicalId",
                        column: x => x.LogicalId,
                        principalTable: "PromotableObjects",
                        principalColumn: "LogicalId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EnvironmentDeployments_PromotableVersions_LiveVersionId",
                        column: x => x.LiveVersionId,
                        principalTable: "PromotableVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EnvironmentDeployments_TenantEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "TenantEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentDeployments_EnvironmentId",
                table: "EnvironmentDeployments",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentDeployments_LiveVersionId",
                table: "EnvironmentDeployments",
                column: "LiveVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentDeployments_LogicalId_EnvironmentId",
                table: "EnvironmentDeployments",
                columns: new[] { "LogicalId", "EnvironmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PromotableObjects_OriginEnvironmentId",
                table: "PromotableObjects",
                column: "OriginEnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_PromotableObjects_TenantId_ObjectType",
                table: "PromotableObjects",
                columns: new[] { "TenantId", "ObjectType" });

            migrationBuilder.CreateIndex(
                name: "IX_PromotableVersions_LogicalId_Version",
                table: "PromotableVersions",
                columns: new[] { "LogicalId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PromotableVersions_PromotedFromVersionId",
                table: "PromotableVersions",
                column: "PromotedFromVersionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnvironmentDeployments");

            migrationBuilder.DropTable(
                name: "PromotableVersions");

            migrationBuilder.DropTable(
                name: "PromotableObjects");
        }
    }
}
