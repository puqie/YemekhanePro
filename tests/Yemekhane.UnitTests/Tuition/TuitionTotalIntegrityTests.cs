using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Tuition;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Tuition;

namespace Yemekhane.UnitTests.Tuition;

/// <summary>
/// Taksitlerin toplami HER ZAMAN plan tutarina esit olmalidir.
///
/// <para>
/// Plan tutari degistirildiginde odenmis (<c>PaidCents &gt; 0</c>) taksitler eski
/// tutariyla ATLANIYOR ve yeni plana gore duzeltilmiyordu. 48.000 TL'lik plan 60.000'e
/// cikarilinca 1. taksit 4.800'de kaliyor, kalan 9 taksit 6.000 uretiliyor ve toplam
/// 58.800 oluyordu: 1.200 TL HIC TAHSIL EDILMIYORDU. Ters yonde (burs/indirim) veliden
/// FAZLA isteniyordu.
/// </para>
/// <para>
/// Ayrisma HICBIR EKRANDA gorunmuyordu: <c>TuitionPlanDetails</c> hem <c>Amount</c>
/// (plandan) hem <c>TotalDue</c> (taksit toplamindan) donuyor ama ekran yalnizca
/// <c>Amount</c> gosteriyor. Yil sonunda "kasa neden 1.200 TL eksik" sorusunun cevabi
/// hicbir yerde yoktu.
/// </para>
/// <para>
/// Odenmis taksite DOKUNULMAZ (tahsilat gecmisi korunur); fark KALAN taksitlere
/// dagitilir. Boylece hem gecmis dogru kalir hem toplam tutar.
/// </para>
/// </summary>
public sealed class TuitionTotalIntegrityTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;

    public TuitionTotalIntegrityTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>Zam: kismi odeme varken plan buyutulurse fark kalan taksitlere yayilir.</summary>
    [Fact]
    public async Task PlanBuyutulunceToplamPlanTutarinaEsitKalir()
    {
        var (service, classId) = await SeedAsync();
        await service.SaveAsync(Plan(classId, 48_000m), Guid.NewGuid());
        await PayPartiallyAsync(1_000m);

        var updated = await service.SaveAsync(Plan(classId, 60_000m), Guid.NewGuid());

        Assert.Equal(60_000m, updated.Amount);
        Assert.Equal(60_000m, updated.TotalDue);
        Assert.Equal(updated.Amount, updated.TotalDue);
    }

    /// <summary>Indirim/burs: plan kucultulurse veliden fazla istenmez.</summary>
    [Fact]
    public async Task PlanKucultulunceToplamPlanTutarinaEsitKalir()
    {
        var (service, classId) = await SeedAsync();
        await service.SaveAsync(Plan(classId, 48_000m), Guid.NewGuid());
        await PayPartiallyAsync(1_000m);

        var updated = await service.SaveAsync(Plan(classId, 36_000m), Guid.NewGuid());

        Assert.Equal(36_000m, updated.TotalDue);
    }

    /// <summary>Odenmis taksitin TUTARI ve ODENENI korunur: tahsilat gecmisi bozulmaz.</summary>
    [Fact]
    public async Task OdenmisTaksitinGecmisiKorunur()
    {
        var (service, classId) = await SeedAsync();
        await service.SaveAsync(Plan(classId, 48_000m), Guid.NewGuid());
        await PayPartiallyAsync(1_000m);

        await service.SaveAsync(Plan(classId, 60_000m), Guid.NewGuid());

        var paid = await db.TuitionInstallments.AsNoTracking()
            .Where(x => x.PaidCents > 0).SingleAsync();
        Assert.Equal(100_000, paid.PaidCents);      // 1.000 TL odendi
        Assert.Equal(480_000, paid.AmountCents);    // ilk plandaki 4.800 TL tutari korunur
    }

    /// <summary>Odeme YOKSA butun taksitler yeniden uretilir; toplam yine tutar.</summary>
    [Fact]
    public async Task OdemeYokkenPlanDegisimindeToplamTutar()
    {
        var (service, classId) = await SeedAsync();
        await service.SaveAsync(Plan(classId, 48_000m), Guid.NewGuid());

        var updated = await service.SaveAsync(Plan(classId, 55_000m), Guid.NewGuid());

        Assert.Equal(55_000m, updated.TotalDue);
    }

    private async Task<(TuitionService Service, Guid ClassId)> SeedAsync()
    {
        var schoolClass = new SchoolClass { Name = "Anasınıfı A", Kind = ClassKinds.Preschool };
        var student = new Student { StudentNo = "9700", FirstName = "Ela", LastName = "Tan", ClassId = schoolClass.Id };
        db.AddRange(schoolClass, student);
        await db.SaveChangesAsync();
        return (new TuitionService(new EfTuitionRepository(db, TimeProvider.System), TimeProvider.System), schoolClass.Id);
    }

    private static SaveTuitionPlanRequest Plan(Guid classId, decimal amount) =>
        new(TuitionPlanKinds.Installment, "2026-2027", amount, classId, null, 0m, 10, 5,
            new DateOnly(2026, 9, 1), null);

    /// <summary>Ilk taksite kismi odeme: catisma tam da bu satirda olusuyor.</summary>
    private async Task PayPartiallyAsync(decimal amount)
    {
        var first = await db.TuitionInstallments.OrderBy(x => x.Sequence).FirstAsync();
        first.PaidCents = (long)(amount * 100m);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
