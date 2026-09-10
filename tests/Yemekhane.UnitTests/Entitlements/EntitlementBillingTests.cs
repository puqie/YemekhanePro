using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Entitlements;

/// <summary>
/// Hakedis ucreti: "gunluk bedel belli ama kasaya yansimiyor ve veliye SMS gitmiyordu."
/// Ucret OGRENCI BASINA ayri bir kasa islemi olur (ekstrede gorunsun, tek tek iptal
/// edilebilsin); hakedis iptal edilince tahsilat da geri alinir.
/// </summary>
public sealed class EntitlementBillingTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private readonly Guid actor = Guid.NewGuid();
    private Guid mealTypeId;

    public EntitlementBillingTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var meal = new MealType { Name = "Öğle Yemeği" };
        db.Add(meal);
        db.SaveChanges();
        mealTypeId = meal.Id;
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private EfEntitlementBillingService Service(FakeSmsLog? sms = null) =>
        new(db, TimeProvider.System, new Yemekhane.Application.Audit.AuditService(
            new Yemekhane.Infrastructure.Audit.EfAuditRepository(db, TimeProvider.System),
            new Yemekhane.Infrastructure.Audit.SystemAuditContext()), sms);

    private Guid AddStudent(string no, string? parentPhone = null)
    {
        var student = new Student { StudentNo = no, FirstName = "Ad" + no, LastName = "Soyad" };
        db.Add(student);
        if (parentPhone is not null)
            db.Add(new Parent { StudentId = student.Id, Name = "Veli " + no, NormalizedPhone = parentPhone });
        db.SaveChanges();
        return student.Id;
    }

    private static EntitlementChargeRequest Request(IReadOnlyCollection<Guid> students, Guid mealTypeId,
        decimal perStudent = 5_000m, bool notify = false, Guid? operationId = null) =>
        new(operationId ?? Guid.NewGuid(), students, mealTypeId, perStudent,
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20), 20, notify);

    [Fact]
    public async Task ChargeWritesOneIncomeRowPerStudent()
    {
        var first = AddStudent("1001");
        var second = AddStudent("1002");

        var result = await Service().ChargeAsync(Request([first, second], mealTypeId), actor);

        Assert.Equal(2, result.ChargedStudents);
        Assert.Equal(10_000m, result.Total);
        var rows = await db.Set<IncomeTransaction>().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(5_000m, row.Amount));
        // Kasada ogrenci adiyla gorunsun ve ekstreye dussun diye StudentId dolu olmali.
        Assert.All(rows, row => Assert.NotNull(row.StudentId));
        Assert.Contains(rows, row => row.StudentId == first);
        Assert.Contains(rows, row => row.StudentId == second);
        Assert.All(rows, row => Assert.StartsWith(EfEntitlementBillingService.DescriptionPrefix, row.Description!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncomeTypeIsCreatedOnce()
    {
        await Service().ChargeAsync(Request([AddStudent("1001")], mealTypeId), actor);
        await Service().ChargeAsync(Request([AddStudent("1002")], mealTypeId), actor);

        var types = await db.Set<IncomeType>().Where(x => x.Name == EntitlementIncomeType.Name).ToListAsync();
        Assert.Single(types);
        Assert.True(types[0].IsActive);
    }

    /// <summary>Ayni islem kimligiyle tekrar (masaustu yeniden denemesi) ikinci kez para yazmamali.</summary>
    [Fact]
    public async Task RetryWithTheSameOperationDoesNotChargeTwice()
    {
        var student = AddStudent("1001");
        var operationId = Guid.NewGuid();

        var first = await Service().ChargeAsync(Request([student], mealTypeId, operationId: operationId), actor);
        var second = await Service().ChargeAsync(Request([student], mealTypeId, operationId: operationId), actor);

        Assert.Equal(1, first.ChargedStudents);
        Assert.Equal(0, second.ChargedStudents);
        Assert.Single(await db.Set<IncomeTransaction>().ToListAsync());
    }

    [Fact]
    public async Task FreeMealWritesNothing()
    {
        var result = await Service().ChargeAsync(Request([AddStudent("1001")], mealTypeId, perStudent: 0m), actor);

        Assert.Equal(0, result.ChargedStudents);
        Assert.Empty(await db.Set<IncomeTransaction>().ToListAsync());
    }

    [Fact]
    public async Task ParentsAreNotifiedWithTheAmountWhenAsked()
    {
        var sms = new FakeSmsLog();
        var student = AddStudent("1001", "+905321234567");
        AddStudent("1002");

        var result = await Service(sms).ChargeAsync(Request([student], mealTypeId, notify: true), actor);

        Assert.Equal(1, result.NotifiedParents);
        Assert.Single(sms.Sent);
        Assert.Equal("+905321234567", sms.Sent[0].Phone);
        Assert.Contains("Öğle Yemeği", sms.Sent[0].Message, StringComparison.Ordinal);
        Assert.Contains("20 gün", sms.Sent[0].Message, StringComparison.Ordinal);
        Assert.Contains("5.000,00", sms.Sent[0].Message, StringComparison.Ordinal);
    }

    /// <summary>Velisi olmayan ogrenci SMS'i sessizce atlanir; tahsilat yine de yazilir.</summary>
    [Fact]
    public async Task StudentsWithoutAParentPhoneAreSkipped()
    {
        var sms = new FakeSmsLog();
        var withPhone = AddStudent("1001", "+905321234567");
        var without = AddStudent("1002");

        var result = await Service(sms).ChargeAsync(Request([withPhone, without], mealTypeId, notify: true), actor);

        Assert.Equal(2, result.ChargedStudents);
        Assert.Equal(1, result.NotifiedParents);
    }

    [Fact]
    public async Task NoSmsWhenNotifyIsOff()
    {
        var sms = new FakeSmsLog();

        await Service(sms).ChargeAsync(Request([AddStudent("1001", "+905321234567")], mealTypeId), actor);

        Assert.Empty(sms.Sent);
    }

    /// <summary>
    /// Hakedis KISMEN iptal edilince tahsilat ORANTILI geri alinir: 20 gunluk tahsilatin
    /// 1 gunu iptal edilirse kalan 19 gunun bedeli okulda kalir.
    ///
    /// <para>
    /// Bu test once tahsilatin TAMAMININ iade edilmesini bekliyordu ("tutar kasada asili
    /// kalmasin" gerekcesiyle). O davranis okula para kaybettiriyordu: yenen ogunlerin
    /// bedeli de geri veriliyordu. Artik eski tahsilat void edilir ve KALAN gunler icin
    /// yeni bir tahsilat yazilir -- denetim izi korunur, kasa dogru kalir.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CancellingRefundsTheChargeProportionally()
    {
        var student = AddStudent("1001");
        await Service().ChargeAsync(Request([student], mealTypeId), actor);
        var entitlement = new MealEntitlement
        {
            StudentId = student, MealTypeId = mealTypeId, EntitlementDate = new DateOnly(2026, 10, 1),
            Quantity = 1, Status = "Cancelled"
        };
        db.Add(entitlement);
        await db.SaveChangesAsync();

        var refunded = await Service().RefundAsync([entitlement.Id], actor);

        Assert.Equal(1, refunded);
        // Eski kayit SILINMEZ, void edilir: denetim izi korunur.
        var original = await db.Set<IncomeTransaction>().SingleAsync(x => x.IsVoided);
        Assert.Equal(5_000m, original.Amount);
        Assert.Equal(actor, original.VoidedBy);
        Assert.Contains("kısmen iptal", original.VoidReason!, StringComparison.Ordinal);
        // 20 gunun 1'i iptal edildi; kalan 19 gunun bedeli yeniden yazilir.
        var kept = await db.Set<IncomeTransaction>().SingleAsync(x => !x.IsVoided);
        Assert.Equal(4_750m, kept.Amount);
        Assert.Equal(19, kept.EntitlementDayCount);
    }

    /// <summary>Tum gunler iptal edilirse tahsilatin tamami geri alinir; kasada kalinti olmaz.</summary>
    [Fact]
    public async Task CancellingEveryDayRefundsTheWholeCharge()
    {
        var student = AddStudent("1002");
        await Service().ChargeAsync(Request([student], mealTypeId), actor);
        var days = Enumerable.Range(0, 20)
            .Select(offset => new MealEntitlement
            {
                StudentId = student, MealTypeId = mealTypeId,
                EntitlementDate = new DateOnly(2026, 10, 1).AddDays(offset),
                Quantity = 1, Status = "Cancelled"
            }).ToList();
        db.AddRange(days);
        await db.SaveChangesAsync();

        var refunded = await Service().RefundAsync(days.Select(x => x.Id).ToList(), actor);

        Assert.Equal(1, refunded);
        var row = await db.Set<IncomeTransaction>().SingleAsync();
        Assert.True(row.IsVoided);
        Assert.Equal("Yemek hakedişi iptal edildi.", row.VoidReason);
        Assert.Equal(5_000m, row.Amount);
    }

    [Fact]
    public async Task RefundIgnoresUnrelatedIncome()
    {
        var student = AddStudent("1001");
        var type = new IncomeType { Name = "Bağış" };
        db.Add(type);
        db.SaveChanges();
        db.Add(new IncomeTransaction
        {
            OperationId = Guid.NewGuid(), StudentId = student, IncomeTypeId = type.Id,
            TransactionAt = DateTimeOffset.UtcNow, Amount = 100m, Description = "Bağış", CreatedBy = actor
        });
        var entitlement = new MealEntitlement
        {
            StudentId = student, MealTypeId = mealTypeId, EntitlementDate = new DateOnly(2026, 10, 1),
            Quantity = 1, Status = "Cancelled"
        };
        db.Add(entitlement);
        await db.SaveChangesAsync();

        var refunded = await Service().RefundAsync([entitlement.Id], actor);

        Assert.Equal(0, refunded);
        Assert.False((await db.Set<IncomeTransaction>().SingleAsync()).IsVoided);
    }

    private sealed class FakeSmsLog : ISmsLogRepository
    {
        public List<(string Phone, string Message, string Key)> Sent { get; } = [];

        public Task<SmsLogDetails> EnqueueAsync(string phone, string message, string idempotencyKey,
            Guid? studentId, Guid? templateId, CancellationToken cancellationToken)
        {
            Sent.Add((phone, message, idempotencyKey));
            return Task.FromResult(new SmsLogDetails(Guid.NewGuid(), studentId, templateId, phone, message,
                null, "Pending", idempotencyKey, 0, null, null, null, null, null, DateTimeOffset.UtcNow));
        }

        public Task<Yemekhane.Application.Common.PagedResult<SmsLogDetails>> ListAsync(SmsHistoryFilter filter, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
