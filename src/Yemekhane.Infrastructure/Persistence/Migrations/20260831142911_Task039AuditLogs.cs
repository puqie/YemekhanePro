using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task039AuditLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "audit_logs",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_Action",
                table: "audit_logs",
                column: "Action");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_BulkOperationId",
                table: "audit_logs",
                column: "BulkOperationId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_CorrelationId",
                table: "audit_logs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_UserId",
                table: "audit_logs",
                column: "UserId");

            migrationBuilder.Sql("""
                CREATE TRIGGER audit_logs_immutable_update
                BEFORE UPDATE ON audit_logs
                BEGIN
                    SELECT RAISE(ABORT, 'audit logs are immutable');
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER audit_logs_immutable_delete
                BEFORE DELETE ON audit_logs
                BEGIN
                    SELECT RAISE(ABORT, 'audit logs are immutable');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_logs_immutable_update;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_logs_immutable_delete;");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_Action",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_BulkOperationId",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_CorrelationId",
                table: "audit_logs");

            migrationBuilder.DropIndex(
                name: "IX_audit_logs_UserId",
                table: "audit_logs");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "audit_logs");
        }
    }
}
