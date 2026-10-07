using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Serial = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    UseHostName = table.Column<bool>(type: "INTEGER", nullable: false),
                    HostName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FirmwareVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DhcpEnabled = table.Column<bool>(type: "INTEGER", nullable: true),
                    HttpsEnabled = table.Column<bool>(type: "INTEGER", nullable: true),
                    Dot1xEnabled = table.Column<bool>(type: "INTEGER", nullable: true),
                    UpnpFriendlyName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ServerName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Scheme = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    CertFingerprintSha256 = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WarrantyExpiry = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ReplacementModel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ValueJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PluginId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Owner = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Progress = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    ScheduledUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceCredentials",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EncryptedPassword = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCredentials", x => x.DeviceId);
                    table.ForeignKey(
                        name: "FK_DeviceCredentials_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskDeviceResults",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: true),
                    Progress = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskDeviceResults", x => new { x.TaskId, x.DeviceId });
                    table.ForeignKey(
                        name: "FK_TaskDeviceResults_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_Serial",
                table: "Devices",
                column: "Serial",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskDeviceResults_DeviceId",
                table: "TaskDeviceResults",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_CreatedUtc",
                table: "Tasks",
                column: "CreatedUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceCredentials");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "TaskDeviceResults");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "Tasks");
        }
    }
}
