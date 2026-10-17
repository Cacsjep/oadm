using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WaitingDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WaitingDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    NormalizedAddress = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    ExpectedSerial = table.Column<string>(type: "TEXT", maxLength: 12, nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EncryptedPassword = table.Column<byte[]>(type: "BLOB", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AddedBy = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastTryUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResult = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    LoginFailed = table.Column<bool>(type: "INTEGER", nullable: false),
                    Tries = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaitingDevices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WaitingDevices_NormalizedAddress",
                table: "WaitingDevices",
                column: "NormalizedAddress",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WaitingDevices");
        }
    }
}
