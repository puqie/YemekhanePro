using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yemekhane.Infrastructure.Persistence.Migrations;

[DbContext(typeof(YemekhaneDbContext))]
[Migration("20260831175100_Task058LargeDatasetIndexes")]
public sealed class Task058LargeDatasetIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"ix_access_logs_decision_instant\";");
        migrationBuilder.Sql("""
            CREATE INDEX "ix_access_logs_instant_id"
            ON "access_logs" (julianday("Timestamp") DESC, "Id");
            """);
        migrationBuilder.Sql("""
            CREATE INDEX "ix_access_logs_decision_instant_id"
            ON "access_logs" ("Decision", julianday("Timestamp") DESC, "Id");
            """);
        migrationBuilder.Sql("""
            CREATE INDEX "ix_meal_entitlements_date_status_student"
            ON "meal_entitlements" ("EntitlementDate", "Status", "StudentId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"ix_meal_entitlements_date_status_student\";");
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"ix_access_logs_decision_instant_id\";");
        migrationBuilder.Sql("DROP INDEX IF EXISTS \"ix_access_logs_instant_id\";");
        migrationBuilder.Sql("""
            CREATE INDEX "ix_access_logs_decision_instant"
            ON "access_logs" ("Decision", julianday("Timestamp") DESC);
            """);
    }
}
