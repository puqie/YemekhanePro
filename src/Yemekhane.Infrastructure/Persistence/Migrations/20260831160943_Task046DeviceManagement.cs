using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task046DeviceManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_devices_IpAddress_IpPort",
                table: "devices");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastStatusAt",
                table: "devices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Model",
                table: "devices",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SerialNumber",
                table: "devices",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_devices_ComPort_BaudRate",
                table: "devices",
                columns: new[] { "ComPort", "BaudRate" },
                unique: true,
                filter: "ComPort IS NOT NULL AND BaudRate IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_devices_IpAddress_IpPort",
                table: "devices",
                columns: new[] { "IpAddress", "IpPort" },
                unique: true,
                filter: "IpAddress IS NOT NULL AND IpPort IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_devices_ComPort_BaudRate",
                table: "devices");

            migrationBuilder.DropIndex(
                name: "IX_devices_IpAddress_IpPort",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "LastStatusAt",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "Model",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "SerialNumber",
                table: "devices");

            migrationBuilder.CreateIndex(
                name: "IX_devices_IpAddress_IpPort",
                table: "devices",
                columns: new[] { "IpAddress", "IpPort" });
        }
    }
}
