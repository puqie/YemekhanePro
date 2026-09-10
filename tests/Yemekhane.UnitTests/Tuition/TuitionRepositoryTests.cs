using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Tuition;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Tuition;

namespace Yemekhane.UnitTests.Tuition;

/// <summary>
/// Ucret planinin kaliciligi gercek SQLite ile dogrulanir: sinif plani o siniftaki herkese
/// taksit uretir, ogrenci plani sinifinkini ezer, tahsilat taksite islenir ve odemesi olan
/// plan silinmez (pasife alinir) ki kasa gecmisi kaybolmasin.
/// </summary>
public sealed class TuitionRepositoryTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private static readonly DateOnly Today = new(2026, 12, 1);
    private readonly Guid actor = Guid.NewGuid();

    public TuitionRepositoryTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>
    /// AYNI taksite AYRI iki tahsilat islenirse IKISI DE sayilmalidir.
    ///
    /// <para>
    /// Once "installment.PaidCents += cents" yaziliyordu: iki kasiyer ayni taksite ayri
    /// tahsilat islerse ikisi de PaidCents=0 okur ve ikincisi birincinin tutarini EZER.
    /// Benzersiz indeks bunu YAKALAMAZ (farkli IncomeTransactionId), Version alani da yok.
    /// TuitionPayment satirlari dogru kalir ama borc yanlis kapanir -- veliden alinan para
    /// taksitte hic gorunmez.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TwoSeparatePaymentsOnTheSameInstallmentBothCount()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "1001");
        var plan = await SaveAsync(ClassPlan(classId));
        var installment = plan.Installments.OrderBy(x => x.DueOn).First();

        await Repository().ApplyPaymentAsync(
            new ApplyTuitionPaymentRequest(installment.Id, AddIncome(studentId, 3_000m), 3_000m), Today, actor, default);
        var second = await Repository().ApplyPaymentAsync(
            new ApplyTuitionPaymentRequest(installment.Id, AddIncome(studentId, 1_800m), 1_800m), Today, actor, default);

        // 3.000 + 1.800 = 4.800: ilk odeme EZILMEMELI.
        Assert.Equal(4_800m, second.Paid);
        Assert.Equal(0m, second.Remaining);
        Assert.Equal(TuitionInstallmentStatuses.Paid, second.Status);
    }

    /// <summary>Ayni anda islenen iki odemede de toplam dogru kalmalidir.</summary>
    [Fact]
    public async Task ConcurrentPaymentsDoNotOverwriteEachOther()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "1001");
        var plan = await SaveAsync(ClassPlan(classId));
        var installment = plan.Installments.OrderBy(x => x.DueOn).First();
        var firstIncome = AddIncome(studentId, 2_000m);
        var secondIncome = AddIncome(studentId, 1_500m);

        // GERCEK YARIS: iki repository taksiti AYNI ANDA, ikisi de PaidCents=0 iken okur.
        // Sirali cagrilar bu hatayi GORMEZ -- ikinci cagri zaten taze deger okur.
        // AYRI DbContext'ler: ayni "db" paylasilirsa EF ikinci okumada onbellekteki
        // nesneyi verir ve yaris hic olusmaz -- test sessizce gecerdi.
        await using var firstContext = NewContext();
        await using var secondContext = NewContext();
        var firstRepository = new EfTuitionRepository(firstContext, TimeProvider.System);
        var secondRepository = new EfTuitionRepository(secondContext, TimeProvider.System);
        var firstRequest = new ApplyTuitionPaymentRequest(installment.Id, firstIncome, 2_000m);
        var secondRequest = new ApplyTuitionPaymentRequest(installment.Id, secondIncome, 1_500m);

        // IKI baglam da taksiti YAZMADAN ONCE okur: gercek yaris budur. Sirali
        // await edilseydi ikinci okuma birincinin sonucunu gorurdu ve hata gizlenirdi.
        _ = secondContext.TuitionInstallments.AsTracking().Single(x => x.Id == installment.Id);
        await firstRepository.ApplyPaymentAsync(firstRequest, Today, actor, default);
        await secondRepository.ApplyPaymentAsync(secondRequest, Today, actor, default);

        var reloaded = await Repository().GetAsync(plan.Id, Today, default);
        Assert.Equal(3_500m, reloaded!.TotalPaid);
    }

    private EfTuitionRepository Repository() => new(db, TimeProvider.System);

    /// <summary>Ayni veritabanina AYRI baglam: es zamanli kullaniciyi taklit eder.</summary>
    private YemekhaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);

    private Guid AddClass(string name = "Anasınıfı A", string kind = ClassKinds.Preschool)
    {
        var row = new SchoolClass { Name = name, Kind = kind };
        db.Add(row); db.SaveChanges(); return row.Id;
    }

    private Guid AddStudent(Guid? classId, string no, bool active = true)
    {
        var row = new Student { StudentNo = no, FirstName = "Ad" + no, LastName = "Soyad", ClassId = classId, IsActive = active };
        db.Add(row); db.SaveChanges(); return row.Id;
    }

    private Guid AddIncome(Guid studentId, decimal amount)
    {
        var type = db.Set<IncomeType>().FirstOrDefault();
        if (type is null) { type = new IncomeType { Name = "Anasınıfı Ücreti" }; db.Add(type); db.SaveChanges(); }
        var row = new IncomeTransaction
        {
            OperationId = Guid.NewGuid(), StudentId = studentId, IncomeTypeId = type.Id,
            TransactionAt = DateTimeOffset.UtcNow, Amount = amount, CreatedBy = actor
        };
        db.Add(row); db.SaveChanges(); return row.Id;
    }

    private static SaveTuitionPlanRequest ClassPlan(Guid classId, decimal amount = 48_000m, int count = 10) =>
        new(TuitionPlanKinds.Installment, "2026-2027", amount, classId, null, 0m, count, 5, new DateOnly(2026, 10, 1));

    private Task<TuitionPlanDetails> SaveAsync(SaveTuitionPlanRequest request) =>
        Repository().SaveAsync(request, TuitionSchedule.Build(TuitionService.Validate(request)), Today, actor, default);

    [Fact]
    public async Task ClassPlanCreatesInstallmentsForEveryActiveStudentInThatClass()
    {
        var classId = AddClass();
        AddStudent(classId, "1001");
        AddStudent(classId, "1002");
        AddStudent(classId, "1003", active: false);
        AddStudent(AddClass("5/A", ClassKinds.Normal), "2001");

        var plan = await SaveAsync(ClassPlan(classId));

        Assert.Equal(TuitionScopes.Class, plan.Scope);
        Assert.Equal("Anasınıfı A", plan.ClassName);
        // 2 aktif ogrenci x 10 taksit; pasif ogrenci ve baska sinif dahil degil.
        Assert.Equal(20, plan.Installments.Count);
        Assert.Equal(2, await db.TuitionInstallments.Select(x => x.StudentId).Distinct().CountAsync());
        Assert.Equal(96_000m, plan.TotalDue);
        Assert.Equal(96_000m, plan.Outstanding);
    }

    [Fact]
    public async Task StudentOwnPlanOverridesTheClassPlan()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "1001");
        await SaveAsync(ClassPlan(classId));

        var inherited = await Repository().ForStudentAsync(studentId, Today, default);
        Assert.True(inherited!.Inherited);
        Assert.Equal(48_000m, inherited.Plan!.Amount);

        await SaveAsync(new SaveTuitionPlanRequest(TuitionPlanKinds.Installment, "2026-2027", 24_000m,
            null, studentId, 0m, 10, 5, new DateOnly(2026, 10, 1), "Kardeş indirimi"));

        var own = await Repository().ForStudentAsync(studentId, Today, default);
        Assert.False(own!.Inherited);
        Assert.Equal(24_000m, own.Plan!.Amount);
        Assert.Equal("Kardeş indirimi", own.Plan.Note);
        Assert.Equal(TuitionScopes.Student, own.Plan.Scope);
    }

    /// <summary>Sinif planinda ogrenciye bakildiginda yalnizca o ogrencinin taksitleri gorunur.</summary>
    [Fact]
    public async Task InheritedPlanShowsOnlyThatStudentsInstallments()
    {
        var classId = AddClass();
        var first = AddStudent(classId, "1001");
        AddStudent(classId, "1002");
        await SaveAsync(ClassPlan(classId));

        var summary = await Repository().ForStudentAsync(first, Today, default);

        Assert.Equal(10, summary!.Plan!.Installments.Count);
        Assert.All(summary.Plan.Installments, row => Assert.Equal(first, row.StudentId));
        Assert.Equal(48_000m, summary.Plan.TotalDue);
    }

    [Fact]
    public async Task PaymentReducesTheOutstandingDebt()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "1001");
        var plan = await SaveAsync(ClassPlan(classId));
        var installment = plan.Installments.OrderBy(x => x.DueOn).First();

        var updated = await Repository().ApplyPaymentAsync(
            new ApplyTuitionPaymentRequest(installment.Id, AddIncome(studentId, 4_800m), 4_800m), Today, actor, default);

        Assert.Equal(4_800m, updated.Paid);
        Assert.Equal(0m, updated.Remaining);
        Assert.Equal(TuitionInstallmentStatuses.Paid, updated.Status);
        Assert.Equal("Ödendi", updated.StatusLabel);

        var reloaded = await Repository().GetAsync(plan.Id, Today, default);
        Assert.Equal(4_800m, reloaded!.TotalPaid);
        Assert.Equal(43_200m, reloaded.Outstanding);
    }

    [Fact]
    public async Task PartialPaymentIsTrackedAndOverdueIsReported()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "1001");
        var plan = await SaveAsync(ClassPlan(classId));
        var first = plan.Installments.OrderBy(x => x.DueOn).First();

        await Repository().ApplyPaymentAsync(new ApplyTuitionPaymentRequest(first.Id, AddIncome(studentId, 1_000m), 1_000m),
            Today, actor, default);

        // 05.10 ve 05.11 vadesi gecti (bugun 01.12): ilki kismi odendi, ikincisi hic odenmedi.
        var reloaded = await Repository().GetAsync(plan.Id, Today, default);
        var overdue = reloaded!.Installments.Where(x => x.Status == TuitionInstallmentStatuses.Overdue).ToList();
        Assert.Equal(2, overdue.Count);
        Assert.Equal(8_600m, reloaded.OverdueAmount);
    }

    /// <summary>Ayni tahsilat ayni taksite iki kez sayilirsa borc yanlis kapanir.</summary>
    [Fact]
    public async Task SameIncomeCannotBeAppliedTwiceToTheSameInstallment()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "1001");
        var plan = await SaveAsync(ClassPlan(classId));
        var installment = plan.Installments[0];
        var incomeId = AddIncome(studentId, 4_800m);
        await Repository().ApplyPaymentAsync(new ApplyTuitionPaymentRequest(installment.Id, incomeId, 4_800m), Today, actor, default);

        await Assert.ThrowsAsync<EntityConflictException>(() => Repository()
            .ApplyPaymentAsync(new ApplyTuitionPaymentRequest(installment.Id, incomeId, 4_800m), Today, actor, default));
    }

    [Fact]
    public async Task UnknownInstallmentOrIncomeIsRejected()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "1001");
        var plan = await SaveAsync(ClassPlan(classId));

        await Assert.ThrowsAsync<EntityNotFoundException>(() => Repository()
            .ApplyPaymentAsync(new ApplyTuitionPaymentRequest(Guid.NewGuid(), AddIncome(studentId, 10m), 10m), Today, actor, default));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => Repository()
            .ApplyPaymentAsync(new ApplyTuitionPaymentRequest(plan.Installments[0].Id, Guid.NewGuid(), 10m), Today, actor, default));
    }

    /// <summary>Plan duzeltilince odenmis taksit KORUNUR; yoksa tahsilat gecmisi silinir.</summary>
    [Fact]
    public async Task ResavingAPlanKeepsPaidInstallments()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "1001");
        var plan = await SaveAsync(ClassPlan(classId));
        var paid = plan.Installments.OrderBy(x => x.DueOn).First();
        await Repository().ApplyPaymentAsync(new ApplyTuitionPaymentRequest(paid.Id, AddIncome(studentId, 4_800m), 4_800m),
            Today, actor, default);

        var updated = await SaveAsync(ClassPlan(classId, amount: 60_000m, count: 12));

        Assert.Equal(plan.Id, updated.Id);
        var survivor = updated.Installments.SingleOrDefault(x => x.Id == paid.Id);
        Assert.NotNull(survivor);
        Assert.Equal(4_800m, survivor!.Paid);
        Assert.Equal(4_800m, updated.TotalPaid);
    }

    [Fact]
    public async Task PlanWithPaymentsIsDeactivatedInsteadOfDeleted()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "1001");
        var plan = await SaveAsync(ClassPlan(classId));
        await Repository().ApplyPaymentAsync(
            new ApplyTuitionPaymentRequest(plan.Installments[0].Id, AddIncome(studentId, 100m), 100m), Today, actor, default);

        Assert.True(await Repository().DeleteAsync(plan.Id, actor, default));

        var reloaded = await Repository().GetAsync(plan.Id, Today, default);
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.IsActive);
        Assert.True(await db.TuitionPayments.AnyAsync());
    }

    [Fact]
    public async Task PlanWithoutPaymentsIsRemovedWithItsInstallments()
    {
        var classId = AddClass();
        AddStudent(classId, "1001");
        var plan = await SaveAsync(ClassPlan(classId));

        Assert.True(await Repository().DeleteAsync(plan.Id, actor, default));

        Assert.Null(await Repository().GetAsync(plan.Id, Today, default));
        Assert.False(await db.TuitionInstallments.AnyAsync());
    }

    [Fact]
    public async Task DailyPlanStoresTheRateWithoutInstallments()
    {
        var studentId = AddStudent(AddClass(), "1001");

        var plan = await SaveAsync(new SaveTuitionPlanRequest(TuitionPlanKinds.Daily, "2026-2027", 250m,
            null, studentId, 0m, 0, 1, new DateOnly(2026, 10, 1)));

        Assert.Equal(250m, plan.Amount);
        Assert.Equal("Günlük ücret", plan.KindLabel);
        Assert.Empty(plan.Installments);
        Assert.Equal(0m, plan.Outstanding);
    }

    [Fact]
    public async Task ListFiltersByClassAndPeriod()
    {
        var preschool = AddClass();
        AddStudent(preschool, "1001");
        var other = AddClass("Anasınıfı B");
        AddStudent(other, "1002");
        await SaveAsync(ClassPlan(preschool));
        await SaveAsync(ClassPlan(other, amount: 36_000m));

        var all = await Repository().ListAsync(new TuitionPlanFilter(Period: "2026-2027"), Today, default);
        var filtered = await Repository().ListAsync(new TuitionPlanFilter(ClassId: other), Today, default);

        Assert.Equal(2, all.TotalCount);
        Assert.Single(filtered.Items);
        Assert.Equal(36_000m, filtered.Items[0].Amount);
    }
}
