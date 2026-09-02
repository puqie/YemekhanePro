using Yemekhane.Application.Common;

namespace Yemekhane.Application.Income;

public sealed record IncomeTypeDetails(Guid Id, string Name, bool IsActive);
public sealed record SaveIncomeTypeRequest(string Name, bool IsActive = true);

public sealed record CreateIncomeTransactionRequest(
    Guid OperationId,
    Guid? StudentId,
    string? CardNumber,
    DateTimeOffset TransactionAt,
    Guid IncomeTypeId,
    decimal Amount,
    string? Description = null);

public sealed record VoidIncomeTransactionRequest(string Reason);

public sealed record IncomeTransactionDetails(
    Guid Id,
    Guid OperationId,
    Guid? StudentId,
    string? StudentName,
    // Ad soyad tek basina ayirt edici degil (ayni adli ogrenciler var); liste ve iptal onayi
    // ogrenci numarasini da gosterir. Kartsiz ogrencide numara tek ayirt edici alandir.
    string? StudentNo,
    string? CardNumber,
    DateTimeOffset TransactionAt,
    Guid IncomeTypeId,
    string IncomeTypeName,
    decimal Amount,
    string? Description,
    Guid CreatedBy,
    bool IsVoided,
    DateTimeOffset? VoidedAt,
    Guid? VoidedBy,
    string? VoidReason,
    // Yalnizca iptal yanitinda dolar: bir bakiye yuklemesi iptal edilip ogrencinin bakiyesi
    // eksiye dustuyse kasiyer bunu hemen gormeli (para zaten harcanmis olabilir).
    string? Warning = null);

public sealed record IncomeTransactionFilter(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    Guid? IncomeTypeId = null,
    Guid? StudentId = null,
    string? CardNumber = null,
    bool? IsVoided = null,
    int Page = 1,
    int PageSize = 50);

public interface IIncomeRepository
{
    Task<IReadOnlyList<IncomeTypeDetails>> ListTypesAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<IncomeTypeDetails?> GetTypeAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> TypeNameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken);
    Task<IncomeTypeDetails> AddTypeAsync(SaveIncomeTypeRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<IncomeTypeDetails?> UpdateTypeAsync(Guid id, SaveIncomeTypeRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<bool> DeactivateTypeAsync(Guid id, Guid actorId, CancellationToken cancellationToken);
    Task<IncomeTransactionDetails> CreateTransactionAsync(CreateIncomeTransactionRequest request, Guid actorId, CancellationToken cancellationToken);
    Task<IncomeTransactionDetails?> VoidTransactionAsync(Guid id, string reason, Guid actorId, CancellationToken cancellationToken);
    Task<PagedResult<IncomeTransactionDetails>> ListTransactionsAsync(IncomeTransactionFilter filter, CancellationToken cancellationToken);
}
