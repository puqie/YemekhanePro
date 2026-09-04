using Yemekhane.Application.Common;

namespace Yemekhane.Application.Entitlements;

public sealed record BulkEntitlementRequest(IReadOnlyCollection<Guid> StudentIds, Guid MealTypeId, DateOnly StartsOn,
    DateOnly EndsOn, int Quantity = 1, bool IncludeSaturday = false, bool IncludeSunday = false, string Source = "Manual");
public sealed record BulkEntitlementResult(int StudentCount, int DayCount, int CreatedCount, int UpdatedCount);
public sealed record EntitlementDetails(Guid Id, Guid StudentId, Guid MealTypeId, DateOnly Date, int Quantity,
    int ConsumedQuantity, int RemainingQuantity, string Status, string? Source);

/// <param name="Search">
/// TEK ARAMA metni: ad, soyad, ogrenci numarasi, kart numarasi ve sinif adinda birden
/// aranir. Kullanici aradigi seyin hangi alana ait oldugunu bilmek zorunda kalmasin
/// diye eklendi; once dort ayri kutu vardi ve kart numarasini "Ogrenci no" kutusuna
/// yazan kullanici sessizce bos sonuc aliyordu.
/// </param>
public sealed record MealEntitlementQuery(
    DateOnly? StartsOn = null, DateOnly? EndsOn = null, string? StudentNo = null, string? CardNumber = null,
    string? Name = null, string? ClassName = null, Guid? GroupId = null, Guid? MealTypeId = null,
    string? Status = null, int Page = 1, int PageSize = 50, string SortBy = "date", bool Descending = true,
    string? Search = null);
public sealed record MealEntitlementListItem(Guid Id, Guid StudentId, DateOnly Date, string StudentNo,
    string? CardNumber, string MealName, string StudentName, string? ClassName, int Quantity,
    int ConsumedQuantity, int RemainingQuantity, string Status, string? Source, long Version);
public sealed record MealEntitlementSummary(int TotalQuantity, int ConsumedQuantity, int RemainingQuantity);
public sealed record MealEntitlementPage(IReadOnlyList<MealEntitlementListItem> Items, int Page, int PageSize,
    int TotalCount, MealEntitlementSummary Summary);

/// <summary>
/// Hakedis hedefi. Manuel hedefte ogrenciler kimlik (<see cref="StudentIds"/>) VEYA
/// okul numarasi (<see cref="StudentNos"/>) ile verilebilir: masaustunde kullanici
/// GUID bilemez, listeden secmedigi ogrenci icin elinde yalnizca numara vardir.
/// Iki liste birlestirilir; eslesmeyen numara istegi reddeder (sessizce atlanmaz).
/// </summary>
public sealed record EntitlementTarget(string Type, IReadOnlyCollection<Guid>? StudentIds = null,
    Guid? ClassId = null, string? Grade = null, Guid? GroupId = null, IReadOnlyCollection<string>? StudentNos = null);
public sealed record EntitlementGrantRequest(EntitlementTarget Target, Guid MealTypeId, DateOnly StartsOn,
    DateOnly EndsOn, int Quantity = 1, bool IncludeSaturday = false, bool IncludeSunday = false,
    string Source = "Manual");
public sealed record EntitlementPreview(int StudentCount, int DayCount, int RightsCount, int CreatedCount,
    int UpdatedCount, string PreviewToken);
public sealed record ApplyEntitlementGrantRequest(EntitlementGrantRequest Grant, string PreviewToken);
public sealed record CancelEntitlementsRequest(IReadOnlyCollection<Guid> EntitlementIds, int ExpectedAffectedCount);
public sealed record CancelEntitlementsResult(int CancelledCount);

public sealed record EntitlementPreviewState(int CreatedCount, int UpdatedCount, string StateHash);

public interface IMealEntitlementRepository
{
    Task<BulkEntitlementResult> UpsertBulkAsync(IReadOnlyCollection<Guid> studentIds, Guid mealTypeId,
        IReadOnlyCollection<DateOnly> dates, int quantity, string source, string? expectedStateHash,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> ResolveTargetAsync(EntitlementTarget target, CancellationToken cancellationToken);
    Task<EntitlementPreviewState> PreviewAsync(IReadOnlyCollection<Guid> studentIds, Guid mealTypeId,
        IReadOnlyCollection<DateOnly> dates, CancellationToken cancellationToken);
    Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntitlementDetails>> ListAsync(Guid studentId, DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken);
    Task<bool> TryConsumeAsync(Guid entitlementId, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid entitlementId, CancellationToken cancellationToken);
    Task<CancelEntitlementsResult> CancelBulkAsync(IReadOnlyCollection<Guid> entitlementIds, int expectedAffectedCount,
        CancellationToken cancellationToken);
}
