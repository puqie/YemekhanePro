using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task030IncomeModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "income_types",
                type: "TEXT",
                maxLength: 100,
                nullable: false,
                collation: "NOCASE",
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                table: "income_transactions",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.Sql("UPDATE income_transactions SET OperationId = Id WHERE OperationId = '00000000-0000-0000-0000-000000000000';");

            migrationBuilder.AddColumn<string>(
                name: "VoidReason",
                table: "income_transactions",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VoidedAt",
                table: "income_transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VoidedBy",
                table: "income_transactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_income_transactions_OperationId",
                table: "income_transactions",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_income_transactions_StudentId_TransactionAt",
                table: "income_transactions",
                columns: new[] { "StudentId", "TransactionAt" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_transactions_amount",
                table: "income_transactions",
                sql: "Amount > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_income_transactions_income_types_IncomeTypeId",
                table: "income_transactions",
                column: "IncomeTypeId",
                principalTable: "income_types",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_income_transactions_students_StudentId",
                table: "income_transactions",
                column: "StudentId",
                principalTable: "students",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_income_transactions_income_types_IncomeTypeId",
                table: "income_transactions");

            migrationBuilder.DropForeignKey(
                name: "FK_income_transactions_students_StudentId",
                table: "income_transactions");

            migrationBuilder.DropIndex(
                name: "IX_income_transactions_OperationId",
                table: "income_transactions");

            migrationBuilder.DropIndex(
                name: "IX_income_transactions_StudentId_TransactionAt",
                table: "income_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_income_transactions_amount",
                table: "income_transactions");

            migrationBuilder.DropColumn(
                name: "OperationId",
                table: "income_transactions");

            migrationBuilder.DropColumn(
                name: "VoidReason",
                table: "income_transactions");

            migrationBuilder.DropColumn(
                name: "VoidedAt",
                table: "income_transactions");

            migrationBuilder.DropColumn(
                name: "VoidedBy",
                table: "income_transactions");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "income_types",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 100,
                oldCollation: "NOCASE");
        }
    }
}
