using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddEnvironmentUserGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AllowedRolesJson",
                table: "TenantEnvironments",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EnvironmentUserGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    EnvironmentId = table.Column<int>(type: "int", nullable: false),
                    UserGroupId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnvironmentUserGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EnvironmentUserGroups_TenantEnvironments_EnvironmentId",
                        column: x => x.EnvironmentId,
                        principalTable: "TenantEnvironments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EnvironmentUserGroups_UserGroups_UserGroupId",
                        column: x => x.UserGroupId,
                        principalTable: "UserGroups",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentUserGroups_EnvironmentId_UserGroupId",
                table: "EnvironmentUserGroups",
                columns: new[] { "EnvironmentId", "UserGroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentUserGroups_TenantId",
                table: "EnvironmentUserGroups",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_EnvironmentUserGroups_UserGroupId",
                table: "EnvironmentUserGroups",
                column: "UserGroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnvironmentUserGroups");

            migrationBuilder.DropColumn(
                name: "AllowedRolesJson",
                table: "TenantEnvironments");
        }
    }
}
