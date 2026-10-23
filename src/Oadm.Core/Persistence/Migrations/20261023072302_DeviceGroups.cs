using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceGroups : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Device groups (docs/spec/device-scope.md). Every existing user gets All devices, every existing device counts as
        /// added by a user with all devices, every existing event rule and compliance check is owned by all devices: an
        /// upgrade or the restore of an older backup changes nobody's devices. Rows written later choose explicitly.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "QueuedByUserId",
                table: "WaitingDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllDevices",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeviceGroupId",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OwnerAllDevices",
                table: "EventRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerGroupId",
                table: "EventRules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AddedByAllDevices",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AddedByGroupId",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OwnerAllDevices",
                table: "ComplianceChecks",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerGroupId",
                table: "ComplianceChecks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeviceGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ThisServer = table.Column<bool>(type: "INTEGER", nullable: false),
                    SiteIds = table.Column<string>(type: "TEXT", nullable: false),
                    TagIds = table.Column<string>(type: "TEXT", nullable: false),
                    Serials = table.Column<string>(type: "TEXT", nullable: false),
                    ExceptSerials = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceGroups", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_DeviceGroupId",
                table: "Users",
                column: "DeviceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_EventRules_OwnerGroupId",
                table: "EventRules",
                column: "OwnerGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceChecks_OwnerGroupId",
                table: "ComplianceChecks",
                column: "OwnerGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroups_NormalizedName",
                table: "DeviceGroups",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ComplianceChecks_DeviceGroups_OwnerGroupId",
                table: "ComplianceChecks",
                column: "OwnerGroupId",
                principalTable: "DeviceGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EventRules_DeviceGroups_OwnerGroupId",
                table: "EventRules",
                column: "OwnerGroupId",
                principalTable: "DeviceGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_DeviceGroups_DeviceGroupId",
                table: "Users",
                column: "DeviceGroupId",
                principalTable: "DeviceGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ComplianceChecks_DeviceGroups_OwnerGroupId",
                table: "ComplianceChecks");

            migrationBuilder.DropForeignKey(
                name: "FK_EventRules_DeviceGroups_OwnerGroupId",
                table: "EventRules");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_DeviceGroups_DeviceGroupId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "DeviceGroups");

            migrationBuilder.DropIndex(
                name: "IX_Users_DeviceGroupId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_EventRules_OwnerGroupId",
                table: "EventRules");

            migrationBuilder.DropIndex(
                name: "IX_ComplianceChecks_OwnerGroupId",
                table: "ComplianceChecks");

            migrationBuilder.DropColumn(
                name: "QueuedByUserId",
                table: "WaitingDevices");

            migrationBuilder.DropColumn(
                name: "AllDevices",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DeviceGroupId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OwnerAllDevices",
                table: "EventRules");

            migrationBuilder.DropColumn(
                name: "OwnerGroupId",
                table: "EventRules");

            migrationBuilder.DropColumn(
                name: "AddedByAllDevices",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "AddedByGroupId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "OwnerAllDevices",
                table: "ComplianceChecks");

            migrationBuilder.DropColumn(
                name: "OwnerGroupId",
                table: "ComplianceChecks");
        }
    }
}
