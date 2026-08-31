using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;
using Yemekhane.Application.Access;
using Yemekhane.Application.DailyTracking;
using Yemekhane.Application.Reports;
using Yemekhane.Application.Students;
using Yemekhane.Infrastructure.Access;
using Yemekhane.Infrastructure.DailyTracking;
using Yemekhane.Infrastructure.Dashboard;
using Yemekhane.Infrastructure.Reports;
using Yemekhane.Infrastructure.Students;

namespace Yemekhane.PerformanceTests;

public sealed class Task058LargeDatasetTests(ITestOutputHelper output)
{
    private readonly Dictionary<string, (TimeSpan Actual, TimeSpan Limit)> timings = [];
    private long peakManagedBytes;

    [Fact]
    [Trait("Category", "LargeDataset")]
    public async Task FullScaleAcceptanceAndBenchmark()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("YEMEKHANE_LARGE_DATASET"), "1", StringComparison.Ordinal))
        {
            output.WriteLine("Skipped: run scripts\\run-large-dataset.cmd to enable the isolated full-scale fixture.");
            return;
        }

        SampleMemory();
        await using var fixture = await LargeDatasetFixture.CreateAsync();
        Record("seed", fixture.SeedDuration, TimeSpan.FromMinutes(4));
        SampleMemory();

        await VerifyCountsAsync(fixture);
        await VerifyPlansAsync(fixture);

        await using (var db = fixture.CreateContext())
        {
            using var metrics = new AccessPerformanceMetrics();
            var cache = new AccessSnapshotCache(TimeProvider.System, metrics);
            var access = new EfAccessDecisionRepository(db, cache, cache, metrics);
            var (snapshot, cold) = await MeasureAsync(() => access.GetSnapshotAsync(
                LargeDatasetFixture.TargetCard, LargeDatasetFixture.DeviceId, LargeDatasetFixture.MealTypeId, fixture.Date, default));
            Assert.Equal(LargeDatasetFixture.TargetStudentId, snapshot.StudentId);
            Record("exact-card-cold", cold, TimeSpan.FromSeconds(2));

            var hotStarted = Stopwatch.GetTimestamp();
            for (var i = 0; i < 1_000; i++)
                await access.GetSnapshotAsync(LargeDatasetFixture.TargetCard, LargeDatasetFixture.DeviceId, LargeDatasetFixture.MealTypeId, fixture.Date, default);
            Record("exact-card-hot-avg", Stopwatch.GetElapsedTime(hotStarted) / 1_000, TimeSpan.FromMilliseconds(5));
        }

        await using (var db = fixture.CreateContext())
        {
            var students = new EfStudentRepository(db);
            var (exact, exactTime) = await MeasureAsync(() => students.SearchAsync(
                new StudentQuery(StudentNo: LargeDatasetFixture.TargetStudentNo, PageSize: 50), default));
            Assert.Equal(LargeDatasetFixture.TargetStudentId, Assert.Single(exact.Items).Id);
            Record("student-number-exact", exactTime, TimeSpan.FromSeconds(5));

            var (namePage, nameTime) = await MeasureAsync(() => students.SearchAsync(
                new StudentQuery(FirstName: "Name042", Page: 2, PageSize: 25), default));
            Assert.Equal(100, namePage.TotalCount);
            Assert.Equal(25, namePage.Items.Count);
            Record("student-name-page-2", nameTime, TimeSpan.FromSeconds(12));

            var (classPage, classTime) = await MeasureAsync(() => students.SearchAsync(
                new StudentQuery(ClassId: LargeDatasetFixture.TargetClassId, Page: 40, PageSize: 25), default));
            Assert.Equal(1_000, classPage.TotalCount);
            Assert.Equal(25, classPage.Items.Count);
            Record("student-class-page-40", classTime, TimeSpan.FromSeconds(8));
        }

        await MeasureAtomicConsumptionAsync(fixture);
        await MeasureTrackingAsync(fixture);
        await MeasureReportsAsync(fixture);

        await using (var db = fixture.CreateContext())
        {
            var (dashboard, elapsed) = await MeasureAsync(() => new EfDashboardRepository(db).GetAsync(
                fixture.Date, fixture.DayStart, fixture.DayStart.AddDays(1), fixture.DayStart.AddHours(14), default));
            Assert.Equal(LargeDatasetFixture.StudentCount, dashboard.Kpis.ActiveStudents);
            Assert.True(dashboard.RecentAccess.Count <= 20);
            Assert.True(dashboard.ClassUsage.Count <= 10);
            Assert.True(dashboard.RecentErrors.Count <= 10);
            Record("dashboard", elapsed, TimeSpan.FromSeconds(15));
        }

        await fixture.CheckpointAsync();
        var fileSize = new FileInfo(fixture.Path).Length;
        SampleMemory();
        output.WriteLine("database-size={0:N1} MiB", fileSize / 1024d / 1024d);
        output.WriteLine("peak-managed-approx={0:N1} MiB; process-peak-working-set={1:N1} MiB",
            peakManagedBytes / 1024d / 1024d, Process.GetCurrentProcess().PeakWorkingSet64 / 1024d / 1024d);
        output.WriteLine("pagination: daily tracking is keyset-validated near the oldest rows; report UI remains offset-based for direct page navigation.");

        Assert.True(fileSize < 2L * 1024 * 1024 * 1024, $"Fixture is unexpectedly large: {fileSize:N0} bytes");
        Assert.True(peakManagedBytes < 1024L * 1024 * 1024, $"Approximate managed memory exceeded 1 GiB: {peakManagedBytes:N0}");
        var failures = timings.Where(x => x.Value.Actual > x.Value.Limit)
            .Select(x => $"{x.Key}: {x.Value.Actual.TotalMilliseconds:N1} ms > {x.Value.Limit.TotalMilliseconds:N1} ms")
            .ToArray();
        Assert.True(failures.Length == 0, "Large dataset thresholds failed: " + string.Join("; ", failures));
    }

    private async Task MeasureAtomicConsumptionAsync(LargeDatasetFixture fixture)
    {
        await using var db = fixture.CreateContext();
        using var metrics = new AccessPerformanceMetrics();
        var cache = new AccessSnapshotCache(TimeProvider.System, metrics);
        var access = new EfAccessDecisionRepository(db, cache, cache, metrics);
        var operationId = Guid.NewGuid();
        var timestamp = fixture.DayStart.AddHours(15);
        var request = new AccessCheckRequest(LargeDatasetFixture.TargetCard, LargeDatasetFixture.DeviceId, LargeDatasetFixture.MealTypeId, timestamp,
            OperationId: operationId);
        var decision = new AccessDecision("ALLOW", "Granted", LargeDatasetFixture.TargetStudentId, "Target Student",
            LargeDatasetFixture.DeviceId, LargeDatasetFixture.MealTypeId, timestamp, operationId);
        var (consumed, elapsed) = await MeasureAsync(() => access.TryConsumeAndLogAsync(
            LargeDatasetFixture.TargetEntitlementId, request, decision, default));
        Assert.True(consumed);
        Assert.Equal(1, await db.MealEntitlements.Where(x => x.Id == LargeDatasetFixture.TargetEntitlementId)
            .Select(x => x.ConsumedQuantity).SingleAsync());
        Assert.Equal(1, await db.AccessLogs.CountAsync(x => x.OperationId == operationId));
        Record("access-atomic-consume-log", elapsed, TimeSpan.FromSeconds(4));
    }

    private async Task MeasureTrackingAsync(LargeDatasetFixture fixture)
    {
        await using var db = fixture.CreateContext();
        var tracking = new EfDailyTrackingRepository(db);
        var (first, firstTime) = await MeasureAsync(() => tracking.GetAsync(new DailyTrackingQuery(100),
            fixture.DayStart, fixture.DayStart.AddDays(1), fixture.DayStart.AddHours(15), default));
        Assert.Equal(100, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Equal(LargeDatasetFixture.AccessLogCount + 1, first.Summary.Total);
        Record("daily-tracking-first", firstTime, TimeSpan.FromSeconds(12));

        var (next, nextTime) = await MeasureAsync(() => tracking.GetAsync(new DailyTrackingQuery(100,
                CursorTimestamp: first.NextCursorTimestamp, CursorOperationId: first.NextCursorOperationId),
            fixture.DayStart, fixture.DayStart.AddDays(1), fixture.DayStart.AddHours(15), default));
        Assert.Equal(100, next.Items.Count);
        Assert.Empty(first.Items.Select(x => x.OperationId).Intersect(next.Items.Select(x => x.OperationId)));
        Assert.True(next.Items[0].Timestamp <= first.Items[^1].Timestamp);
        Record("daily-tracking-next", nextTime, TimeSpan.FromSeconds(12));

        var deepIndex = 150;
        var (deep, deepTime) = await MeasureAsync(() => tracking.GetAsync(new DailyTrackingQuery(100,
                CursorTimestamp: fixture.LogTimestamp(deepIndex), CursorOperationId: LargeDatasetFixture.OperationId(deepIndex)),
            fixture.DayStart, fixture.DayStart.AddDays(1), fixture.DayStart.AddHours(15), default));
        Assert.Equal(100, deep.Items.Count);
        Assert.Equal(LargeDatasetFixture.OperationId(deepIndex - 1), deep.Items[0].OperationId);
        Assert.Equal(LargeDatasetFixture.OperationId(deepIndex - 100), deep.Items[^1].OperationId);
        Record("daily-tracking-deep-keyset", deepTime, TimeSpan.FromSeconds(12));
    }

    private async Task MeasureReportsAsync(LargeDatasetFixture fixture)
    {
        await using var db = fixture.CreateContext();
        var reports = new EfReportRepository(db);
        var deniedQuery = new ReportQuery(fixture.DayStart, fixture.DayStart.AddDays(1), Status: "No entitlement",
            PageSize: 100);
        var (denied, deniedTime) = await MeasureAsync(() => reports.QueryAsync(ReportType.DeniedAccess, deniedQuery, default));
        Assert.Equal(100_000, denied.Summary.TotalRecords);
        Assert.Equal(100, denied.Items.Count);
        Assert.All(denied.Items, x => Assert.Equal("DENY", x.Decision));
        Record("denied-report-filter-page-summary", deniedTime, TimeSpan.FromSeconds(20));

        var deepQuery = new ReportQuery(fixture.DayStart, fixture.DayStart.AddDays(1), Page: 9_999, PageSize: 100);
        var (deep, deepTime) = await MeasureAsync(() => reports.QueryAsync(ReportType.DailyAccess, deepQuery, default));
        Assert.Equal(100, deep.Items.Count);
        Assert.True(deep.Items.Zip(deep.Items.Skip(1)).All(x => x.First.Timestamp >= x.Second.Timestamp));
        Record("report-deep-offset-page-9999", deepTime, TimeSpan.FromSeconds(30));

        var chunks = 0;
        var rows = 0;
        var started = Stopwatch.GetTimestamp();
        await foreach (var batch in reports.StreamBatchesAsync(ReportType.DailyAccess,
                           new ReportQuery(fixture.DayStart, fixture.DayStart.AddDays(1)), 1_000, default))
        {
            chunks++;
            rows += batch.Count;
            SampleMemory();
            if (chunks == 3) break;
        }
        Assert.Equal(3_000, rows);
        Record("report-export-first-3x1000", Stopwatch.GetElapsedTime(started), TimeSpan.FromSeconds(20));
    }

    private static async Task VerifyCountsAsync(LargeDatasetFixture fixture)
    {
        await using var db = fixture.CreateContext();
        Assert.Equal(LargeDatasetFixture.StudentCount, await db.Students.CountAsync());
        Assert.Equal(LargeDatasetFixture.StudentCount, await db.StudentCards.CountAsync());
        Assert.Equal(LargeDatasetFixture.StudentCount, await db.MealEntitlements.CountAsync());
        Assert.Equal(LargeDatasetFixture.AccessLogCount, await db.AccessLogs.CountAsync());
    }

    private async Task VerifyPlansAsync(LargeDatasetFixture fixture)
    {
        var plans = new Dictionary<string, IReadOnlyList<string>>
        {
            ["card"] = await fixture.ExplainAsync("SELECT StudentId FROM student_cards WHERE card_number = $value", ("$value", LargeDatasetFixture.TargetCard)),
            ["student"] = await fixture.ExplainAsync("SELECT Id FROM students WHERE student_no = $value", ("$value", LargeDatasetFixture.TargetStudentNo)),
            ["tracking"] = await fixture.ExplainAsync("SELECT OperationId FROM access_logs WHERE julianday(Timestamp) >= julianday($start) ORDER BY julianday(Timestamp) DESC, OperationId DESC LIMIT 100", ("$start", fixture.DayStart)),
            ["denied-report"] = await fixture.ExplainAsync("SELECT Id FROM access_logs WHERE Decision = 'DENY' AND julianday(Timestamp) >= julianday($start) ORDER BY julianday(Timestamp) DESC, Id LIMIT 100", ("$start", fixture.DayStart)),
            ["entitlements"] = await fixture.ExplainAsync("SELECT StudentId FROM meal_entitlements WHERE EntitlementDate = $date AND Status = 'Active'", ("$date", fixture.Date))
        };
        AssertPlan(plans["card"], "ix_student_cards_card_number");
        AssertPlan(plans["student"], "ix_students_student_no");
        AssertPlan(plans["tracking"], "ix_access_logs_instant_operation");
        AssertPlan(plans["denied-report"], "ix_access_logs_decision_instant_id");
        AssertPlan(plans["entitlements"], "ix_meal_entitlements_date_status_student");
        foreach (var plan in plans) output.WriteLine("plan-{0}: {1}", plan.Key, string.Join(" | ", plan.Value));
    }

    private static void AssertPlan(IEnumerable<string> plan, string index) =>
        Assert.Contains(plan, x => x.Contains(index, StringComparison.OrdinalIgnoreCase));

    private void Record(string name, TimeSpan actual, TimeSpan limit)
    {
        timings[name] = (actual, limit);
        SampleMemory();
        output.WriteLine("{0}={1:N1} ms (limit {2:N0} ms)", name, actual.TotalMilliseconds, limit.TotalMilliseconds);
    }

    private void SampleMemory() => peakManagedBytes = Math.Max(peakManagedBytes, GC.GetTotalMemory(false));

    private static async Task<(T Value, TimeSpan Elapsed)> MeasureAsync<T>(Func<Task<T>> action)
    {
        var started = Stopwatch.GetTimestamp();
        var value = await action();
        return (value, Stopwatch.GetElapsedTime(started));
    }
}
