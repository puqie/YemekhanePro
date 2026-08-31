using Yemekhane.Application.Common;

namespace Yemekhane.Application.Cards;

public sealed class CardService(ICardRepository repository, TimeProvider timeProvider)
{
    public Task<CardDetails> AssignAsync(Guid studentId, AssignCardRequest request, CancellationToken cancellationToken = default) =>
        repository.AssignAsync(studentId, NormalizeCardNumber(request.CardNumber), timeProvider.GetUtcNow(), cancellationToken);

    public Task<CardDetails> ReplaceAsync(Guid studentId, ReplaceCardRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new RequestValidationException("Kart değiştirme nedeni zorunludur.");
        return repository.ReplaceAsync(studentId, NormalizeCardNumber(request.CardNumber), request.Reason.Trim(), timeProvider.GetUtcNow(), cancellationToken);
    }

    public async Task<CardDetails> FindAsync(string cardNumber, CancellationToken cancellationToken = default) =>
        await repository.FindByNumberAsync(NormalizeCardNumber(cardNumber), cancellationToken)
        ?? throw new EntityNotFoundException("Kart sistemde kayıtlı değil.");

    public Task<IReadOnlyList<CardDetails>> GetHistoryAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        repository.GetHistoryAsync(studentId, cancellationToken);

    public async Task DeactivateAsync(Guid cardId, string reason, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new RequestValidationException("Kart pasifleştirme nedeni zorunludur.");
        if (!await repository.DeactivateAsync(cardId, reason.Trim(), timeProvider.GetUtcNow(), cancellationToken))
            throw new EntityNotFoundException("Aktif kart bulunamadı.");
    }

    private static string NormalizeCardNumber(string cardNumber)
    {
        var normalized = cardNumber?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 128) throw new RequestValidationException("Kart No 1-128 karakter olmalıdır.");
        return normalized;
    }
}
