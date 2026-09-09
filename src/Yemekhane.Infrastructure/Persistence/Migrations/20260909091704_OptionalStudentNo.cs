using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OptionalStudentNo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_students_student_no",
                table: "students");

            migrationBuilder.CreateIndex(
                name: "ix_students_student_no",
                table: "students",
                column: "student_no",
                unique: true,
                filter: "student_no <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_students_student_no",
                table: "students");

            migrationBuilder.CreateIndex(
                name: "ix_students_student_no",
                table: "students",
                column: "student_no",
                unique: true);
        }
    }
}
