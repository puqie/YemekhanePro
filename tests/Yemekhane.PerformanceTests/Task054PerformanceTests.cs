using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;
using Yemekhane.Application.DailyTracking;
using Yemekhane.Application.Reports;
using Yemekhane.Application.Students;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Access;
using Yemekhane.Infrastructure.DailyTracking;
using Yemekhane.Infrastructure.Dashboard;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Reports;
using Yemekhane.Infrastructure.Students;

namespace Yemekhane.PerformanceTests;

public sealed class Task054PerformanceTests(ITestOutputHelper output)
{
    // These are regression smoke limits, not micro-benchmark claims. They intentionally tolerate slow CI hosts.
    private static readonly TimeSpan ExactLookupLimit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StudentSearchLimit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DashboardLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TrackingLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReportLimit = TimeSpan.FromSeconds(8);

    [Fact]
    public async Task TenThousandStudentCriticalQueriesStayWithinSmokeThresholds()
    {
        await using var database = await PerformanceDatabase.CreateAsync(StudentCount());
        await using var db = database.CreateContext();
        using var metrics = new AccessPerformanceMetrics();
        var cache = new AccessSnapshotCache(TimeProvider.System, metrics);
        var access = new EfAccessDecisionRepository(db, cache, cache, metrics);

        var lookup = await Measure(() => access.GetSnapshotAsync(database.TargetCard, database.DeviceId,
            database.MealTypeId, database.Date, default));
        var studentSearch = await Measure(() => new EfStudentRepository(db).SearchAsync(
            new StudentQuery(StudentNo: database.TargetStudentNo, PageSize: 50), default));
        var dashboard = await Measure(() => new EfDashboardRepository(db).GetAsync(database.Date,
            database.DayStart, database.DayStart.AddDays(1), database.DayStart.AddHours(12), default));
        var tracking = await Measure(() => new EfDailyTrackingRepository(db).GetAsync(new DailyTrackingQuery(100),
            database.DayStart, database.DayStart.AddDays(1), database.DayStart.AddHours(12), default));
        var report = await Measure(() => new EfReportRepository(db).QueryAsync(ReportType.DailyAccess,
            new ReportQuery(database.DayStart, database.DayStart.AddDays(1), PageSize: 100), default));

        output.WriteLine("Rows={0:N0}; cold-card={1:N1}ms; student={2:N1}ms; dashboard={3:N1}ms; tracking={4:N1}ms; report={5:N1}ms",
            StudentCount(), lookup.TotalMilliseconds, studentSearch.TotalMilliseconds, dashboard.TotalMilliseconds,
            tracking.TotalMilliseconds, report.TotalMilliseconds);

        Assert.True(lookup < ExactLookupLimit, $"Cold card lookup took {lookup.TotalMilliseconds:N0} ms");
        Assert.True(studentSearch < StudentSearchLimit, $"Student search took {studentSearch.TotalMilliseconds:N0} ms");
        Assert.True(dashboard < DashboardLimit, $"Dashboard took {dashboard.TotalMilliseconds:N0} ms");
        Assert.True(tracking < TrackingLimit, $"Daily tracking took {tracking.TotalMilliseconds:N0} ms");
        Assert.True(report < ReportLimit, $"Report page took {report.TotalMilliseconds:N0} ms");
    }

    [Fact]
    public async Task CriticalPlansUseExactAndExpressionIndexesAndLookupIsOneCommand()
    {
        await using var database = await PerformanceDatabase.CreateAsync(10_000);
        var cardPlan = await database.ExplainAsync(
            "SELECT StudentId FROM student_cards WHERE card_number = $value", ("$value", database.TargetCard));
        var accessPlan = await database.ExplainAsync(
            "SELECT OperationId FROM access_logs WHERE julianday(Timestamp) >= julianday($start) ORDER BY julianday(Timestamp) DESC, OperationId DESC LIMIT 100",
            ("$start", database.DayStart));

        Assert.Contains(cardPlan, x => x.Contains("ix_student_cards_card_number", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(accessPlan, x => x.Contains("ix_access_logs_instant_operation", StringComparison.OrdinalIgnoreCase));

        var counter = new CommandCounter();
        await using var db = database.CreateContext(counter);
        using var metrics = new AccessPerformanceMetrics();
        var cache = new AccessSnapshotCache(TimeProvider.System, metrics);
        var repository = new EfAccessDecisionRepository(db, cache, cache, metrics);
        await repository.GetSnapshotAsync(database.TargetCard, database.DeviceId, database.MealTypeId, database.Date, default);
        Assert.Equal(1, counter.ReaderCommands);
        await repository.GetSnapshotAsync(database.TargetCard, database.DeviceId, database.MealTypeId, database.Date, default);
        Assert.Equal(1, counter.ReaderCommands);
    }

    private static int StudentCount()
    {
        var configured = Environment.GetEnvironmentVariable("YEMEKHANE_PERF_STUDENTS");
        return int.TryParse(configured, out var count) && count is >= 10_000 and <= 1_000_000 ? count : 10_000;
    }

    private static async Task<TimeSpan> Measure(Func<Task> operation)
    {
        var started = Stopwatch.GetTimestamp();
        await operation();
        return Stopwatch.GetElapsedTime(started);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int ReaderCommands { get; private set; }
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            ReaderCommands++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PerformanceDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private PerformanceDatabase(SqliteConnection connection) => this.connection = connection;
        public DateOnly Date { get; } = new(2026, 8, 31);
        public DateTimeOffset DayStart { get; } = new(2026, 8, 30, 21, 0, 0, TimeSpan.Zero);
        public Guid DeviceId { get; private set; }
        public Guid MealTypeId { get; private set; }
        public string TargetCard { get; private set; } = null!;
        public string TargetStudentNo { get; private set; } = null!;

        public static async Task<PerformanceDatabase> CreateAsync(int studentCount)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var database = new PerformanceDatabase(connection);
            await using var db = database.CreateContext();
            await db.Database.MigrateAsync();
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            var meal = new MealType { Name = "Lunch" };
            var device = new Device { Name = "Gate", DeviceType = "Reader", ConnectionType = "TCP", Direction = "Entry", ConnectionStatus = "Online" };
            db.AddRange(meal, device);
            var students = new List<Student>(studentCount);
            var cards = new List<StudentCard>(studentCount);
            var rights = new List<MealEntitlement>(studentCount);
            var logs = new List<AccessLog>(studentCount);
            for (var i = 0; i < studentCount; i++)
            {
                var number = i.ToString("D7", System.Globalization.CultureInfo.InvariantCulture);
                var student = new Student { StudentNo = number, FirstName = "Student", LastName = number };
                students.Add(student);
                cards.Add(new StudentCard { StudentId = student.Id, CardNumber = "CARD-" + number, ValidFrom = database.DayStart });
                rights.Add(new MealEntitlement { StudentId = student.Id, MealTypeId = meal.Id, EntitlementDate = database.Date, Quantity = 1, Status = "Active" });
                logs.Add(new AccessLog { StudentId = student.Id, DeviceId = device.Id, MealTypeId = meal.Id,
                    Timestamp = database.DayStart.AddMilliseconds(i), CardNumber = "CARD-" + number,
                    Decision = i % 10 == 0 ? "DENY" : "ALLOW", Reason = "seed", Direction = "Entry",
                    ReaderSource = "Performance", OperationId = Guid.NewGuid() });
            }
            db.AddRange(students); db.AddRange(cards); db.AddRange(rights); db.AddRange(logs);
            await db.SaveChangesAsync();
            database.DeviceId = device.Id;
            database.MealTypeId = meal.Id;
            database.TargetStudentNo = (studentCount - 1).ToString("D7", System.Globalization.CultureInfo.InvariantCulture);
            database.TargetCard = "CARD-" + database.TargetStudentNo;
            return database;
        }

        public YemekhaneDbContext CreateContext(params IInterceptor[] interceptors)
        {
            var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection);
            if (interceptors.Length > 0) options.AddInterceptors(interceptors);
            return new YemekhaneDbContext(options.Options);
        }

        public async Task<IReadOnlyList<string>> ExplainAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "EXPLAIN QUERY PLAN " + sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await using var reader = await command.ExecuteReaderAsync();
            var result = new List<string>();
            while (await reader.ReadAsync()) result.Add(reader.GetString(3));
            return result;
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
