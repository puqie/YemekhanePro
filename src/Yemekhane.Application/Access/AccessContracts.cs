namespace Yemekhane.Application.Access;

public sealed record AccessCheckRequest(string CardNumber, Guid DeviceId, Guid MealTypeId, DateTimeOffset Timestamp,
    string Direction = "Entry", string ReaderSource = "CardReader", Guid? OperatorId = null,
    Guid? OperationId = null);
public sealed record AccessSnapshot(bool CardExists, bool CardActive, Guid? StudentId, string? StudentName,
    Guid? ClassId, bool StudentActive, bool DeviceActive, Guid? EntitlementId, int Quantity, int ConsumedQuantity,
    string? EntitlementStatus, bool IsOnLeave);
public sealed record AccessDecision(string Decision, string Reason, Guid? StudentId, string? StudentName,
    Guid DeviceId, Guid MealTypeId, DateTimeOffset Timestamp, Guid OperationId);

public interface IAccessDecisionGateway
{
    Task<AccessDecision> CheckAccessAsync(AccessCheckRequest request, CancellationToken cancellationToken = default);
}

public sealed record TurnstileEventData(Guid DeviceId, Guid? OperationId, DateTimeOffset Timestamp,
    string Command, string Result, string? Error = null);

public sealed record TurnstileEventWriteResult(bool ConsumptionCompensated);

public interface ITurnstileEventStore
{
    Task<TurnstileEventWriteResult> RecordAsync(TurnstileEventData turnstileEvent,
        bool compensateConsumption, CancellationToken cancellationToken);
}

public interface IAccessDecisionRepository
{
    Task<AccessDecision?> FindDecisionAsync(Guid operationId, CancellationToken cancellationToken) =>
        Task.FromResult<AccessDecision?>(null);
    Task<AccessSnapshot> GetSnapshotAsync(string cardNumber, Guid deviceId, Guid mealTypeId, DateOnly calendarDate, CancellationToken cancellationToken);
    Task<bool> TryConsumeAndLogAsync(Guid entitlementId, AccessCheckRequest request, AccessDecision decision, CancellationToken cancellationToken);
    Task LogDeniedAsync(AccessCheckRequest request, AccessDecision decision, CancellationToken cancellationToken);
}

public sealed record AccessCacheInvalidation(Guid? StudentId = null, string? CardNumber = null, bool ClearAll = false);

public interface IAccessCacheInvalidationSink
{
    void Publish(AccessCacheInvalidation invalidation);
}
