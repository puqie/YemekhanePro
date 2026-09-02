using Yemekhane.Application.Common;

namespace Yemekhane.Application.Meals;

public sealed class MealTypeService(IMealTypeRepository repository)
{
    public Task<IReadOnlyList<MealTypeDetails>> ListAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        repository.ListAsync(includeInactive, cancellationToken);

    public async Task<MealTypeDetails> CreateAsync(SaveMealTypeRequest request, CancellationToken cancellationToken = default)
    {
        var valid = Validate(request);
        if (await repository.NameExistsAsync(valid.Name, null, cancellationToken)) throw new EntityConflictException("Öğün adı zaten kayıtlı.");
        return await repository.AddAsync(valid, cancellationToken);
    }

    public async Task<MealTypeDetails> UpdateAsync(Guid id, SaveMealTypeRequest request, CancellationToken cancellationToken = default)
    {
        var valid = Validate(request);
        if (await repository.NameExistsAsync(valid.Name, id, cancellationToken)) throw new EntityConflictException("Öğün adı zaten kayıtlı.");
        return await repository.UpdateAsync(id, valid, cancellationToken) ?? throw new EntityNotFoundException("Öğün bulunamadı.");
    }

    public async Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await repository.DeactivateAsync(id, cancellationToken)) throw new EntityNotFoundException("Aktif öğün bulunamadı.");
    }

    private static SaveMealTypeRequest Validate(SaveMealTypeRequest request)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 100) throw new RequestValidationException("Öğün adı 2-100 karakter olmalıdır.");
        if (request.StartsAt.HasValue != request.EndsAt.HasValue) throw new RequestValidationException("Öğün başlangıç ve bitiş saati birlikte girilmelidir.");
        if (request.StartsAt >= request.EndsAt) throw new RequestValidationException("Öğün bitiş saati başlangıçtan sonra olmalıdır.");
        if (request.Price is < 0 or > 100_000) throw new RequestValidationException("Öğün ücreti 0 ile 100.000 ₺ arasında olmalıdır.");
        // Kurus hassasiyeti: 12,345 gibi bir deger sessizce yuvarlanmasin.
        if (decimal.Round(request.Price, 2, MidpointRounding.AwayFromZero) != request.Price) throw new RequestValidationException("Öğün ücreti en fazla iki ondalık basamak içerebilir.");
        return request with { Name = name };
    }
}
