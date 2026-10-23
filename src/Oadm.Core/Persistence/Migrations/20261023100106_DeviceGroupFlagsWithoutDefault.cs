using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceGroupFlagsWithoutDefault : Migration
    {
        /// <inheritdoc />
        /// <remarks>
        /// The migration DeviceGroups added <c>Users.AllDevices</c>, <c>Devices.AddedByAllDevices</c> and
        /// <c>ComplianceChecks.OwnerAllDevices</c> with the database default 1, so every existing row got all devices. The
        /// default stayed on the columns: a row inserted without the column (raw SQL, a later writer) would get all devices.
        /// The columns lose the default (fail closed: such an insert fails, NOT NULL); the values of existing rows stay.
        /// SQLite rebuilds the three tables for it.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "AllDevices",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<bool>(
                name: "AddedByAllDevices",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: true);

            migrationBuilder.AlterColumn<bool>(
                name: "OwnerAllDevices",
                table: "ComplianceChecks",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "INTEGER",
                oldDefaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<bool>(
                name: "AllDevices",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<bool>(
                name: "AddedByAllDevices",
                table: "Devices",
                type: "INTEGER",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<bool>(
                name: "OwnerAllDevices",
                table: "ComplianceChecks",
                type: "INTEGER",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "INTEGER");
        }
    }
}
