using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Income;
using Yemekhane.Application.Tuition;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Income;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Tuition;

namespace Yemekhane.UnitTests.Tuition;

/// <summary>
/// SAHA: anasinifi velisi her ay kasadan odeme yapiyor ama hicbir yer bu parayi taksite
/// yazmiyordu; okul "kacinci taksiti odedi, kac kez odedi" sorusuna cevap alamiyordu.
///
/// <para>
/// Kural: gelir turu "taksite sayilir" isaretliyse ogrenciye bagli tahsilat, gecerli planin
/// ODENMEMIS taksitlerine vade sirasiyla dagitilir; iptal geri alir; eski kayitlar mutabakatla
/// islenir; Anasinifi ekrani ilerlemeyi "3/10" olarak gosterir. Gercek SQLite + migration.
/// </para>
/// </summary>
public sealed class TuitionIncomeAllocationTests : IAsyncLifetime
{
    private static readonly DateOnly Today = new(2026, 12, 15);
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly Guid actor = Guid.NewGuid();
    private YemekhaneDbContext db = null!;
    private Guid tuitionTypeId, topUpTypeId;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var tuition = new IncomeType { Name = "Anasınıfı Ücreti", CountsTowardTuition = true };
        var topUp = new IncomeType { Name = "Bakiye Yükleme" };
        db.AddRange(tuition, topUp);
        await db.SaveChangesAsync();
        tuitionTypeId = tuition.Id; topUpTypeId = topUp.Id;
    }

    public async Task DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    [Fact]
    public async Task FlaggedIncomeFillsInstallmentsInDueOrderAndReportsWhichOnes()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "3001");
        await SavePlanAsync(classId, 48_000m, 10);

        var first = await RecordAsync(studentId, 4_800m, tuitionTypeId);
        var second = await RecordAsync(studentId, 7_200m, tuitionTypeId);

        Assert.Equal("1. taksite sayıldı (1/10 ödendi).", first.TuitionNote);
        Assert.Null(first.Warning);
        Assert.Equal("2. ve 3. taksite sayıldı (2/10 ödendi).", second.TuitionNote);
        var rows = await Installments(studentId);
        Assert.Equal([480_000L, 480_000L, 240_000L, 0L], rows.Take(4).Select(x => x.PaidCents));
        Assert.Equal(3, await db.TuitionPayments.CountAsync());

        var summary = await Tuition().ForStudentAsync(studentId, Today, default);
        Assert.Equal(12_000m, summary!.Plan!.TotalPaid);
        Assert.Equal([3, 2, 1], summary.Payments!.Select(x => x.Sequence));
        Assert.Equal("Anasınıfı Ücreti", summary.Payments![0].IncomeTypeName);
    }

    [Fact]
    public async Task UnflaggedStudentIncomeLeavesInstallmentsAlone()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "3002");
        await SavePlanAsync(classId, 10_000m, 2);

        var created = await RecordAsync(studentId, 5_000m, topUpTypeId);

        Assert.Null(created.TuitionNote);
        Assert.Null(created.Warning);
        Assert.Empty(await db.TuitionPayments.ToListAsync());
        Assert.All(await Installments(studentId), x => Assert.Equal(0, x.PaidCents));
    }

    [Fact]
    public async Task VoidingTheIncomeReopensTheInstallments()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "3003");
        await SavePlanAsync(classId, 10_000m, 2);
        var created = await RecordAsync(studentId, 7_000m, tuitionTypeId);
        Assert.Equal([500_000L, 200_000L], (await Installments(studentId)).Select(x => x.PaidCents));

        var voided = await Income().VoidAsync(created.Id, "Yanlış öğrenciye girildi", actor);

        Assert.True(voided.IsVoided);
        Assert.Contains("2 taksit yeniden borçlu", voided.Warning);
        Assert.Empty(await db.TuitionPayments.ToListAsync());
        Assert.All(await Installments(studentId), x => Assert.Equal(0, x.PaidCents));
        var overview = await Tuition().KindergartenAsync(Today, default);
        Assert.Equal("0/2", overview.Students.Single().Progress);
        // Iptal edilen tahsilat mutabakatta yeniden islenmez.
        Assert.Equal(0, overview.UnappliedIncomeCount);
    }

    [Fact]
    public async Task StudentWithoutAPlanStillGetsTheIncomeRecordedButIsWarned()
    {
        var studentId = AddStudent(AddClass(), "3004");

        var created = await RecordAsync(studentId, 1_000m, tuitionTypeId);

        Assert.Null(created.TuitionNote);
        Assert.Contains("taksite sayılmadı", created.Warning);
        Assert.False(created.IsVoided);
    }

    [Fact]
    public async Task OverpaymentFillsEverythingAndLeavesTheRestInCash()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "3005");
        await SavePlanAsync(classId, 1_000m, 2);

        var created = await RecordAsync(studentId, 1_500m, tuitionTypeId);

        Assert.Equal("1. ve 2. taksite sayıldı (2/2 ödendi). 500,00 ₺ fazla ödeme taksitlere sığmadı.", created.TuitionNote);
        Assert.Contains("500,00 ₺ fazla ödeme", created.Warning);
        Assert.Equal([50_000L, 50_000L], (await Installments(studentId)).Select(x => x.PaidCents));
    }

    [Fact]
    public async Task ReconcileAppliesOlderUnappliedIncomesInDateOrderAndIsIdempotent()
    {
        var classId = AddClass();
        var studentId = AddStudent(classId, "3006");
        await SavePlanAsync(classId, 30_000m, 3);
        // Bu surumden onceki kayitlar: dogrudan tabloya yazilmis, taksite islenmemis.
        AddRawIncome(studentId, 10_000m, new DateTimeOffset(2026, 11, 5, 10, 0, 0, TimeSpan.Zero));
        AddRawIncome(studentId, 4_000m, new DateTimeOffset(2026, 10, 5, 10, 0, 0, TimeSpan.Zero));
        AddRawIncome(studentId, 999m, new DateTimeOffset(2026, 10, 6, 10, 0, 0, TimeSpan.Zero), topUpTypeId);
        Assert.Equal(2, (await Tuition().KindergartenAsync(Today, default)).UnappliedIncomeCount);

        var first = await Tuition().ReconcileAsync(Today, actor, default);
        var second = await Tuition().ReconcileAsync(Today, actor, default);

        Assert.Equal(new TuitionReconcileResult(2, 2, 0), first);
        Assert.Equal(new TuitionReconcileResult(0, 0, 0), second);
        // Ekim tahsilati (4.000) once islendi: 1. taksit 4.000; Kasim (10.000) 1. taksiti tamamlar, 2. taksite 4.000.
        Assert.Equal([1_000_000L, 400_000L, 0L], (await Installments(studentId)).Select(x => x.PaidCents));
        var overview = await Tuition().KindergartenAsync(Today, default);
        var row = overview.Students.Single();
        Assert.Equal("1/3", row.Progress);
        Assert.Equal(2, row.PaymentCount);
        Assert.Equal(0, overview.UnappliedIncomeCount);
    }

    [Fact]
    public async Task KindergartenOverviewListsPreschoolStudentsWithProgressAndOverdue()
    {
        var preschool = AddClass("Anasınıfı A");
        var normal = AddClass("5/A", ClassKinds.Normal);
        var paying = AddStudent(preschool, "3007");
        var noPlanClass = AddClass("Anasınıfı B");
        var withoutPlan = AddStudent(noPlanClass, "3008");
        AddStudent(normal, "3009");
        AddStudent(preschool, "3010", active: false);
        await SavePlanAsync(preschool, 12_000m, 12);
        await RecordAsync(paying, 2_000m, tuitionTypeId);

        var overview = await Tuition().KindergartenAsync(Today, default);

        Assert.True(overview.HasTuitionIncomeType);
        Assert.Equal(["3007", "3008"], overview.Students.Select(x => x.StudentNo).OrderBy(x => x));
        var row = overview.Students.Single(x => x.StudentNo == "3007");
        Assert.Equal("2/12", row.Progress);
        Assert.Equal(1, row.PaymentCount);
        Assert.Equal(12_000m, row.TotalDue);
        Assert.Equal(2_000m, row.TotalPaid);
        Assert.Equal(10_000m, row.Outstanding);
        // Plan 1 Ekim'den, vade ayin 5'i: Ekim+Kasim odendi, 15 Aralik itibariyla 3. taksit (5 Aralik) gecikmis.
        Assert.Equal(1, row.OverdueCount);
        Assert.Equal(1_000m, row.OverdueAmount);
        Assert.Equal(new DateOnly(2026, 12, 5), row.NextDueOn);
        Assert.NotNull(row.LastPaidAt);
        var planless = overview.Students.Single(x => x.StudentNo == "3008");
        Assert.False(planless.HasPlan);
        Assert.Equal("Plan yok", planless.Progress);
    }

    [Fact]
    public async Task OverviewIsEmptyWhenNoPreschoolStudentOrPlanExists()
    {
        AddStudent(AddClass("5/A", ClassKinds.Normal), "3011");

        var overview = await Tuition().KindergartenAsync(Today, default);

        Assert.Empty(overview.Students);
    }

    private EfTuitionRepository Tuition() => new(db, TimeProvider.System);
    private IncomeService Income() => new(new EfIncomeRepository(db, TimeProvider.System));

    private Task<IncomeTransactionDetails> RecordAsync(Guid studentId, decimal amount, Guid typeId) =>
        Income().RecordAsync(new CreateIncomeTransactionRequest(Guid.NewGuid(), studentId, null,
            new DateTimeOffset(2026, 12, 10, 9, 0, 0, TimeSpan.Zero), typeId, amount), actor);

    private async Task<List<TuitionInstallment>> Installments(Guid studentId) =>
        (await db.TuitionInstallments.AsNoTracking().Where(x => x.StudentId == studentId).ToListAsync())
            .OrderBy(x => x.DueOn).ThenBy(x => x.Sequence).ToList();

    private async Task SavePlanAsync(Guid classId, decimal amount, int count)
    {
        var request = new SaveTuitionPlanRequest(TuitionPlanKinds.Installment, "2026-2027", amount, classId, null, 0m, count, 5, new DateOnly(2026, 10, 1));
        await Tuition().SaveAsync(request, TuitionSchedule.Build(TuitionService.Validate(request)), Today, actor, default);
    }

    private Guid AddClass(string name = "Anasınıfı A", string kind = ClassKinds.Preschool)
    {
        var row = new SchoolClass { Name = name, Kind = kind };
        db.Add(row); db.SaveChanges(); return row.Id;
    }

    private Guid AddStudent(Guid classId, string no, bool active = true)
    {
        var row = new Student { StudentNo = no, FirstName = "Ad" + no, LastName = "Soyad", ClassId = classId, IsActive = active };
        db.Add(row); db.SaveChanges(); return row.Id;
    }

    private void AddRawIncome(Guid studentId, decimal amount, DateTimeOffset at, Guid? typeId = null)
    {
        db.Add(new IncomeTransaction
        {
            OperationId = Guid.NewGuid(), StudentId = studentId, IncomeTypeId = typeId ?? tuitionTypeId,
            TransactionAt = at, Amount = amount, CreatedBy = actor, CreatedAt = at
        });
        db.SaveChanges();
    }
}
