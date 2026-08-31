using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Task054PerformanceIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_access_logs_timestamp",
                table: "access_logs");

            // DateTimeOffset is stored as TEXT by SQLite. The application deliberately uses
            // julianday(Timestamp) for offset-independent comparisons, so the index must match it.
            migrationBuilder.Sql("""
                CREATE INDEX "ix_access_logs_instant_operation"
                ON "access_logs" (julianday("Timestamp") DESC, "OperationId" DESC);
                """);
            migrationBuilder.Sql("""
                CREATE INDEX "ix_access_logs_decision_instant"
                ON "access_logs" ("Decision", julianday("Timestamp") DESC);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"ix_access_logs_decision_instant\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"ix_access_logs_instant_operation\";");

            migrationBuilder.CreateIndex(
                name: "ix_access_logs_timestamp",
                table: "access_logs",
                column: "Timestamp");
        }
    }
}
