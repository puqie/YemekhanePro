using Yemekhane.Application.Common;

namespace Yemekhane.Application.Entitlements;

public sealed record BulkEntitlementRequest(IReadOnlyCollection<Guid> StudentIds, Guid MealTypeId, DateOnly StartsOn,
    DateOnly EndsOn, int Quantity = 1, bool IncludeSaturday = false, bool IncludeSunday = false, string Source = "Manual");
/// <param name="ChargedStudents">Kasaya gelir yazilan ogrenci sayisi (ucretlendirme kapaliysa 0).</param>
/// <param name="ChargedTotal">Kasaya yazilan toplam tutar.</param>
/// <param name="NotifiedParents">SMS kuyruguna alinan veli sayisi.</param>
public sealed record BulkEntitlementResult(int StudentCount, int DayCount, int CreatedCount, int UpdatedCount,
    int ChargedStudents = 0, decimal ChargedTotal = 0, int NotifiedParents = 0);
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
/// <param name="ChargeToCash">
/// Ogun bedeli kasaya OGRENCI BASINA gelir olarak islensin mi. Hakedis kaydinin kendisi
/// para tasimaz; ucret ayri bir kasa islemi olur ve hakedis iptal edilince geri alinir.
/// </param>
/// <param name="NotifyParents">Veliye "hakkiniz tanimlandi" SMS'i kuyruklansin mi.</param>
/// <param name="OperationId">
/// Tekrar denemede ayni tahsilatin ikinci kez yazilmamasi icin islem kimligi; masaustu
/// ayni onizleme icin ayni kimligi gonderir.
/// </param>
public sealed record EntitlementGrantRequest(EntitlementTarget Target, Guid MealTypeId, DateOnly StartsOn,
    DateOnly EndsOn, int Quantity = 1, bool IncludeSaturday = false, bool IncludeSunday = false,
    string Source = "Manual", bool ChargeToCash = false, bool NotifyParents = false, Guid? OperationId = null);
/// <param name="AmountPerStudent">Ogrenci basina toplam bedel (ogun ucreti x gun x adet); ucretsiz ogunde 0.</param>
/// <param name="Total">Butun ogrenciler icin toplam; ekranda "Toplam bedel" olarak gorunur.</param>
public sealed record EntitlementPreview(int StudentCount, int DayCount, int RightsCount, int CreatedCount,
    int UpdatedCount, string PreviewToken, decimal AmountPerStudent = 0, decimal Total = 0);
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
    /// <summary>Ogunun birim ucreti (₺); tanimlanmamissa 0 (ucretsiz ogun).</summary>
    Task<decimal> MealPriceAsync(Guid mealTypeId, CancellationToken cancellationToken);
    Task<EntitlementPreviewState> PreviewAsync(IReadOnlyCollection<Guid> studentIds, Guid mealTypeId,
        IReadOnlyCollection<DateOnly> dates, CancellationToken cancellationToken);
    Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<EntitlementDetails>> ListAsync(Guid studentId, DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken);
    Task<bool> TryConsumeAsync(Guid entitlementId, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid entitlementId, CancellationToken cancellationToken);
    Task<CancelEntitlementsResult> CancelBulkAsync(IReadOnlyCollection<Guid> entitlementIds, int expectedAffectedCount,
        CancellationToken cancellationToken);
}
