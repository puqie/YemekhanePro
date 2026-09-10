using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EntitlementChargeLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EntitlementDayCount",
                table: "income_transactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EntitlementEndsOn",
                table: "income_transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EntitlementStartsOn",
                table: "income_transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "MealTypeId",
                table: "income_transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_income_transactions_StudentId_MealTypeId",
                table: "income_transactions",
                columns: new[] { "StudentId", "MealTypeId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_income_transactions_StudentId_MealTypeId",
                table: "income_transactions");

            migrationBuilder.DropColumn(
                name: "EntitlementDayCount",
                table: "income_transactions");

            migrationBuilder.DropColumn(
                name: "EntitlementEndsOn",
                table: "income_transactions");

            migrationBuilder.DropColumn(
                name: "EntitlementStartsOn",
                table: "income_transactions");

            migrationBuilder.DropColumn(
                name: "MealTypeId",
                table: "income_transactions");
        }
    }
}
