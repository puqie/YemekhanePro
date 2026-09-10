using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Meals;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Meals;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Meals;

/// <summary>
/// Ogun ucreti degistiginde ESKI FIYAT kaybolmamalidir.
///
/// <para>
/// <c>MealTypePrice</c> ogun basina TEK satir tutuyor ve degisiklikte uzerine
/// yaziliyordu; <c>ValidFrom</c>/<c>ValidTo</c> yoktu. Hakedis satiri da odenen fiyati
/// saklamadigi icin "bu hak kac liradan verildi" bilgisi sistemde HICBIR YERDE yoktu.
/// </para>
/// <para>
/// Sonuc: Eylul'de 200 TL'den verilen haklar, Ocak'ta fiyat 300 TL olunca gecmise donuk
/// 300 TL gibi raporlaniyordu -- gecmis sessizce yeniden yaziliyordu.
/// </para>
/// </summary>
public sealed class MealPriceHistoryTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;

    public MealPriceHistoryTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    /// <summary>Ogun acilirken ilk fiyat da gecmise yazilir.</summary>
    [Fact]
    public async Task IlkFiyatGecmiseYazilir()
    {
        var repository = new EfMealTypeRepository(db);

        var meal = await repository.AddAsync(new SaveMealTypeRequest("Öğle", null, null, true, 200m), default);

        var history = await db.Set<MealTypePriceHistory>().AsNoTracking()
            .Where(x => x.MealTypeId == meal.Id).ToListAsync();
        var first = Assert.Single(history);
        Assert.Equal(20_000, first.PriceCents);
        Assert.Null(first.PreviousPriceCents);
    }

    /// <summary>
    /// Fiyat degisikligi ESKI degeri koruyarak yeni bir satir birakir; boylece bir
    /// tarihte hangi fiyatin gecerli oldugu sorgulanabilir.
    /// </summary>
    [Fact]
    public async Task FiyatDegisikligiEskiDegeriKorur()
    {
        var repository = new EfMealTypeRepository(db);
        var meal = await repository.AddAsync(new SaveMealTypeRequest("Öğle", null, null, true, 200m), default);

        await repository.UpdateAsync(meal.Id, new SaveMealTypeRequest("Öğle", null, null, true, 300m), default);

        var history = await db.Set<MealTypePriceHistory>().AsNoTracking()
            .Where(x => x.MealTypeId == meal.Id)
            // SQLite DateTimeOffset uzerinde ORDER BY desteklemez; depo bunun icin
            // JulianDay DbFunction'ini kullaniyor.
            .OrderBy(x => YemekhaneDbContext.JulianDay(x.EffectiveFrom)).ToListAsync();
        Assert.Equal(2, history.Count);
        Assert.Equal(20_000, history[0].PriceCents);
        Assert.Equal(30_000, history[1].PriceCents);
        // Onceki fiyat da satirda durur: tek satira bakip degisimi gormek icin.
        Assert.Equal(20_000, history[1].PreviousPriceCents);
    }

    /// <summary>
    /// Fiyat DEGISMEDIYSE gecmise yeni satir eklenmez; ogun adi guncellemesi gecmisi
    /// gurultuye bogmamali.
    /// </summary>
    [Fact]
    public async Task DegismeyenFiyatGecmiseYazilmaz()
    {
        var repository = new EfMealTypeRepository(db);
        var meal = await repository.AddAsync(new SaveMealTypeRequest("Öğle", null, null, true, 200m), default);

        await repository.UpdateAsync(meal.Id, new SaveMealTypeRequest("Öğle Yemeği", null, null, true, 200m), default);

        var history = await db.Set<MealTypePriceHistory>().AsNoTracking()
            .Where(x => x.MealTypeId == meal.Id).ToListAsync();
        Assert.Single(history);
    }

    /// <summary>
    /// Gecmis sorgusu: bir TARIHTE gecerli fiyat, o tarihten onceki en son kayittir.
    /// Gecmis hakedislerin dogru fiyattan raporlanmasi buna dayanir.
    /// </summary>
    [Fact]
    public async Task BelliBirTarihteGecerliFiyatBulunabilir()
    {
        var meal = new MealType { Name = "Öğle" };
        db.Add(meal);
        await db.SaveChangesAsync();
        db.AddRange(
            new MealTypePriceHistory { MealTypeId = meal.Id, PriceCents = 20_000, EffectiveFrom = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero) },
            new MealTypePriceHistory { MealTypeId = meal.Id, PriceCents = 30_000, EffectiveFrom = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) });
        await db.SaveChangesAsync();

        var priceInOctober = await PriceAtAsync(meal.Id, new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero));
        var priceInFebruary = await PriceAtAsync(meal.Id, new DateTimeOffset(2027, 2, 15, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(20_000, priceInOctober);
        Assert.Equal(30_000, priceInFebruary);
    }

    private async Task<long?> PriceAtAsync(Guid mealTypeId, DateTimeOffset moment) =>
        await db.Set<MealTypePriceHistory>().AsNoTracking()
            .Where(x => x.MealTypeId == mealTypeId
                && YemekhaneDbContext.JulianDay(x.EffectiveFrom) <= YemekhaneDbContext.JulianDay(moment))
            .OrderByDescending(x => YemekhaneDbContext.JulianDay(x.EffectiveFrom))
            .Select(x => (long?)x.PriceCents)
            .FirstOrDefaultAsync();
}
