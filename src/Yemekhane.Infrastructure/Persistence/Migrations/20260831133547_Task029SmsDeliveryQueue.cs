using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task029SmsDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sms_logs_Status_CreatedAt",
                table: "sms_logs");

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                table: "sms_logs",
                type: "TEXT",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "sms_logs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ClaimToken",
                table: "sms_logs",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "sms_logs",
                type: "TEXT",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "sms_logs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderMessageId",
                table: "sms_logs",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SendingStartedAt",
                table: "sms_logs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql("UPDATE sms_logs SET IdempotencyKey = Id WHERE IdempotencyKey = '';");

            migrationBuilder.CreateIndex(
                name: "IX_sms_logs_IdempotencyKey",
                table: "sms_logs",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sms_logs_Status_NextAttemptAt_CreatedAt",
                table: "sms_logs",
                columns: new[] { "Status", "NextAttemptAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_sms_logs_IdempotencyKey",
                table: "sms_logs");

            migrationBuilder.DropIndex(
                name: "IX_sms_logs_Status_NextAttemptAt_CreatedAt",
                table: "sms_logs");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "sms_logs");

            migrationBuilder.DropColumn(
                name: "ClaimToken",
                table: "sms_logs");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "sms_logs");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "sms_logs");

            migrationBuilder.DropColumn(
                name: "ProviderMessageId",
                table: "sms_logs");

            migrationBuilder.DropColumn(
                name: "SendingStartedAt",
                table: "sms_logs");

            migrationBuilder.AlterColumn<string>(
                name: "Provider",
                table: "sms_logs",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_sms_logs_Status_CreatedAt",
                table: "sms_logs",
                columns: new[] { "Status", "CreatedAt" });
        }
    }
}
