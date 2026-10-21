using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RoleStartPage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StartPage",
                table: "Roles",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StartPage",
                table: "Roles");
        }
    }
}
