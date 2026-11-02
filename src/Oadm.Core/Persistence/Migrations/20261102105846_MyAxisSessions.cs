using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MyAxisSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MyAxisSessions",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EncryptedSession = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SignedInUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastAcceptedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Expired = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MyAxisSessions", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_MyAxisSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MyAxisSessions");
        }
    }
}
