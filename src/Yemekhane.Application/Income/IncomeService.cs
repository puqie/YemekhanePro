using Yemekhane.Application.Common;

namespace Yemekhane.Application.Income;

public sealed class IncomeService(IIncomeRepository repository)
{
    public Task<IReadOnlyList<IncomeTypeDetails>> ListTypesAsync(bool includeInactive = false,
        CancellationToken cancellationToken = default) => repository.ListTypesAsync(includeInactive, cancellationToken);

    public async Task<IncomeTypeDetails> GetTypeAsync(Guid id, CancellationToken cancellationToken = default) =>
        await repository.GetTypeAsync(id, cancellationToken) ?? throw new EntityNotFoundException("Gelir türü bulunamadı.");

    public async Task<IncomeTypeDetails> CreateTypeAsync(SaveIncomeTypeRequest request, Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var valid = ValidateType(request);
        if (await repository.TypeNameExistsAsync(valid.Name, null, cancellationToken))
            throw new EntityConflictException("Gelir türü adı zaten kayıtlı.");
        return await repository.AddTypeAsync(valid, actorId, cancellationToken);
    }

    public async Task<IncomeTypeDetails> UpdateTypeAsync(Guid id, SaveIncomeTypeRequest request, Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var valid = ValidateType(request);
        if (await repository.TypeNameExistsAsync(valid.Name, id, cancellationToken))
            throw new EntityConflictException("Gelir türü adı zaten kayıtlı.");
        return await repository.UpdateTypeAsync(id, valid, actorId, cancellationToken)
            ?? throw new EntityNotFoundException("Gelir türü bulunamadı.");
    }

    public async Task DeactivateTypeAsync(Guid id, Guid actorId, CancellationToken cancellationToken = default)
    {
        if (!await repository.DeactivateTypeAsync(id, actorId, cancellationToken))
            throw new EntityNotFoundException("Aktif gelir türü bulunamadı.");
    }

    public Task<IncomeTransactionDetails> RecordAsync(CreateIncomeTransactionRequest request, Guid actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty) throw new RequestValidationException("Operation id zorunludur.");
        if (request.IncomeTypeId == Guid.Empty) throw new RequestValidationException("Gelir türü zorunludur.");
        if (request.TransactionAt == default) throw new RequestValidationException("İşlem tarihi zorunludur.");
        if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
            throw new RequestValidationException("Tutar sıfırdan büyük ve en fazla iki ondalık basamaklı olmalıdır.");
        var cardNumber = NormalizeOptional(request.CardNumber, 128, "Kart numarası");
        var description = NormalizeOptional(request.Description, 500, "Açıklama");
        return repository.CreateTransactionAsync(request with { CardNumber = cardNumber, Description = description },
            actorId, cancellationToken);
    }

    public async Task<IncomeTransactionDetails> VoidAsync(Guid id, string reason, Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var validReason = NormalizeOptional(reason, 500, "İptal nedeni");
        if (validReason is null) throw new RequestValidationException("İptal nedeni zorunludur.");
        return await repository.VoidTransactionAsync(id, validReason, actorId, cancellationToken)
            ?? throw new EntityNotFoundException("Aktif gelir işlemi bulunamadı.");
    }

    public Task<PagedResult<IncomeTransactionDetails>> ListAsync(IncomeTransactionFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Page < 1 || filter.PageSize is < 1 or > 200)
            throw new RequestValidationException("Sayfa en az 1, sayfa boyutu 1-200 olmalıdır.");
        if (filter.From > filter.To) throw new RequestValidationException("Başlangıç tarihi bitiş tarihinden sonra olamaz.");
        var cardNumber = NormalizeOptional(filter.CardNumber, 128, "Kart numarası");
        return repository.ListTransactionsAsync(filter with { CardNumber = cardNumber }, cancellationToken);
    }

    private static SaveIncomeTypeRequest ValidateType(SaveIncomeTypeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 100) throw new RequestValidationException("Gelir türü adı 2-100 karakter olmalıdır.");
        return request with { Name = name };
    }

    private static string? NormalizeOptional(string? value, int maxLength, string field)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (normalized?.Length > maxLength) throw new RequestValidationException($"{field} en fazla {maxLength} karakter olmalıdır.");
        return normalized;
    }
}
