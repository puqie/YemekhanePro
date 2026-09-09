using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTuitionPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tuition_plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StudentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Period = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    DownPaymentCents = table.Column<long>(type: "INTEGER", nullable: false),
                    InstallmentCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DueDayOfMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tuition_plans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tuition_plans_classes_ClassId",
                        column: x => x.ClassId,
                        principalTable: "classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tuition_plans_students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tuition_installments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StudentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    DueOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    PaidCents = table.Column<long>(type: "INTEGER", nullable: false),
                    IsCancelled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tuition_installments", x => x.Id);
                    table.CheckConstraint("ck_tuition_installment_paid", "PaidCents >= 0");
                    table.ForeignKey(
                        name: "FK_tuition_installments_students_StudentId",
                        column: x => x.StudentId,
                        principalTable: "students",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tuition_installments_tuition_plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "tuition_plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tuition_payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InstallmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncomeTransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    PaidAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tuition_payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tuition_payments_income_transactions_IncomeTransactionId",
                        column: x => x.IncomeTransactionId,
                        principalTable: "income_transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tuition_payments_tuition_installments_InstallmentId",
                        column: x => x.InstallmentId,
                        principalTable: "tuition_installments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_tuition_installments_plan_id",
                table: "tuition_installments",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "ix_tuition_installments_student_due",
                table: "tuition_installments",
                columns: new[] { "StudentId", "DueOn" });

            migrationBuilder.CreateIndex(
                name: "ix_tuition_payments_income_id",
                table: "tuition_payments",
                column: "IncomeTransactionId");

            migrationBuilder.CreateIndex(
                name: "ux_tuition_payments_installment_income",
                table: "tuition_payments",
                columns: new[] { "InstallmentId", "IncomeTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tuition_plans_class_period",
                table: "tuition_plans",
                columns: new[] { "ClassId", "Period" },
                unique: true,
                filter: "ClassId IS NOT NULL AND IsActive = 1");

            migrationBuilder.CreateIndex(
                name: "ux_tuition_plans_student_period",
                table: "tuition_plans",
                columns: new[] { "StudentId", "Period" },
                unique: true,
                filter: "StudentId IS NOT NULL AND IsActive = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tuition_payments");

            migrationBuilder.DropTable(
                name: "tuition_installments");

            migrationBuilder.DropTable(
                name: "tuition_plans");
        }
    }
}
