using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Integration;

/// <summary>
/// UZLASTIRMA: bir donem boyunca yapilan islemlerden sonra defterler BIRBIRINI TUTMALIDIR.
///
/// <para>
/// Tek tek dogru calisan islemler, bir arada calistiginda birbirini bozabilir. Bu testler
/// gercek bir donemi taklit eder (coklu ogrenci, coklu ogun, kismi iptaller, yeniden
/// yuklemeler) ve sonunda TEK BIR SORU sorar: kasadaki para, verilen haklarin karsiligi
/// midir?
/// </para>
/// <para>
/// Tek bir senaryoyu degil, DEGISMEZLIKLERI dogrular: hangi sirayla ne yapilirsa yapilsin
/// bozulmamasi gereken esitlikler.
/// </para>
/// </summary>
public sealed class SchoolYearReconciliationTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;
    private static readonly DateOnly Start = new(2026, 10, 1);
    private Guid lunchId, breakfastId;
    private readonly List<Guid> students = [];

    public SchoolYearReconciliationTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>
    /// KOK DEGISMEZ: aktif tahsilat toplami, aktif haklarin bedeline ESIT olmalidir.
    /// Kismi iptaller ve yeniden yuklemeler bu esitligi bozmamalidir.
    /// </summary>
    [Fact]
    public async Task TheCashAlwaysMatchesTheRightsItPaidFor()
    {
        await SeedAsync(studentCount: 12);
        var service = Service();

        // 1) Herkese 10 gunluk ogle hakki.
        foreach (var student in students) await GrantAsync(service, lunchId, student, 10);
        await AssertBalancedAsync();

        // 2) Ucte birine kismi iptal (ilk 3 gun).
        foreach (var student in students.Take(4))
        {
            var ids = await FirstIdsAsync(student, lunchId, 3);
            await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(ids, ids.Count));
        }
        await AssertBalancedAsync();

        // 3) Yeniden yukleme: iptal edilenlere 5 gun daha.
        foreach (var student in students.Take(4)) await GrantAsync(service, lunchId, student, 5, Start.AddDays(20));
        await AssertBalancedAsync();

        // 4) Ikinci ogun: yarisina kahvalti.
        foreach (var student in students.Take(6)) await GrantAsync(service, breakfastId, student, 4);
        await AssertBalancedAsync();

        // 5) Tam iptal: iki ogrencinin TUM haklari.
        foreach (var student in students.TakeLast(2))
        {
            var ids = await AllIdsAsync(student);
            await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(ids, ids.Count));
        }
        await AssertBalancedAsync();
    }

    /// <summary>
    /// Ayni gune tekrar hak vermek IKINCI tahsilat acmaz: hak guncellenir, para bir kez alinir.
    /// </summary>
    [Fact]
    public async Task RegrantingTheSameDayDoesNotChargeTwice()
    {
        await SeedAsync(studentCount: 3);
        var service = Service();
        foreach (var student in students) await GrantAsync(service, lunchId, student, 5);
        var afterFirst = await ActiveCashAsync();

        // Ayni gunler icin YENIDEN hak verilir.
        foreach (var student in students) await GrantAsync(service, lunchId, student, 5);

        Assert.Equal(afterFirst, await ActiveCashAsync());
        await AssertBalancedAsync();
    }

    /// <summary>
    /// Tuketilmis hak iptal edilemez: yenen ogunun parasi iade edilirse okul zarar eder.
    /// </summary>
    [Fact]
    public async Task AConsumedRightCannotBeCancelled()
    {
        await SeedAsync(studentCount: 1);
        var service = Service();
        var student = students[0];
        await GrantAsync(service, lunchId, student, 3);
        var cashBefore = await ActiveCashAsync();

        // Ilk gunun hakki KULLANILIR.
        var first = (await FirstIdsAsync(student, lunchId, 1)).Single();
        var right = await db.MealEntitlements.SingleAsync(x => x.Id == first);
        right.ConsumedQuantity = right.Quantity;
        await db.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<Exception>(
            () => service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest([first], 1)));

        // Hicbir sey degismedi: para da hak da yerinde.
        Assert.Equal(cashBefore, await ActiveCashAsync());
    }

    /// <summary>
    /// RASTGELE ISLEM DIZISI: hangi sirayla ne yapilirsa yapilsin kasa ile hak
    /// birbirini tutmalidir.
    ///
    /// <para>
    /// Elle yazilan senaryolar yalnizca DUSUNULEN sirayi dener. Gercek okulda islemler
    /// karisik sirayla gelir: yukleme, kismi iptal, yeniden yukleme, ikinci ogun, tam
    /// iptal... Bu test sabit bir tohumla (tekrarlanabilir) rastgele diziler uretir ve
    /// her adimdan sonra degismezi dogrular. Bozulursa hangi ADIMDA bozuldugunu soyler.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(42)]
    [InlineData(2026)]
    public async Task TheLedgersStayBalancedUnderRandomOperationSequences(int seed)
    {
        await SeedAsync(studentCount: 8);
        var service = Service();
        var random = new Random(seed);

        for (var step = 0; step < 40; step++)
        {
            var student = students[random.Next(students.Count)];
            var meal = random.Next(2) == 0 ? lunchId : breakfastId;

            // CAKISMA BEKLENIR: onizleme alindiktan sonra veri degisirse sistem
            // "Yeniden onizleyin" der ve HICBIR SEY YAZMAZ. Bu DOGRU davranistir:
            // iki kisi ayni ogrenciye ayni anda hak verirse ikincisi eski onizlemeyle
            // yazmamalidir. Test bu reddi bir HATA saymaz; onemli olan reddedilen
            // islemin defterleri BOZMAMASIDIR.
            try
            {
            switch (random.Next(3))
            {
                case 0:
                    // Yeni hak: baslangic gunu de degisir ki araliklar cakissin.
                    await GrantAsync(service, meal, student, random.Next(1, 6),
                        Start.AddDays(random.Next(0, 30)));
                    break;
                case 1:
                    // Kismi iptal: var olan haklarin bir kismi.
                    var some = await FirstIdsAsync(student, meal, random.Next(1, 4));
                    if (some.Count > 0)
                        await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(some, some.Count));
                    break;
                default:
                    // Tam iptal.
                    var all = await AllIdsAsync(student);
                    if (all.Count > 0)
                        await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(all, all.Count));
                    break;
            }
            }
            catch (Yemekhane.Application.Common.EntityConflictException)
            {
                // Reddedildi; degismez yine de dogrulanir.
            }
            catch (Yemekhane.Application.Common.RequestValidationException)
            {
                // Gecersiz istek (orn. bos secim); yazma olmadi.
            }

            var cash = await ActiveCashAsync();
            var owed = await ActiveRightsValueAsync();
            Assert.True(cash == owed,
                $"Tohum {seed}, adım {step}: kasa {cash:N2} ₺, hakların bedeli {owed:N2} ₺ (fark {cash - owed:N2} ₺).");
        }
    }

    /// <summary>
    /// OGRENCI HAKKI DEGISMEZI: kullanilan ogun sayisi, verilen haktan FAZLA olamaz.
    ///
    /// <para>
    /// Para tarafi tutuyor olsa bile hak tarafi bozulabilir: iptal/aktarim/yeniden yukleme
    /// karisiminda bir hak iki kez sayilirsa ogrenci hakkindan fazla yemek yer, tersi olursa
    /// hakkini kaybeder. Bu test her adimda ConsumedQuantity &lt;= Quantity kuralini dogrular.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(99)]
    public async Task NoStudentEverConsumesMoreThanGranted(int seed)
    {
        await SeedAsync(studentCount: 6);
        var service = Service();
        var random = new Random(seed);

        for (var step = 0; step < 30; step++)
        {
            var student = students[random.Next(students.Count)];
            try
            {
                if (random.Next(2) == 0)
                {
                    await GrantAsync(service, lunchId, student, random.Next(1, 4),
                        Start.AddDays(random.Next(0, 15)));
                }
                else
                {
                    // Rastgele bir hakki TUKET: turnike gecisini taklit eder.
                    var open = await db.MealEntitlements
                        .Where(x => x.StudentId == student && x.Status == "Active" && x.ConsumedQuantity < x.Quantity)
                        .FirstOrDefaultAsync();
                    if (open is not null)
                    {
                        open.ConsumedQuantity++;
                        await db.SaveChangesAsync();
                    }
                }
            }
            catch (Yemekhane.Application.Common.EntityConflictException) { }
            catch (Yemekhane.Application.Common.RequestValidationException) { }

            var overdrawn = await db.MealEntitlements.AsNoTracking()
                .Where(x => x.ConsumedQuantity > x.Quantity)
                .CountAsync();
            Assert.True(overdrawn == 0,
                $"Tohum {seed}, adım {step}: {overdrawn} hakta kullanım verilen haktan fazla.");

            // Negatif adet de olusmamali: iptal/aktarim bir hakki eksiye dusurmemeli.
            var negative = await db.MealEntitlements.AsNoTracking()
                .Where(x => x.Quantity < 0 || x.ConsumedQuantity < 0)
                .CountAsync();
            Assert.True(negative == 0, $"Tohum {seed}, adım {step}: {negative} hakta negatif adet var.");
        }
    }

    /// <summary>
    /// Iptal edilen hak KULLANILAMAZ hale gelmeli: "Cancelled" satirin kalan hakki
    /// sayilmamalidir, yoksa iptal edilmis hak turnikeden gecerdi.
    /// </summary>
    [Fact]
    public async Task CancelledRightsAreNotCountedAsAvailable()
    {
        await SeedAsync(studentCount: 2);
        var service = Service();
        foreach (var student in students) await GrantAsync(service, lunchId, student, 4);

        var ids = await AllIdsAsync(students[0]);
        await service.CancelBulkWithRefundAsync(new CancelEntitlementsRequest(ids, ids.Count));

        var available = await db.MealEntitlements.AsNoTracking()
            .Where(x => x.StudentId == students[0] && x.Status == "Active")
            .SumAsync(x => (int?)(x.Quantity - x.ConsumedQuantity)) ?? 0;
        Assert.Equal(0, available);
    }

    // --- degismez kontrolu ---

    /// <summary>
    /// Aktif tahsilat toplami = aktif haklarin bedeli. Sapma varsa hangi yonde oldugunu
    /// soyler: kullaniciya "kasa ile hak birbirini tutmuyor" demek yeterli degildir.
    /// </summary>
    private async Task AssertBalancedAsync()
    {
        var cash = await ActiveCashAsync();
        var owed = await ActiveRightsValueAsync();

        Assert.True(cash == owed,
            $"Kasa ile hak ayrıştı: kasada {cash:N2} ₺, hakların bedeli {owed:N2} ₺ (fark {cash - owed:N2} ₺).");
    }

    private async Task<decimal> ActiveCashAsync() =>
        (await db.Set<IncomeTransaction>().AsNoTracking().Where(x => !x.IsVoided).ToListAsync()).Sum(x => x.Amount);

    /// <summary>Aktif haklarin toplam bedeli (ogun fiyati x adet).</summary>
    private async Task<decimal> ActiveRightsValueAsync()
    {
        var prices = await db.Set<MealTypePrice>().AsNoTracking()
            .ToDictionaryAsync(x => x.MealTypeId, x => x.PriceCents);
        var rows = await db.MealEntitlements.AsNoTracking()
            .Where(x => x.Status == "Active")
            .Select(x => new { x.MealTypeId, x.Quantity })
            .ToListAsync();
        return rows.Sum(x => prices.GetValueOrDefault(x.MealTypeId) * x.Quantity) / 100m;
    }

    // --- kurulum ---

    private async Task SeedAsync(int studentCount)
    {
        var lunch = new MealType { Name = "Öğle" };
        var breakfast = new MealType { Name = "Kahvaltı" };
        db.AddRange(lunch, breakfast);
        await db.SaveChangesAsync();
        lunchId = lunch.Id; breakfastId = breakfast.Id;
        db.Add(new MealTypePrice { MealTypeId = lunchId, PriceCents = 6000 });
        db.Add(new MealTypePrice { MealTypeId = breakfastId, PriceCents = 2500 });
        await db.SaveChangesAsync();

        for (var index = 0; index < studentCount; index++)
        {
            var student = new Student
            {
                StudentNo = (7000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                FirstName = "Ad" + index, LastName = "Soyad",
                SearchName = "ad" + index + " soyad", IsActive = true
            };
            db.Add(student);
            students.Add(student.Id);
        }
        await db.SaveChangesAsync();
    }

    private static async Task GrantAsync(MealEntitlementService service, Guid mealTypeId, Guid studentId,
        int days, DateOnly? startsOn = null)
    {
        var from = startsOn ?? Start;
        var grant = new EntitlementGrantRequest(new EntitlementTarget("Manual", [studentId]), mealTypeId,
            from, from, ChargeToCash: true, OperationId: Guid.NewGuid(), DayCount: days);
        var preview = await service.PreviewAsync(grant);
        await service.ApplyAsync(new ApplyEntitlementGrantRequest(grant, preview.PreviewToken));
    }

    private async Task<IReadOnlyCollection<Guid>> FirstIdsAsync(Guid studentId, Guid mealTypeId, int count) =>
        await db.MealEntitlements.AsNoTracking()
            .Where(x => x.StudentId == studentId && x.MealTypeId == mealTypeId && x.Status == "Active")
            .OrderBy(x => x.EntitlementDate).Take(count).Select(x => x.Id).ToListAsync();

    private async Task<IReadOnlyCollection<Guid>> AllIdsAsync(Guid studentId) =>
        await db.MealEntitlements.AsNoTracking()
            .Where(x => x.StudentId == studentId && x.Status == "Active")
            .Select(x => x.Id).ToListAsync();

    private MealEntitlementService Service()
    {
        var audit = new AuditService(new EfAuditRepository(db, TimeProvider.System), new SystemAuditContext());
        return new MealEntitlementService(new EfMealEntitlementRepository(db),
            new BusinessDayService(new NoClosures(), new WeekendPolicy()),
            new EfEntitlementBillingService(db, TimeProvider.System, audit));
    }

    private sealed class NoClosures : ICalendarClosureProvider
    {
        public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }
}
