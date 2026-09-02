using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Yemekhane.Application.Common;
using Yemekhane.Application.Reports;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Reports;
using Yemekhane.Reports;

namespace Yemekhane.UnitTests.Reports;

public sealed class ReportServiceTests
{
    public static TheoryData<ReportType> AllReports => new(Enum.GetValues<ReportType>());

    [Theory]
    [MemberData(nameof(AllReports))]
    public async Task EveryReportExecutesAsServerQuery(ReportType type)
    {
        await using var fixture = await ReportFixture.CreateAsync();

        var result = await fixture.Service.QueryAsync(type, new ReportQuery());

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Summary.TotalRecords);
    }


    public static TheoryData<ReportType, string> AllTypesAndFilters =>
        Build(["cardNo", "class", "department", "section", "job", "mealType", "device"]);

    // ReportService.SortColumns allowlist'inin tamami; hepsi API'den erisilebilir.
    public static TheoryData<ReportType, string> AllTypesAndSorts =>
        Build(["timestamp", "studentNo", "cardNo", "firstName", "lastName", "class", "department",
            "section", "job", "mealType", "device", "decision", "status", "mealCount", "amount"]);

    private static TheoryData<ReportType, string> Build(string[] keys)
    {
        var data = new TheoryData<ReportType, string>();
        foreach (var type in Enum.GetValues<ReportType>())
        foreach (var key in keys)
            data.Add(type, key);
        return data;
    }

    /// <summary>
    /// Projeksiyonda sabit atanan sutunlar (Decision = null, AmountCents = 0L, hic atanmayan Device)
    /// EF'in filtreyi SQL'e cevirmesini engelliyordu: sorgu derlenir ama calisma aninda patlardi.
    /// Her rapor turu her filtreyle sunucuda calisabilmelidir.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllTypesAndFilters))]
    public async Task EveryReportTypeSupportsEveryTextFilter(ReportType type, string filter)
    {
        await using var fixture = await ReportFixture.CreateAsync();
        var query = filter switch
        {
            "cardNo" => new ReportQuery(CardNo: "x"),
            "class" => new ReportQuery(Class: "x"),
            "department" => new ReportQuery(Department: "x"),
            "section" => new ReportQuery(Section: "x"),
            "job" => new ReportQuery(Job: "x"),
            "mealType" => new ReportQuery(MealType: "x"),
            _ => new ReportQuery(Device: "x")
        };

        var result = await fixture.Service.QueryAsync(type, query);

        Assert.Empty(result.Items);
    }

    [Theory]
    [MemberData(nameof(AllTypesAndSorts))]
    public async Task EveryReportTypeSupportsEverySortColumn(ReportType type, string sortBy)
    {
        await using var fixture = await ReportFixture.CreateAsync();

        var result = await fixture.Service.QueryAsync(type, new ReportQuery(SortBy: sortBy));

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task FiltersPagesAndSummarizesFromSameFilteredSet()
    {
        await using var fixture = await ReportFixture.CreateAsync();
        await fixture.SeedAccessAsync();
        var start = new DateTimeOffset(2026, 8, 31, 12, 30, 10, TimeSpan.FromHours(3));

        var first = await fixture.Service.QueryAsync(ReportType.DailyAccess,
            new ReportQuery(start, start.AddSeconds(1), "10", FirstName: "Ad", LastName: "Yıl",
                Class: "10", Department: "Fen", Section: "A", Job: "Öğrenci", MealType: "Öğ",
                Device: "Turnike", Decision: "ALLOW", Status: "ALLOW", SortBy: "cardNo",
                Page: 1, PageSize: 1));
        var second = await fixture.Service.QueryAsync(ReportType.DailyAccess,
            new ReportQuery(StudentNo: "10", Decision: "ALLOW", SortBy: "cardNo", Page: 2, PageSize: 1));
        var card = await fixture.Service.QueryAsync(ReportType.DailyAccess,
            new ReportQuery(CardNo: "0002"));

        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotEqual(first.Items[0].Id, second.Items[0].Id);
        Assert.Equal(new ReportSummary(2, 2, 0, 2, 0m), first.Summary);
        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal("0003", first.Items[0].CardNo);
        Assert.Equal(new ReportSummary(1, 0, 1, 0, 0m), card.Summary);
        Assert.Matches(@"\.\d{3}\+", first.Items[0].TimestampMilliseconds!);
    }

    [Fact]
    public async Task ProjectionIsNoTrackingAndUsesConstantQueryCount()
    {
        await using var fixture = await ReportFixture.CreateAsync();
        await fixture.SeedAccessAsync();
        fixture.Context.ChangeTracker.Clear();
        fixture.Counter.Reset();

        var result = await fixture.Service.QueryAsync(ReportType.DailyAccess,
            new ReportQuery(PageSize: 2));

        Assert.Equal(3, result.Summary.TotalRecords);
        Assert.Empty(fixture.Context.ChangeTracker.Entries());
        Assert.Equal(2, fixture.Counter.Commands);
    }

    [Fact]
    public async Task RejectsUnlistedSortAndOversizedPageBeforeRepositoryCall()
    {
        var service = new ReportService(new NeverCalledRepository());

        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.QueryAsync(ReportType.Income, new ReportQuery(SortBy: "description; drop table students")));
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            service.QueryAsync(ReportType.Income, new ReportQuery(PageSize: 201)));
    }

    [Fact]
    public async Task StreamingPipelineReturnsBoundedBatchesWithoutPagingResult()
    {
        await using var fixture = await ReportFixture.CreateAsync();
        await fixture.SeedAccessAsync();
        var batches = new List<IReadOnlyList<ReportRow>>();

        await foreach (var batch in fixture.Service.StreamBatchesAsync(
                           ReportType.DailyAccess, new ReportQuery(), batchSize: 2))
            batches.Add(batch);

        Assert.Equal([2, 1], batches.Select(x => x.Count));
    }

    [Fact]
    public async Task IncomeSummaryUsesExactFilteredCurrencyAmount()
    {
        await using var fixture = await ReportFixture.CreateAsync();
        var type = new IncomeType { Name = "Nakit" };
        fixture.Context.Add(type);
        fixture.Context.AddRange(
            Income(10.10m, false), Income(20.20m, false), Income(99.99m, true));
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.QueryAsync(ReportType.Income,
            new ReportQuery(Status: "ACTIVE", SortBy: "amount"));

        Assert.Equal(30.30m, result.Summary.Amount);
        Assert.Equal([20.20m, 10.10m], result.Items.Select(x => x.Amount));

        IncomeTransaction Income(decimal amount, bool isVoided) => new()
        {
            OperationId = Guid.NewGuid(), IncomeTypeId = type.Id, TransactionAt = DateTimeOffset.UtcNow,
            Amount = amount, CreatedBy = Guid.NewGuid(), IsVoided = isVoided
        };
    }

    /// <summary>
    /// Gunluk Kasa ile Gelir raporu birebir ayni sorguyu calistiriyordu; iki rapor turunun
    /// varligi anlamsizdi. Gunluk Kasa artik kasa defteri gibi GUN + GELIR TURU (+ iptal)
    /// kirilimidir; Gelir ise islem islem listedir. Gun siniri Istanbul'a gore alinmalidir:
    /// 2 Eylul 00:30 (+03:00) UTC'de hala 1 Eylul'dur, kasa defterinde 2 Eylul'e yazilmalidir.
    /// </summary>
    [Fact]
    public async Task DailyCashGroupsTransactionsByIstanbulDayAndIncomeType()
    {
        await using var fixture = await ReportFixture.CreateAsync();
        var monthly = new IncomeType { Name = "Aylık" };
        var daily = new IncomeType { Name = "Günlük" };
        fixture.Context.AddRange(monthly, daily);
        var day1 = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(3));
        fixture.Context.AddRange(
            Income(monthly, 100m, day1), Income(monthly, 250m, day1.AddHours(3)),
            Income(daily, 40m, day1.AddHours(5)), Income(daily, 60m, day1.AddHours(5), voided: true),
            Income(monthly, 500m, new DateTimeOffset(2026, 9, 2, 0, 30, 0, TimeSpan.FromHours(3))));
        await fixture.Context.SaveChangesAsync();

        var cash = await fixture.Service.QueryAsync(ReportType.DailyCash, new ReportQuery(Descending: false));
        var income = await fixture.Service.QueryAsync(ReportType.Income, new ReportQuery());

        Assert.Equal(5, income.Summary.TotalRecords);
        Assert.Equal(4, cash.Summary.TotalRecords);
        Assert.Equal(income.Summary.Amount, cash.Summary.Amount);
        Assert.Equal(890m, cash.Summary.Amount);
        Assert.Equal(5, cash.Summary.TotalMeals);
        var monthlyDay1 = cash.Items.Single(x => x.ReportDate == new DateOnly(2026, 9, 1) && x.Description == "Aylık");
        Assert.Equal(2, monthlyDay1.MealCount);
        Assert.Equal(350m, monthlyDay1.Amount);
        Assert.Equal("ACTIVE", monthlyDay1.Status);
        var voided = cash.Items.Single(x => x.Status == "VOIDED");
        Assert.Equal(1, voided.MealCount);
        Assert.Equal(0m, voided.Amount);
        Assert.Equal(new DateOnly(2026, 9, 2), cash.Items.Last().ReportDate);
        Assert.All(cash.Items, x => Assert.Null(x.Timestamp));

        // Tarih filtresi gruplamadan ONCE, islem zamanina uygulanir.
        var secondDay = await fixture.Service.QueryAsync(ReportType.DailyCash,
            new ReportQuery(Start: new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.FromHours(3))));
        Assert.Equal(1, secondDay.Summary.TotalRecords);
        Assert.Equal(500m, secondDay.Summary.Amount);
        var voidedOnly = await fixture.Service.QueryAsync(ReportType.DailyCash, new ReportQuery(Status: "VOIDED"));
        Assert.Single(voidedOnly.Items);

        // Disa aktarma ayni gruplu satirlari akitmalidir.
        var streamed = new List<ReportRow>();
        await foreach (var batch in fixture.Service.StreamBatchesAsync(ReportType.DailyCash, new ReportQuery()))
            streamed.AddRange(batch);
        Assert.Equal(4, streamed.Count);

        IncomeTransaction Income(IncomeType type, decimal amount, DateTimeOffset at, bool voided = false) => new()
        {
            OperationId = Guid.NewGuid(), IncomeTypeId = type.Id, TransactionAt = at, Amount = amount,
            CreatedBy = Guid.NewGuid(), IsVoided = voided, Description = "Eylül ödemesi"
        };
    }

    private sealed class ReportFixture(
        SqliteConnection connection,
        YemekhaneDbContext context,
        CommandCounter counter) : IAsyncDisposable
    {
        public YemekhaneDbContext Context { get; } = context;
        public CommandCounter Counter { get; } = counter;
        public ReportService Service { get; } = new(new EfReportRepository(context));

        public static async Task<ReportFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var counter = new CommandCounter();
            var context = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>()
                .UseSqlite(connection).AddInterceptors(counter).Options);
            await context.Database.EnsureCreatedAsync();
            return new ReportFixture(connection, context, counter);
        }

        public async Task SeedAccessAsync()
        {
            var schoolClass = new SchoolClass { Name = "10-A" };
            var department = new Department { Name = "Fen" };
            var section = new Section { Name = "A" };
            var job = new Job { Name = "Öğrenci" };
            var student = new Student
            {
                StudentNo = "1001", FirstName = "Ada", LastName = "Yılmaz", ClassId = schoolClass.Id,
                DepartmentId = department.Id, SectionId = section.Id, JobId = job.Id
            };
            var device = new Device
            {
                Name = "Ana Turnike", DeviceType = "Turnstile", ConnectionType = "TCP",
                Direction = "Entry", ConnectionStatus = "Connected"
            };
            var meal = new MealType { Name = "Öğle" };
            Context.AddRange(schoolClass, department, section, job, student, device, meal);
            var timestamp = new DateTimeOffset(2026, 8, 31, 12, 30, 10, 123, TimeSpan.FromHours(3));
            Context.AccessLogs.AddRange(
                Access("0001", "ALLOW", timestamp),
                Access("0002", "DENY", timestamp.AddMilliseconds(1)),
                Access("0003", "ALLOW", timestamp.AddMilliseconds(2)));
            await Context.SaveChangesAsync();

            AccessLog Access(string card, string decision, DateTimeOffset occurredAt) => new()
            {
                StudentId = student.Id, DeviceId = device.Id, MealTypeId = meal.Id, CardNumber = card,
                Decision = decision, Reason = decision, Direction = "Entry", ReaderSource = "Reader",
                Timestamp = occurredAt, OperationId = Guid.NewGuid()
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    public sealed class CommandCounter : DbCommandInterceptor
    {
        public int Commands { get; private set; }
        public void Reset() => Commands = 0;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class NeverCalledRepository : IReportRepository
    {
        public Task<ReportResult> QueryAsync(ReportType type, ReportQuery query, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Repository çağrılmamalıydı.");

        public async IAsyncEnumerable<IReadOnlyList<ReportRow>> StreamBatchesAsync(
            ReportType type, ReportQuery query, int batchSize,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
