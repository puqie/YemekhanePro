using Yemekhane.Application.Common;

namespace Yemekhane.Application.Cards;

// Otomatik SMS kancasi istege baglidir: `new CardService(repo, clock)` kuran testler etkilenmez.
public sealed class CardService(ICardRepository repository, TimeProvider timeProvider, Yemekhane.Application.Sms.ISmsAutomationTrigger? smsAutomation = null)
{
    public async Task<CardDetails> AssignAsync(Guid studentId, AssignCardRequest request, CancellationToken cancellationToken = default)
    {
        var card = await repository.AssignAsync(studentId, NormalizeCardNumber(request.CardNumber), NormalizePrintedNumber(request.PrintedNumber), timeProvider.GetUtcNow(), cancellationToken);
        // Kayit basarisindan SONRA; kanca hata yutar, kart islemi geri alinmaz.
        if (smsAutomation is not null) await smsAutomation.CardChangedAsync(card, replaced: false, cancellationToken);
        return card;
    }

    public async Task<CardDetails> ReplaceAsync(Guid studentId, ReplaceCardRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new RequestValidationException("Kart değiştirme nedeni zorunludur.");
        var card = await repository.ReplaceAsync(studentId, NormalizeCardNumber(request.CardNumber), NormalizePrintedNumber(request.PrintedNumber), request.Reason.Trim(), timeProvider.GetUtcNow(), cancellationToken);
        if (smsAutomation is not null) await smsAutomation.CardChangedAsync(card, replaced: true, cancellationToken);
        return card;
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

    /// <summary>Aktif kartin baski numarasini gunceller; kart degismez.</summary>
    public async Task<CardDetails> SetPrintedNumberAsync(Guid studentId, SetPrintedNumberRequest request, CancellationToken cancellationToken = default) =>
        await repository.SetPrintedNumberAsync(studentId, NormalizePrintedNumber(request.PrintedNumber), timeProvider.GetUtcNow(), cancellationToken)
        ?? throw new EntityNotFoundException("Öğrencinin aktif kartı bulunamadı.");

    private static string? NormalizePrintedNumber(string? printedNumber)
    {
        var normalized = printedNumber?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > 32) throw new RequestValidationException("Baskı No en fazla 32 karakter olabilir.");
        return normalized;
    }

    private static string NormalizeCardNumber(string cardNumber)
    {
        var normalized = cardNumber?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 128) throw new RequestValidationException("Kart No 1-128 karakter olmalıdır.");
        return normalized;
    }
}
