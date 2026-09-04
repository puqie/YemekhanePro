using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.BulkOperations;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.BulkOperations;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.BulkOperations;

public sealed class BulkOperationServiceTests
{
    [Fact]
    public async Task ClassScopeSeparates5AFrom5B()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request("CancelEntitlements", "Delete", new BulkOperationScope("Class", fixture.ClassA.Id));
        var preview = await fixture.Service.PreviewAsync(request);
        Assert.Single(preview.Entitlements);
        Assert.Equal(fixture.StudentA.Id, preview.Entitlements[0].StudentId);
        Assert.DoesNotContain(preview.Entitlements, x => x.StudentId == fixture.StudentB.Id);
    }

    [Fact]
    public async Task SpecifiedDateTransfersElevenTwelveThirteenToFifteenth()
    {
        await using var fixture = await Fixture.CreateAsync(includeThreeDays: true);
        var request = fixture.Request("Transfer", "SpecifiedDate", target: new DateOnly(2026, 9, 15),
            starts: new DateOnly(2026, 9, 11), ends: new DateOnly(2026, 9, 13));
        var preview = await fixture.Service.PreviewAsync(request);
        Assert.Equal(3, preview.TransferredCount);
        Assert.All(preview.Entitlements, x => Assert.Equal(new DateOnly(2026, 9, 15), x.TargetDate));
        var result = await fixture.Service.ApplyAsync(new(request, preview.PreviewToken), fixture.UserId);
        Assert.Equal([new DateOnly(2026, 9, 15)], result.TargetDates);
        Assert.Equal(3, (await fixture.Db.MealEntitlements.SingleAsync(x => x.EntitlementDate == new DateOnly(2026, 9, 15))).Quantity);
        Assert.Equal(3, await fixture.Db.MealTransfers.CountAsync());
    }

    [Fact]
    public async Task NextBusinessDaySkipsWeekendAndClosure()
    {
        await using var fixture = await Fixture.CreateAsync(closed: [new DateOnly(2026, 9, 14)]);
        var request = fixture.Request("Transfer", "NextBusinessDay");
        var preview = await fixture.Service.PreviewAsync(request);
        Assert.Equal(new DateOnly(2026, 9, 15), preview.Entitlements.Single().TargetDate);
    }

    /// <summary>
    /// COK GUNLU TATIL: 11-13 Eylul tatil edilirse UC hak, izleyen UC AYRI is gunune
    /// dagilmalidir.
    ///
    /// <para>
    /// Onceki davranis hepsini AYNI "sonraki is gunune" tasiyip orada topluyordu
    /// (Quantity += ...): ogrenci 14 Eylul'de uc ogun hakkina sahip gorunuyor,
    /// 15-16 bos kaliyordu. Kullanicinin kurali "bes gunu olana bes gun, uc gunu
    /// olana uc gun" -- yani her hak kendi gunune.
    /// </para>
    /// </summary>
    [Fact]
    public async Task CokGunluTatilHaklariAyriIsGunlerineDagitir()
    {
        // BES ARDISIK IS GUNU (7-11 Eylul, Pzt-Cum). Ardisik olmasi SART: 11-13
        // kullanilsaydi 12-13 hafta sonu olacagi icin kaynak gunlerin birbirine
        // hedef olmasi durumu hic olusmaz ve hata gozden kacardi (olculdu).
        await using var fixture = await Fixture.CreateAsync(includeWorkWeek: true);
        var request = fixture.Request("Transfer", "NextBusinessDay",
            starts: new DateOnly(2026, 9, 7), ends: new DateOnly(2026, 9, 11));

        var preview = await fixture.Service.PreviewAsync(request);

        // Bes hak, izleyen bes IS GUNUNE birer birer: 14-18 Eylul.
        // Kaynak gunler (7-11) HEDEF OLAMAZ; olsaydi haklar tatilin ICINE kayardi.
        Assert.Equal(5, preview.TransferredCount);
        Assert.Equal(
            [new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 16),
             new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 18)],
            preview.Entitlements.Select(x => x.TargetDate).OrderBy(x => x));

        await fixture.Service.ApplyAsync(new(request, preview.PreviewToken), fixture.UserId);

        // HICBIR gune yigilmamali: her hedef gunde tek hak.
        foreach (var day in new[] { 14, 15, 16, 17, 18 })
            Assert.Equal(1, (await fixture.Db.MealEntitlements
                .SingleAsync(x => x.EntitlementDate == new DateOnly(2026, 9, day) && x.StudentId == fixture.StudentA.Id)).Quantity);
        Assert.Equal(5, await fixture.Db.MealTransfers.CountAsync(x => x.StudentId == fixture.StudentA.Id));
    }

    [Fact]
    public async Task DatabaseVersionChangeInvalidatesPreviewAndWritesNothing()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request("CancelEntitlements", "Delete");
        var preview = await fixture.Service.PreviewAsync(request);
        var right = await fixture.Db.MealEntitlements.SingleAsync(x => x.StudentId == fixture.StudentA.Id);
        right.Quantity++; right.Version++; await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<EntityConflictException>(() => fixture.Service.ApplyAsync(new(request, preview.PreviewToken), fixture.UserId));
        Assert.Empty(await fixture.Db.BulkOperations.ToListAsync());
        Assert.Equal("Active", right.Status);
    }

    [Fact]
    public async Task AuditFailureRollsBackWholeApply()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request("CancelEntitlements", "Delete");
        var preview = await fixture.Service.PreviewAsync(request);
        await fixture.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_bulk_audit BEFORE INSERT ON audit_logs WHEN NEW.Action = 'BulkOperationApplied' BEGIN SELECT RAISE(ABORT, 'audit failure'); END;");
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Service.ApplyAsync(new(request, preview.PreviewToken), fixture.UserId));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal("Active", (await fixture.Db.MealEntitlements.SingleAsync(x => x.StudentId == fixture.StudentA.Id)).Status);
        Assert.Empty(await fixture.Db.BulkOperations.ToListAsync());
    }

    [Fact]
    public async Task ApplyIsIdempotentAndRejectsKeyReuseForDifferentRequest()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request("CancelEntitlements", "Delete");
        var preview = await fixture.Service.PreviewAsync(request);
        var first = await fixture.Service.ApplyAsync(new(request, preview.PreviewToken), fixture.UserId);
        var replay = await fixture.Service.ApplyAsync(new(request, preview.PreviewToken), fixture.UserId);
        Assert.Equal(first.OperationId, replay.OperationId); Assert.True(replay.IdempotentReplay);
        var changed = request with { Description = "different" };
        await Assert.ThrowsAsync<EntityConflictException>(() => fixture.Service.ApplyAsync(new(changed, preview.PreviewToken), fixture.UserId));
    }

    /// <summary>
    /// Manuel kapsam okul numarasi kabul eder; onizleme satirlari ogrenciyi no/ad/sinif ile
    /// tasir ve aktarim olmayan islemde hedef tarih null'dur (once 0001-01-01 geliyordu).
    /// </summary>
    [Fact]
    public async Task ManualScopeResolvesStudentNosAndPreviewCarriesIdentity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var request = fixture.Request("CancelEntitlements", "Delete", new BulkOperationScope("Manual", null, null, ["A"]));
        var preview = await fixture.Service.PreviewAsync(request);
        var row = Assert.Single(preview.Entitlements);
        Assert.Equal(fixture.StudentA.Id, row.StudentId);
        Assert.Equal("A", row.StudentNo); Assert.Equal("Ada A", row.StudentName); Assert.Equal("5A", row.ClassName);
        Assert.Null(row.TargetDate);
        var applied = await fixture.Service.ApplyAsync(new(request, preview.PreviewToken), fixture.UserId);
        Assert.Equal(1, applied.CancelledCount);

        var unknown = fixture.Request("CancelEntitlements", "Delete", new BulkOperationScope("Manual", null, null, ["A", "ZZZ"]));
        var error = await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Service.PreviewAsync(unknown));
        Assert.Contains("ZZZ", error.Message);
        await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Service.PreviewAsync(
            fixture.Request("CancelEntitlements", "Delete", new BulkOperationScope("Manual", null, [], []))));
    }

    /// <summary>
    /// Gecmis listesi SQLite uzerinde CALISMALI ve en yeni islem en ustte gelmeli.
    /// Onceden OrderByDescending(CreatedAt) DateTimeOffset oldugu icin SQLite tarafinda
    /// NotSupportedException firlatiyor, API 500 donuyor ve sihirbaz "Gecmis"i hic acamiyordu.
    /// </summary>
    [Fact]
    public async Task HistoryWorksOnSqliteAndListsNewestFirst()
    {
        await using var fixture = await Fixture.CreateAsync(includeThreeDays: true);
        var first = fixture.Request("CancelEntitlements", "Delete", starts: new DateOnly(2026, 9, 11), ends: new DateOnly(2026, 9, 11));
        var firstResult = await fixture.Service.ApplyAsync(new(first, (await fixture.Service.PreviewAsync(first)).PreviewToken), fixture.UserId);
        await Task.Delay(5); // JulianDay milisaniye cozunurlugunde; ayni milisaniyede iki kayit siralamayi belirsiz kilar
        var second = fixture.Request("CancelEntitlements", "Forfeit", starts: new DateOnly(2026, 9, 12), ends: new DateOnly(2026, 9, 12));
        var secondResult = await fixture.Service.ApplyAsync(new(second, (await fixture.Service.PreviewAsync(second)).PreviewToken), fixture.UserId);

        var page = await fixture.Service.HistoryAsync(1, 30);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(secondResult.OperationId, page.Items[0].Id);
        Assert.Equal(firstResult.OperationId, page.Items[1].Id);
        Assert.True(page.Items[0].CanUndo);
    }

    [Fact]
    public async Task UndoRestoresUntouchedOperationAndReportsChangedRecordConflict()
    {
        await using var success = await Fixture.CreateAsync();
        var request = success.Request("CancelEntitlements", "Delete");
        var preview = await success.Service.PreviewAsync(request);
        var applied = await success.Service.ApplyAsync(new(request, preview.PreviewToken), success.UserId);
        var undone = await success.Service.UndoAsync(applied.OperationId, success.UserId);
        Assert.True(undone.Reverted);
        Assert.Equal("Active", (await success.Db.MealEntitlements.SingleAsync(x => x.StudentId == success.StudentA.Id)).Status);

        await using var conflict = await Fixture.CreateAsync();
        var transfer = conflict.Request("Transfer", "SpecifiedDate", target: new DateOnly(2026, 9, 15));
        var transferPreview = await conflict.Service.PreviewAsync(transfer);
        var transferResult = await conflict.Service.ApplyAsync(new(transfer, transferPreview.PreviewToken), conflict.UserId);
        var target = await conflict.Db.MealEntitlements.SingleAsync(x => x.EntitlementDate == new DateOnly(2026, 9, 15));
        target.ConsumedQuantity = 1; target.Version++; await conflict.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<EntityConflictException>(() => conflict.Service.UndoAsync(transferResult.OperationId, conflict.UserId));
        Assert.Contains("hak kullanılmış", error.Message);
        Assert.Equal("Completed", (await conflict.Db.BulkOperations.SingleAsync()).Status);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        private Fixture(SqliteConnection connection, YemekhaneDbContext db, BulkOperationService service,
            SchoolClass classA, SchoolClass classB, Student studentA, Student studentB, MealType meal)
        { this.connection = connection; Db = db; Service = service; ClassA = classA; ClassB = classB; StudentA = studentA; StudentB = studentB; Meal = meal; }
        public YemekhaneDbContext Db { get; } public BulkOperationService Service { get; }
        public SchoolClass ClassA { get; } public SchoolClass ClassB { get; } public Student StudentA { get; } public Student StudentB { get; } public MealType Meal { get; }
        public Guid UserId { get; } = Guid.NewGuid();

        /// <param name="includeWorkWeek">
        /// 7-11 Eylul 2026 (Pazartesi-Cuma) bes ARDISIK IS GUNU. includeThreeDays
        /// 11-13'u kullanir ama 12-13 hafta sonudur; kaynak gunlerin birbirine hedef
        /// olmasi durumu orada HIC olusmaz ve hata gozden kacar.
        /// </param>
        public static async Task<Fixture> CreateAsync(bool includeThreeDays = false, IReadOnlyCollection<DateOnly>? closed = null,
            bool includeWorkWeek = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await db.Database.MigrateAsync();
            var classA = new SchoolClass { Name = "5A" }; var classB = new SchoolClass { Name = "5B" };
            var studentA = new Student { StudentNo = "A", FirstName = "Ada", LastName = "A", ClassId = classA.Id };
            var studentB = new Student { StudentNo = "B", FirstName = "Bora", LastName = "B", ClassId = classB.Id };
            var meal = new MealType { Name = "Öğle" }; db.AddRange(classA, classB, studentA, studentB, meal); await db.SaveChangesAsync();
            var days = includeWorkWeek
                ? [new DateOnly(2026, 9, 7), new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 11)]
                : includeThreeDays ? new[] { new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 12), new DateOnly(2026, 9, 13) } : [new DateOnly(2026, 9, 11)];
            foreach (var day in days) db.MealEntitlements.Add(new MealEntitlement { StudentId = studentA.Id, MealTypeId = meal.Id, EntitlementDate = day, Quantity = 1, Status = "Active" });
            db.MealEntitlements.Add(new MealEntitlement { StudentId = studentB.Id, MealTypeId = meal.Id, EntitlementDate = new DateOnly(2026, 9, 11), Quantity = 1, Status = "Active" });
            await db.SaveChangesAsync();
            var audit = new AuditService(new EfAuditRepository(db, TimeProvider.System), new SystemAuditContext());
            var repo = new EfBulkOperationRepository(db, audit, TimeProvider.System);
            var service = new BulkOperationService(repo, new BusinessDayService(new Closures(closed ?? []), new WeekendPolicy()), new BulkPreviewTokenProtector(), TimeProvider.System);
            return new Fixture(connection, db, service, classA, classB, studentA, studentB, meal);
        }

        public BulkCalendarOperationRequest Request(string operation, string behavior, BulkOperationScope? scope = null,
            DateOnly? target = null, DateOnly? starts = null, DateOnly? ends = null) =>
            new(Guid.NewGuid().ToString("N"), scope ?? new("Class", ClassA.Id), starts ?? new(2026, 9, 11),
                ends ?? new(2026, 9, 11), [], Meal.Id, operation, behavior, target, "Test");
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }

    private sealed class Closures(IReadOnlyCollection<DateOnly> dates) : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) => Task.FromResult(dates.Contains(calendarDate));
    }
}
