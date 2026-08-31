using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task045BulkOperationWizard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "bulk_operations",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "bulk_operations",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ResultJson",
                table: "bulk_operations",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE bulk_operations SET IdempotencyKey = 'legacy:' || Id, RequestHash = Id, ResultJson = '{}' WHERE IdempotencyKey = ''; ");

            migrationBuilder.CreateIndex(
                name: "IX_bulk_operations_CreatedAt",
                table: "bulk_operations",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_bulk_operations_IdempotencyKey",
                table: "bulk_operations",
                column: "IdempotencyKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_bulk_operations_CreatedAt",
                table: "bulk_operations");

            migrationBuilder.DropIndex(
                name: "IX_bulk_operations_IdempotencyKey",
                table: "bulk_operations");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "bulk_operations");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "bulk_operations");

            migrationBuilder.DropColumn(
                name: "ResultJson",
                table: "bulk_operations");
        }
    }
}
