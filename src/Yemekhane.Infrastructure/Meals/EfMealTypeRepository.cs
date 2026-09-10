using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Meals;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Meals;

public sealed class EfMealTypeRepository(YemekhaneDbContext dbContext) : IMealTypeRepository
{
    public async Task<IReadOnlyList<MealTypeDetails>> ListAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        // Ucret ayri tabloda (MealTypePrice); fiyati olmayan ogun 0 ₺ ile doner.
        var rows = await dbContext.Set<MealType>().AsNoTracking().Where(x => includeInactive || x.IsActive).OrderBy(x => x.StartsAt)
            .Select(x => new
            {
                x.Id, x.Name, x.StartsAt, x.EndsAt, x.IsActive,
                PriceCents = dbContext.Set<MealTypePrice>().Where(p => p.MealTypeId == x.Id).Select(p => (long?)p.PriceCents).FirstOrDefault(),
            }).ToListAsync(cancellationToken);
        return rows.Select(x => new MealTypeDetails(x.Id, x.Name, x.StartsAt, x.EndsAt, x.IsActive, ToLira(x.PriceCents ?? 0))).ToList();
    }

    public Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken) =>
        dbContext.Set<MealType>().AnyAsync(x => x.Name == name && (!excludingId.HasValue || x.Id != excludingId), cancellationToken);

    public async Task<MealTypeDetails> AddAsync(SaveMealTypeRequest request, CancellationToken cancellationToken)
    {
        var meal = new MealType { Name = request.Name, StartsAt = request.StartsAt, EndsAt = request.EndsAt, IsActive = request.IsActive };
        dbContext.Add(meal);
        var cents = ToCents(request.Price);
        dbContext.Add(new MealTypePrice { MealTypeId = meal.Id, PriceCents = cents });
        RecordPriceChange(meal.Id, cents, null);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(meal, request.Price);
    }

    public async Task<MealTypeDetails?> UpdateAsync(Guid id, SaveMealTypeRequest request, CancellationToken cancellationToken)
    {
        var meal = await dbContext.Set<MealType>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (meal is null) return null;
        meal.Name = request.Name; meal.StartsAt = request.StartsAt; meal.EndsAt = request.EndsAt; meal.IsActive = request.IsActive; meal.UpdatedAt = DateTimeOffset.UtcNow;
        var price = await dbContext.Set<MealTypePrice>().SingleOrDefaultAsync(x => x.MealTypeId == id, cancellationToken);
        var cents = ToCents(request.Price);
        if (price is null)
        {
            dbContext.Add(new MealTypePrice { MealTypeId = id, PriceCents = cents });
            RecordPriceChange(id, cents, null);
        }
        else if (price.PriceCents != cents)
        {
            // Yalnizca fiyat DEGISTIYSE gecmise satir eklenir; ogun adi guncellemesi
            // gecmisi gurultuye bogmamali.
            RecordPriceChange(id, cents, price.PriceCents);
            price.PriceCents = cents; price.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(meal, request.Price);
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var meal = await dbContext.Set<MealType>().SingleOrDefaultAsync(x => x.Id == id && x.IsActive, cancellationToken);
        if (meal is null) return false;
        meal.IsActive = false; meal.UpdatedAt = DateTimeOffset.UtcNow; await dbContext.SaveChangesAsync(cancellationToken); return true;
    }

    /// <summary>
    /// Fiyat degisikligini GECMISE yazar. <see cref="MealTypePrice"/> ogun basina tek
    /// satir tutup uzerine yazdigi icin eski deger kayboluyordu; gecmis hakedisler
    /// guncel fiyattan raporlaniyordu.
    /// </summary>
    private void RecordPriceChange(Guid mealTypeId, long priceCents, long? previousPriceCents) =>
        dbContext.Add(new MealTypePriceHistory
        {
            MealTypeId = mealTypeId,
            PriceCents = priceCents,
            PreviousPriceCents = previousPriceCents,
            EffectiveFrom = DateTimeOffset.UtcNow
        });

    private static MealTypeDetails Map(MealType x, decimal price) => new(x.Id, x.Name, x.StartsAt, x.EndsAt, x.IsActive, price);
    private static long ToCents(decimal lira) => (long)decimal.Round(lira * 100m, 0, MidpointRounding.AwayFromZero);
    private static decimal ToLira(long cents) => cents / 100m;
}
