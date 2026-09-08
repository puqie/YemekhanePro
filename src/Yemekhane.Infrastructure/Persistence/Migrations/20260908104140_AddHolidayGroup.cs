using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHolidayGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "holidays",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_holidays_group_id",
                table: "holidays",
                column: "GroupId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_holidays_group_id",
                table: "holidays");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "holidays");
        }
    }
}
