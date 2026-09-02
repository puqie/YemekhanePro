using Yemekhane.Application.Common;

namespace Yemekhane.Application.BulkOperations;

/// <summary>
/// Manuel kapsamda ogrenciler kimlikle (<see cref="StudentIds"/>) ya da okul
/// numarasiyla (<see cref="StudentNos"/>) verilebilir; ikisi birlestirilir.
/// Eslesmeyen numara istegi reddeder -- yanlis yazilan bir numara sessizce
/// dusseydi kullanici "5 ogrenci" beklerken 4'une islem yapilirdi.
/// </summary>
public sealed record BulkOperationScope(string Type, Guid? ScopeId = null, IReadOnlyCollection<Guid>? StudentIds = null,
    IReadOnlyCollection<string>? StudentNos = null);

public sealed record BulkCalendarOperationRequest(
    string IdempotencyKey,
    BulkOperationScope Scope,
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    IReadOnlyCollection<DateOnly>? Dates,
    Guid? MealTypeId,
    string Operation,
    string TransferBehavior,
    DateOnly? TargetDate,
    string? Description = null);

/// <summary>
/// Onizlemede etkilenecek tek hak. Ogrenci kimligi (GUID) kullaniciya hicbir sey
/// soylemez; sihirbazin onizleme tablosu ogrenciyi no + ad + sinif ile gosterebilsin
/// diye kimlik bilgileri de tasinir (ayni adli ogrenciler ancak boyle ayirt edilir).
/// </summary>
public sealed record BulkAffectedEntitlement(Guid EntitlementId, Guid StudentId, Guid MealTypeId,
    DateOnly Date, int Quantity, int ConsumedQuantity, int AffectedQuantity, long Version, DateOnly? TargetDate,
    string StudentNo = "", string StudentName = "", string? ClassName = null);

public sealed record BulkOperationPreview(int StudentCount, int EntitlementCount, int Quantity,
    int CancelledCount, int TransferredCount, IReadOnlyList<BulkAffectedEntitlement> Entitlements,
    IReadOnlyList<DateOnly> TargetDates, IReadOnlyList<string> Warnings, string PreviewToken,
    DateTimeOffset ExpiresAt);

public sealed record ApplyBulkOperationRequest(BulkCalendarOperationRequest Request, string PreviewToken);
public sealed record BulkOperationResult(Guid OperationId, string Status, int StudentCount,
    int EntitlementCount, int Quantity, int CancelledCount, int TransferredCount,
    IReadOnlyList<DateOnly> TargetDates, bool IdempotentReplay = false);
public sealed record BulkOperationHistoryItem(Guid Id, string Operation, string Status, DateTimeOffset CreatedAt,
    Guid CreatedBy, int StudentCount, int EntitlementCount, int Quantity, bool CanUndo, DateTimeOffset? RevertedAt);
public sealed record BulkOperationHistoryPage(IReadOnlyList<BulkOperationHistoryItem> Items, int Page, int PageSize, int TotalCount);
public sealed record UndoBulkOperationResult(Guid OperationId, bool Reverted, string Message);

public sealed record BulkOperationState(IReadOnlyList<Guid> ScopeStudentIds,
    IReadOnlyList<BulkAffectedEntitlement> Entitlements, int UsedEntitlementCount, string StateHash);

public interface IBulkOperationRepository
{
    Task<BulkOperationState> PreviewAsync(BulkCalendarOperationRequest request, CancellationToken cancellationToken);
    Task<BulkOperationResult?> FindIdempotentAsync(string idempotencyKey, string requestHash, CancellationToken cancellationToken);
    Task<BulkOperationResult> ApplyAsync(BulkCalendarOperationRequest request, string requestHash,
        string expectedStateHash, IReadOnlyDictionary<Guid, DateOnly> targetDates, Guid createdBy,
        CancellationToken cancellationToken);
    Task<BulkOperationHistoryPage> HistoryAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<UndoBulkOperationResult> UndoAsync(Guid operationId, Guid revertedBy, CancellationToken cancellationToken);
}
