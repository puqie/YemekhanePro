using Yemekhane.Application.Common;

namespace Yemekhane.Application.Audit;

public sealed record AuditEntry(
    string Action,
    string EntityName,
    string? EntityId,
    string Description,
    int AffectedRecords = 1,
    object? Before = null,
    object? After = null,
    Guid? BulkOperationId = null,
    string? CorrelationId = null,
    Guid? UserId = null);

public sealed record AuditLogFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    Guid? UserId = null,
    string? Action = null,
    string? Entity = null,
    Guid? BulkOperationId = null,
    string? CorrelationId = null,
    int Page = 1,
    int PageSize = 50,
    string? EntityId = null);

public sealed record AuditLogDetails(
    Guid Id,
    Guid? UserId,
    DateTimeOffset Timestamp,
    string Action,
    string EntityName,
    string? EntityId,
    string Description,
    int AffectedRecords,
    string? BeforeJson,
    string? AfterJson,
    Guid? BulkOperationId,
    string? CorrelationId);

public interface IAuditContext
{
    Guid? UserId { get; }
    string? CorrelationId { get; }
}

public interface IAuditRepository
{
    void Add(AuditEntry entry, Guid? userId, string? correlationId);
    Task<PagedResult<AuditLogDetails>> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken);
}

public interface IAuditService
{
    void Record(AuditEntry entry);
    Task<PagedResult<AuditLogDetails>> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken = default);
}

public sealed class AuditService(IAuditRepository repository, IAuditContext context) : IAuditService
{
    public void Record(AuditEntry entry) =>
        repository.Add(entry, entry.UserId ?? context.UserId, entry.CorrelationId ?? context.CorrelationId);

    public Task<PagedResult<AuditLogDetails>> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken = default)
    {
        if (filter.Page < 1 || filter.PageSize is < 1 or > 200)
            throw new RequestValidationException("Sayfa en az 1, sayfa boyutu 1-200 arasında olmalıdır.");
        if (filter.From > filter.To)
            throw new RequestValidationException("Başlangıç tarihi bitiş tarihinden sonra olamaz.");
        return repository.ListAsync(filter, cancellationToken);
    }
}
