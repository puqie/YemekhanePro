using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Meals;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Meals;

public sealed class EfMealTypeRepository(YemekhaneDbContext dbContext) : IMealTypeRepository
{
    public async Task<IReadOnlyList<MealTypeDetails>> ListAsync(bool includeInactive, CancellationToken cancellationToken) =>
        await dbContext.Set<MealType>().AsNoTracking().Where(x => includeInactive || x.IsActive).OrderBy(x => x.StartsAt)
            .Select(x => new MealTypeDetails(x.Id, x.Name, x.StartsAt, x.EndsAt, x.IsActive)).ToListAsync(cancellationToken);

    public Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken) =>
        dbContext.Set<MealType>().AnyAsync(x => x.Name == name && (!excludingId.HasValue || x.Id != excludingId), cancellationToken);

    public async Task<MealTypeDetails> AddAsync(SaveMealTypeRequest request, CancellationToken cancellationToken)
    {
        var meal = new MealType { Name = request.Name, StartsAt = request.StartsAt, EndsAt = request.EndsAt, IsActive = request.IsActive };
        dbContext.Add(meal); await dbContext.SaveChangesAsync(cancellationToken); return Map(meal);
    }

    public async Task<MealTypeDetails?> UpdateAsync(Guid id, SaveMealTypeRequest request, CancellationToken cancellationToken)
    {
        var meal = await dbContext.Set<MealType>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (meal is null) return null;
        meal.Name = request.Name; meal.StartsAt = request.StartsAt; meal.EndsAt = request.EndsAt; meal.IsActive = request.IsActive; meal.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken); return Map(meal);
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var meal = await dbContext.Set<MealType>().SingleOrDefaultAsync(x => x.Id == id && x.IsActive, cancellationToken);
        if (meal is null) return false;
        meal.IsActive = false; meal.UpdatedAt = DateTimeOffset.UtcNow; await dbContext.SaveChangesAsync(cancellationToken); return true;
    }

    private static MealTypeDetails Map(MealType x) => new(x.Id, x.Name, x.StartsAt, x.EndsAt, x.IsActive);
}
