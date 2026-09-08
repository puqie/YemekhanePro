namespace Yemekhane.Application.Cards;

/// <summary>
/// PrintedNumber: kartin ON yuzune basili kisa numara (orn. 6296). Cipin numarasi (CardNumber)
/// DEGILDIR; okul kartlarinda iki numara birden yazar ve kayip kart bulununca sahibini bulmak
/// icin bu aranir. Istege baglidir.
/// </summary>
public sealed record CardDetails(Guid Id, Guid StudentId, string StudentNo, string StudentName, string CardNumber,
    DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, string? ReplacementReason, bool IsActive, string? PrintedNumber = null);

public sealed record AssignCardRequest(string CardNumber, string? PrintedNumber = null);
public sealed record ReplaceCardRequest(string CardNumber, string Reason, string? PrintedNumber = null);
public sealed record SetPrintedNumberRequest(string? PrintedNumber);

public interface ICardRepository
{
    /// <summary>Cip numarasi ya da (aktif kartlarda) baski numarasi ile bulur.</summary>
    Task<CardDetails?> FindByNumberAsync(string cardNumber, CancellationToken cancellationToken);
    Task<IReadOnlyList<CardDetails>> GetHistoryAsync(Guid studentId, CancellationToken cancellationToken);
    Task<CardDetails> AssignAsync(Guid studentId, string cardNumber, string? printedNumber, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
    Task<CardDetails> ReplaceAsync(Guid studentId, string cardNumber, string? printedNumber, string reason, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
    /// <summary>Aktif kartin baski numarasini gunceller; aktif kart yoksa null.</summary>
    Task<CardDetails?> SetPrintedNumberAsync(Guid studentId, string? printedNumber, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(Guid cardId, string reason, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
}
