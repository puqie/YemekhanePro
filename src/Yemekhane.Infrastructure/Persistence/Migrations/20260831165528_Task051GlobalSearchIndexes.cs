using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task051GlobalSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_students_first_name",
                table: "students",
                column: "FirstName");

            migrationBuilder.CreateIndex(
                name: "ix_holidays_name",
                table: "holidays",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_students_first_name",
                table: "students");

            migrationBuilder.DropIndex(
                name: "ix_holidays_name",
                table: "holidays");
        }
    }
}
