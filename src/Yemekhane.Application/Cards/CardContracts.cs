namespace Yemekhane.Application.Cards;

public sealed record CardDetails(Guid Id, Guid StudentId, string StudentNo, string StudentName, string CardNumber,
    DateTimeOffset ValidFrom, DateTimeOffset? ValidTo, string? ReplacementReason, bool IsActive);

public sealed record AssignCardRequest(string CardNumber);
public sealed record ReplaceCardRequest(string CardNumber, string Reason);

public interface ICardRepository
{
    Task<CardDetails?> FindByNumberAsync(string cardNumber, CancellationToken cancellationToken);
    Task<IReadOnlyList<CardDetails>> GetHistoryAsync(Guid studentId, CancellationToken cancellationToken);
    Task<CardDetails> AssignAsync(Guid studentId, string cardNumber, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
    Task<CardDetails> ReplaceAsync(Guid studentId, string cardNumber, string reason, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(Guid cardId, string reason, DateTimeOffset effectiveAt, CancellationToken cancellationToken);
}
