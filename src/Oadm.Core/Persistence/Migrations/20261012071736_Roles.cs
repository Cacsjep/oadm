using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Roles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The built-in roles first; their permissions are filled at the first server start (RoleStore.SyncCatalogAsync).
            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PermissionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    KnownJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_NormalizedName",
                table: "Roles",
                column: "NormalizedName",
                unique: true);

            var created = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
            migrationBuilder.InsertData(
                table: "Roles",
                columns: ["Id", "Name", "NormalizedName", "Description", "Kind", "PermissionsJson", "KnownJson", "CreatedUtc", "ChangedUtc"],
                values: new object[,]
                {
                    { new Guid("00000000-0000-0000-0000-000000000001"), "Administrator", "ADMINISTRATOR", "Everything, including what plugins add later.", "Administrator", "[]", "[]", created, created },
                    { new Guid("00000000-0000-0000-0000-000000000002"), "Operator", "OPERATOR", "Works with devices and tasks.", "Operator", "[]", "[]", created, created },
                    { new Guid("00000000-0000-0000-0000-000000000003"), "Viewer", "VIEWER", "Sees devices, live view and the monitoring pages.", "Viewer", "[]", "[]", created, created },
                });

            // Every user gets the role of the old two-value column: Admin = Administrator, else Operator.
            migrationBuilder.AddColumn<Guid>(
                name: "RoleId",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000002"));

            migrationBuilder.Sql("UPDATE \"Users\" SET \"RoleId\" = '00000000-0000-0000-0000-000000000001' WHERE \"Role\" = 'Admin';");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Users");

            migrationBuilder.CreateIndex(
                name: "IX_Users_RoleId",
                table: "Users",
                column: "RoleId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Roles_RoleId",
                table: "Users",
                column: "RoleId",
                principalTable: "Roles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Roles_RoleId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Users_RoleId",
                table: "Users");

            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Users",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Operator");

            migrationBuilder.Sql("UPDATE \"Users\" SET \"Role\" = 'Admin' WHERE \"RoleId\" = '00000000-0000-0000-0000-000000000001';");

            migrationBuilder.DropColumn(
                name: "RoleId",
                table: "Users");
        }
    }
}
