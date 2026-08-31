using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task055ConcurrencyConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeduplicationSlot",
                table: "notifications",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT Id, ROW_NUMBER() OVER (PARTITION BY StudentId ORDER BY ValidFrom DESC, Id DESC) AS rn
                    FROM student_cards WHERE IsActive = 1
                )
                UPDATE student_cards
                SET IsActive = 0,
                    ValidTo = COALESCE(ValidTo, CURRENT_TIMESTAMP),
                    ReplacementReason = COALESCE(ReplacementReason, 'Concurrency constraint migration')
                WHERE Id IN (SELECT Id FROM ranked WHERE rn > 1);
                """);

            migrationBuilder.CreateIndex(
                name: "ux_student_cards_one_active",
                table: "student_cards",
                column: "StudentId",
                unique: true,
                filter: "IsActive = 1");

            migrationBuilder.CreateIndex(
                name: "ux_notifications_deduplication_slot",
                table: "notifications",
                column: "DeduplicationSlot",
                unique: true,
                filter: "DeduplicationSlot IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_student_cards_one_active",
                table: "student_cards");

            migrationBuilder.DropIndex(
                name: "ux_notifications_deduplication_slot",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "DeduplicationSlot",
                table: "notifications");
        }
    }
}
