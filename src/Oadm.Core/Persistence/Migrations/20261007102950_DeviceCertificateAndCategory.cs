using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oadm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeviceCertificateAndCategory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "Devices",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "CertIssuer",
                table: "Devices",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CertNameMatches",
                table: "Devices",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CertNotAfterUtc",
                table: "Devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertSubject",
                table: "Devices",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CertTrust",
                table: "Devices",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "ProductType",
                table: "Devices",
                type: "TEXT",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Category",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "CertIssuer",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "CertNameMatches",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "CertNotAfterUtc",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "CertSubject",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "CertTrust",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "ProductType",
                table: "Devices");
        }
    }
}
