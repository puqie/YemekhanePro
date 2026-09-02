namespace Yemekhane.Application.Meals;

/// <summary>Price: ogun ucreti (₺, 2 hane). Eski programdaki "Ucret TL"; 0 = ucretsiz/tanimsiz.</summary>
public sealed record MealTypeDetails(Guid Id, string Name, TimeOnly? StartsAt, TimeOnly? EndsAt, bool IsActive, decimal Price = 0);
public sealed record SaveMealTypeRequest(string Name, TimeOnly? StartsAt = null, TimeOnly? EndsAt = null, bool IsActive = true, decimal Price = 0);

public interface IMealTypeRepository
{
    Task<IReadOnlyList<MealTypeDetails>> ListAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken);
    Task<MealTypeDetails> AddAsync(SaveMealTypeRequest request, CancellationToken cancellationToken);
    Task<MealTypeDetails?> UpdateAsync(Guid id, SaveMealTypeRequest request, CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken);
}
