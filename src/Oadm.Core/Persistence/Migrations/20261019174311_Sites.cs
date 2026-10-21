using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Sites : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SiteId",
                table: "WaitingDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SiteId",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Sites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: true),
                    NodeKeyFingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NodeId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BoundUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    NotBound = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sites", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_SiteId",
                table: "Devices",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_Sites_NodeId",
                table: "Sites",
                column: "NodeId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sites_NodeKeyFingerprint",
                table: "Sites",
                column: "NodeKeyFingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sites_NormalizedName",
                table: "Sites",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Sites");

            migrationBuilder.DropIndex(
                name: "IX_Devices_SiteId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "WaitingDevices");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "Devices");
        }
    }
}
