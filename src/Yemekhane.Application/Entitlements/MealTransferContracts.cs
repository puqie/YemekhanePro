using Yemekhane.Application.Calendar;

namespace Yemekhane.Application.Entitlements;

public sealed record TransferMealEntitlementsRequest(
    IReadOnlyCollection<Guid> EntitlementIds,
    string TargetMode,
    DateOnly? TargetDate,
    CalendarScope Scope,
    string Reason,
    Guid CreatedBy);

public sealed record EntitlementTransferCandidate(Guid EntitlementId, Guid StudentId, Guid MealTypeId,
    DateOnly OriginalDate, int RemainingQuantity);
public sealed record EntitlementTransferCommand(EntitlementTransferCandidate Source, DateOnly TargetDate);
public sealed record MealTransferResult(int EntitlementCount, int TransferredQuantity, IReadOnlyCollection<DateOnly> TargetDates);
public sealed record MealTransferDetails(Guid Id, Guid StudentId, Guid MealTypeId, DateOnly OriginalDate,
    DateOnly TargetDate, int Quantity, string Reason, Guid CreatedBy);

public interface IMealTransferRepository
{
    Task<IReadOnlyList<EntitlementTransferCandidate>> GetCandidatesAsync(IReadOnlyCollection<Guid> entitlementIds, CancellationToken cancellationToken);
    Task<MealTransferResult> TransferAsync(IReadOnlyCollection<EntitlementTransferCommand> commands, string reason,
        Guid createdBy, CancellationToken cancellationToken);
    Task<IReadOnlyList<MealTransferDetails>> ListAsync(Guid studentId, CancellationToken cancellationToken);
}
