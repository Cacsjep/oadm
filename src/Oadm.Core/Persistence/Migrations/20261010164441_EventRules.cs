using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EventRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventLogEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Result = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    What = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Device = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Rule = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventLogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastFiredUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EventRuleStates",
                columns: table => new
                {
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SubjectId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SinceUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventRuleStates", x => new { x.RuleId, x.SubjectId });
                    table.ForeignKey(
                        name: "FK_EventRuleStates_EventRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "EventRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventLogEntries_RuleId",
                table: "EventLogEntries",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_EventLogEntries_TimeUtc",
                table: "EventLogEntries",
                column: "TimeUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventLogEntries");

            migrationBuilder.DropTable(
                name: "EventRuleStates");

            migrationBuilder.DropTable(
                name: "EventRules");
        }
    }
}
