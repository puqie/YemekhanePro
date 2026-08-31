namespace Yemekhane.Application.Meals;

public sealed record MealTypeDetails(Guid Id, string Name, TimeOnly? StartsAt, TimeOnly? EndsAt, bool IsActive);
public sealed record SaveMealTypeRequest(string Name, TimeOnly? StartsAt = null, TimeOnly? EndsAt = null, bool IsActive = true);

public interface IMealTypeRepository
{
    Task<IReadOnlyList<MealTypeDetails>> ListAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken);
    Task<MealTypeDetails> AddAsync(SaveMealTypeRequest request, CancellationToken cancellationToken);
    Task<MealTypeDetails?> UpdateAsync(Guid id, SaveMealTypeRequest request, CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken);
}
