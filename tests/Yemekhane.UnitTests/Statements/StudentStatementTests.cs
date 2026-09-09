using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Common;
using Yemekhane.Application.Reports;
using Yemekhane.Application.Statements;
using Yemekhane.Application.Tuition;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Statements;
using Yemekhane.Infrastructure.Tuition;
using Yemekhane.Reports;

namespace Yemekhane.UnitTests.Statements;

/// <summary>
/// Ogrenci ekstresi: veli "1-2 yil onceki odemelerimi gorebilir miyim" diye soruyor.
/// Dort defter (tahsilat, bakiye, taksit, yemek) tek belgede, tarih araligiyla birlesir;
/// yil sonu sifirlamasi mali kayitlari silmedigi icin gecmis yillar da sorgulanabilir.
/// </summary>
public sealed class StudentStatementTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private static readonly DateOnly Today = new(2026, 12, 1);
    private readonly FakeClock clock = new(new DateTimeOffset(2026, 12, 1, 9, 0, 0, TimeSpan.FromHours(3)));
    private readonly Guid actor = Guid.NewGuid();
    private Guid studentId;
    private Guid mealTypeId;
    private Guid incomeTypeId;
    private Guid deviceId;

    public StudentStatementTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        Seed();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private void Seed()
    {
        var schoolClass = new SchoolClass { Name = "Anasınıfı A", Kind = ClassKinds.Preschool };
        var section = new Section { Name = "A" };
        db.AddRange(schoolClass, section);
        var student = new Student { StudentNo = "1042", FirstName = "Ayşe", LastName = "Yılmaz", ClassId = schoolClass.Id, SectionId = section.Id };
        var parent = new Parent { StudentId = student.Id, Name = "Fatma Yılmaz", NormalizedPhone = "+905321234567" };
        var mealType = new MealType { Name = "Öğle Yemeği" };
        var incomeType = new IncomeType { Name = "Anasınıfı Ücreti" };
        var device = new Device { Name = "Turnike 1", DeviceType = "SF300", ConnectionType = "Ethernet", Direction = "Entry", ConnectionStatus = "Disconnected" };
        db.AddRange(student, parent, mealType, incomeType, device);
        db.SaveChanges();
        studentId = student.Id; mealTypeId = mealType.Id; incomeTypeId = incomeType.Id; deviceId = device.Id;
    }

    private Guid AddIncome(decimal amount, DateTimeOffset at, bool voided = false, string? description = null)
    {
        var row = new IncomeTransaction
        {
            OperationId = Guid.NewGuid(), StudentId = studentId, IncomeTypeId = incomeTypeId,
            TransactionAt = at, Amount = amount, Description = description, CreatedBy = actor, IsVoided = voided
        };
        db.Add(row); db.SaveChanges(); return row.Id;
    }

    private void AddBalance(long cents, DateTimeOffset at, string kind)
    {
        db.Add(new StudentBalanceEntry { StudentId = studentId, AmountCents = cents, Kind = kind, OccurredAt = at });
        db.SaveChanges();
    }

    private void AddMealUsage(DateTimeOffset at)
    {
        var entitlement = new MealEntitlement
        {
            StudentId = studentId, MealTypeId = mealTypeId, EntitlementDate = DateOnly.FromDateTime(at.Date),
            Quantity = 1, Status = "Active"
        };
        var log = new AccessLog
        {
            OperationId = Guid.NewGuid(), StudentId = studentId, DeviceId = deviceId, MealTypeId = mealTypeId,
            Timestamp = at, Decision = "ALLOW", Reason = "OK",
            CardNumber = "8350042", Direction = "Entry", ReaderSource = "Test"
        };
        db.AddRange(entitlement, log);
        db.SaveChanges();
        db.Add(new MealUsage { EntitlementId = entitlement.Id, StudentId = studentId, MealTypeId = mealTypeId, AccessLogId = log.Id, UsedAt = at });
        db.SaveChanges();
    }

    private StudentStatementService Service() => new(new EfStudentStatementRepository(db), clock);

    private Task<StudentStatement> BuildAsync(DateOnly from, DateOnly to) =>
        Service().BuildAsync(new StudentStatementQuery(studentId, from, to));

    [Fact]
    public async Task StatementCarriesTheStudentIdentityAndParent()
    {
        var statement = await BuildAsync(new DateOnly(2026, 9, 1), new DateOnly(2027, 8, 31));

        Assert.Equal("1042", statement.StudentNo);
        Assert.Equal("Ayşe Yılmaz", statement.StudentName);
        Assert.Equal("Anasınıfı A", statement.ClassName);
        Assert.Equal("A", statement.SectionName);
        Assert.Equal("Fatma Yılmaz", statement.ParentName);
    }

    [Fact]
    public async Task PaymentsAppearWithTheirIncomeTypeAndVoidState()
    {
        AddIncome(4_800m, new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(3)), description: "1. taksit");
        AddIncome(1_000m, new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.FromHours(3)), voided: true);

        var statement = await BuildAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        var payments = statement.Lines.Where(x => x.Section == StatementSections.Payment).ToList();
        Assert.Equal(2, payments.Count);
        Assert.Equal("Anasınıfı Ücreti", payments[0].Title);
        Assert.Equal("1. taksit", payments[0].Detail);
        Assert.Equal(4_800m, statement.TotalPaid);
        Assert.Equal(1_000m, statement.TotalVoided);
        // Iptal edilen tahsilat bolum toplamina girmez ama satir olarak gorunur.
        Assert.True(payments.Single(x => x.IsCancelled).IsCancelled);
        Assert.Equal(4_800m, statement.Sections.Single(x => x.Section == StatementSections.Payment).Total);
    }

    /// <summary>Aralik disi kayit ekstreye girmemeli; veli yalnizca sordugu donemi gormeli.</summary>
    [Fact]
    public async Task OutOfRangeRecordsAreExcluded()
    {
        AddIncome(500m, new DateTimeOffset(2025, 5, 1, 10, 0, 0, TimeSpan.FromHours(3)));
        AddIncome(700m, new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(3)));

        var statement = await BuildAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        Assert.Single(statement.Lines, x => x.Section == StatementSections.Payment);
        Assert.Equal(700m, statement.TotalPaid);
    }

    /// <summary>Gecmis yil sorgulanabilmeli: yil sonu sifirlamasi mali kayitlari silmez.</summary>
    [Fact]
    public async Task LastYearCanStillBeQueried()
    {
        AddIncome(3_000m, new DateTimeOffset(2024, 11, 15, 10, 0, 0, TimeSpan.FromHours(3)));

        var statement = await BuildAsync(new DateOnly(2024, 9, 1), new DateOnly(2025, 8, 31));

        Assert.Equal(3_000m, statement.TotalPaid);
        Assert.Single(statement.Lines);
    }

    [Fact]
    public async Task BalanceMovementsAreSplitIntoTopUpsAndSpending()
    {
        AddBalance(50_000, new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(3)), StudentBalanceEntryKinds.TopUp);
        AddBalance(-25_000, new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.FromHours(3)), StudentBalanceEntryKinds.Deduction);

        var statement = await BuildAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        Assert.Equal(500m, statement.BalanceTopUps);
        Assert.Equal(250m, statement.BalanceSpent);
        Assert.Equal(250m, statement.CurrentBalance);
        var balanceLines = statement.Lines.Where(x => x.Section == StatementSections.Balance).ToList();
        Assert.Equal("Bakiye yükleme", balanceLines[0].Title);
        Assert.Equal("Öğün düşümü", balanceLines[1].Title);
    }

    /// <summary>Guncel bakiye TUM defterden gelir; aralik disi yukleme de cebindeki paradir.</summary>
    [Fact]
    public async Task CurrentBalanceIncludesEntriesOutsideTheRange()
    {
        AddBalance(100_000, new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.FromHours(3)), StudentBalanceEntryKinds.TopUp);

        var statement = await BuildAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        Assert.Equal(0m, statement.BalanceTopUps);
        Assert.Equal(1_000m, statement.CurrentBalance);
    }

    [Fact]
    public async Task TuitionInstallmentsAndDebtAppear()
    {
        var request = new SaveTuitionPlanRequest(TuitionPlanKinds.Installment, "2026-2027", 48_000m,
            null, studentId, 0m, 10, 5, new DateOnly(2026, 10, 1));
        var repository = new EfTuitionRepository(db, clock);
        var plan = await repository.SaveAsync(request, TuitionSchedule.Build(TuitionService.Validate(request)), Today, actor, default);
        await repository.ApplyPaymentAsync(
            new ApplyTuitionPaymentRequest(plan.Installments[0].Id, AddIncome(4_800m, new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(3))), 4_800m),
            Today, actor, default);

        var statement = await BuildAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31));

        Assert.Equal(48_000m, statement.TuitionDue);
        Assert.Equal(4_800m, statement.TuitionPaid);
        Assert.Equal(43_200m, statement.TuitionOutstanding);
        // 05.10 odendi, 05.11 odenmedi (bugun 01.12) -> bir taksit gecikmis.
        Assert.Equal(4_800m, statement.TuitionOverdue);
        var tuitionLines = statement.Lines.Where(x => x.Section == StatementSections.Tuition).ToList();
        Assert.Equal(3, tuitionLines.Count);
        Assert.Equal("1. taksit", tuitionLines[0].Title);
        Assert.Equal("Ödendi", tuitionLines[0].Status);
        Assert.Equal("Gecikmiş", tuitionLines[1].Status);
    }

    [Fact]
    public async Task MealUsageIsCountedNotPriced()
    {
        AddMealUsage(new DateTimeOffset(2026, 10, 6, 12, 15, 0, TimeSpan.FromHours(3)));
        AddMealUsage(new DateTimeOffset(2026, 10, 7, 12, 20, 0, TimeSpan.FromHours(3)));

        var statement = await BuildAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        Assert.Equal(2, statement.MealsUsed);
        var meals = statement.Lines.Where(x => x.Section == StatementSections.Meal).ToList();
        Assert.All(meals, line => { Assert.Equal("Öğle Yemeği", line.Title); Assert.Equal(0m, line.Amount); Assert.Equal(1, line.Quantity); });
        Assert.Equal(2m, statement.Sections.Single(x => x.Section == StatementSections.Meal).Total);
    }

    [Fact]
    public async Task LinesAreOrderedByDateAcrossSections()
    {
        AddIncome(100m, new DateTimeOffset(2026, 10, 10, 10, 0, 0, TimeSpan.FromHours(3)));
        AddBalance(20_000, new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(3)), StudentBalanceEntryKinds.TopUp);
        AddMealUsage(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(3)));

        var statement = await BuildAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));

        Assert.Equal([StatementSections.Balance, StatementSections.Meal, StatementSections.Payment],
            statement.Lines.Select(x => x.Section).ToArray());
    }

    [Fact]
    public async Task ReversedRangeIsRejected() =>
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            BuildAsync(new DateOnly(2026, 10, 31), new DateOnly(2026, 10, 1)));

    [Fact]
    public async Task ExcessiveRangeIsRejected() =>
        await Assert.ThrowsAsync<RequestValidationException>(() =>
            BuildAsync(new DateOnly(2020, 1, 1), new DateOnly(2026, 12, 31)));

    [Fact]
    public async Task UnknownStudentIsReported() =>
        await Assert.ThrowsAsync<EntityNotFoundException>(() => Service()
            .BuildAsync(new StudentStatementQuery(Guid.NewGuid(), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31))));

    /// <summary>Varsayilan aralik icinde bulunulan egitim yilidir (1 Eylul - 31 Agustos).</summary>
    [Fact]
    public void DefaultRangeIsTheCurrentSchoolYear()
    {
        var (from, to) = Service().DefaultRange();

        Assert.Equal(new DateOnly(2026, 9, 1), from);
        Assert.Equal(new DateOnly(2027, 8, 31), to);
    }

    [Fact]
    public void DefaultRangeBeforeSeptemberUsesThePreviousYear()
    {
        var service = new StudentStatementService(new EfStudentStatementRepository(db),
            new FakeClock(new DateTimeOffset(2027, 3, 15, 9, 0, 0, TimeSpan.FromHours(3))));

        var (from, to) = service.DefaultRange();

        Assert.Equal(new DateOnly(2026, 9, 1), from);
        Assert.Equal(new DateOnly(2027, 8, 31), to);
    }

    /// <summary>PDF gercekten uretilmeli ve Turkce karakter tasiyabilmeli.</summary>
    [Fact]
    public async Task PdfIsGenerated()
    {
        AddIncome(4_800m, new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(3)), description: "Peşin ödeme");
        AddBalance(50_000, new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(3)), StudentBalanceEntryKinds.TopUp);
        AddMealUsage(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.FromHours(3)));
        var statement = await BuildAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        var service = new StudentStatementPdfService(
            Options.Create(new ReportPdfOptions { SchoolName = "Şehit Öğretmen İlkokulu" }), clock);

        using var stream = new MemoryStream();
        await service.GenerateAsync(statement, stream);

        Assert.True(stream.Length > 1000);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(stream.ToArray(), 0, 4));
    }
}
