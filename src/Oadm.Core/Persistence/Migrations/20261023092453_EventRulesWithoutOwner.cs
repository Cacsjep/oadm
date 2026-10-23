using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EventRulesWithoutOwner : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// Event rules have no owner (decision 2026-10-23: rules, the event log and Test are for users with all devices): the
        /// owner columns of the unreleased migration DeviceGroups go again. Rules run on all devices as before device groups.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_EventRules_DeviceGroups_OwnerGroupId",
                table: "EventRules");

            migrationBuilder.DropIndex(
                name: "IX_EventRules_OwnerGroupId",
                table: "EventRules");

            migrationBuilder.DropColumn(
                name: "OwnerAllDevices",
                table: "EventRules");

            migrationBuilder.DropColumn(
                name: "OwnerGroupId",
                table: "EventRules");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OwnerAllDevices",
                table: "EventRules",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerGroupId",
                table: "EventRules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EventRules_OwnerGroupId",
                table: "EventRules",
                column: "OwnerGroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_EventRules_DeviceGroups_OwnerGroupId",
                table: "EventRules",
                column: "OwnerGroupId",
                principalTable: "DeviceGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
