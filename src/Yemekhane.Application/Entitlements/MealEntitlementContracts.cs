using Yemekhane.Application.Common;

namespace Yemekhane.Application.Entitlements;

public sealed record BulkEntitlementRequest(IReadOnlyCollection<Guid> StudentIds, Guid MealTypeId, DateOnly StartsOn,
    DateOnly EndsOn, int Quantity = 1, bool IncludeSaturday = false, bool IncludeSunday = false, string Source = "Manual");
/// <param name="ChargedStudents">Kasaya gelir yazilan ogrenci sayisi (ucretlendirme kapaliysa 0).</param>
/// <param name="ChargedTotal">Kasaya yazilan toplam tutar.</param>
/// <param name="NotifiedParents">SMS kuyruguna alinan veli sayisi.</param>
/// <param name="CreatedPerStudent">
/// Ogrenci basina YENI yaratilan hak gunu sayisi. Ucret bunun uzerinden hesaplanir:
/// zaten var olan bir hak guncellendiginde para ikinci kez alinmamalidir.
///
/// <para>
/// Ucret once <c>dates.Count</c> (aralikta kac gun var) uzerinden hesaplaniyordu. Ayni
/// hakedis ikinci kez verildiginde hakedis satiri upsert ile guncelleniyor ama kasaya
/// tam tutar TEKRAR yaziliyordu. Ogrenciler farkli sayida yeni gun alabildigi icin
/// (kismen ortusen aralik) tek bir sayi yetmez, ogrenci basina ayrim gerekir.
/// </para>
/// </param>
public sealed record BulkEntitlementResult(int StudentCount, int DayCount, int CreatedCount, int UpdatedCount,
    int ChargedStudents = 0, decimal ChargedTotal = 0, int NotifiedParents = 0,
    IReadOnlyDictionary<Guid, int>? CreatedPerStudent = null);
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
/// <param name="DayCount">
/// Kullanicinin istedigi GUN SAYISI. Verilirse <paramref name="EndsOn"/> yok sayilir ve
/// bitis tarihi SUNUCUDA, tatil takvimi okunarak hesaplanir: istenen sayida yemek gunu
/// bulunana kadar aralik uzar.
///
/// <para>
/// Bu alan olmadan kullanicinin niyeti sunucuya hic ulasmiyordu. Masaustu bitis tarihini
/// tatilleri BILMEDEN hesapliyor, sunucu ayni araligi tatil takvimiyle yeniden eliyordu;
/// "20 gun" sessizce 15 gune, 6.000 TL sessizce 4.500 TL'ye dusuyordu. Gun sayisi tek
/// dogru kaynak olarak sunucuya tasinir ve hesap yalnizca takvimi bilen tarafta yapilir.
/// </para>
/// </param>
public sealed record EntitlementGrantRequest(EntitlementTarget Target, Guid MealTypeId, DateOnly StartsOn,
    DateOnly EndsOn, int Quantity = 1, bool IncludeSaturday = false, bool IncludeSunday = false,
    string Source = "Manual", bool ChargeToCash = false, bool NotifyParents = false, Guid? OperationId = null,
    int? DayCount = null);
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

    /// <summary>
    /// Ogrencinin ogun bazinda ACIK hakedis donemleri: kalan ogun, son gun ve yenileme
    /// gunu. Veli telefondayken bakilacak ozet budur.
    /// </summary>
    /// <param name="today">Okul saatiyle bugun; "kalan" ve "kac gun kaldi" buna gore hesaplanir.</param>
    Task<IReadOnlyList<EntitlementPeriodSummary>> PeriodsAsync(Guid studentId, DateOnly today,
        CancellationToken cancellationToken);

    /// <summary>Hakedisi bitmek uzere olan (ve bitmis) ogrenciler; yenileme takibi icin.</summary>
    Task<IReadOnlyList<EntitlementPeriodSummary>> ExpiringAsync(ExpiringEntitlementQuery query, DateOnly today,
        CancellationToken cancellationToken);
    Task<bool> TryConsumeAsync(Guid entitlementId, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid entitlementId, CancellationToken cancellationToken);
    Task<CancelEntitlementsResult> CancelBulkAsync(IReadOnlyCollection<Guid> entitlementIds, int expectedAffectedCount,
        CancellationToken cancellationToken);
}

/// <summary>
/// Bir ogrencinin BIR OGUN icin acik hakedis donemi: "veli arayip 'kac ogun kaldi,
/// ne zaman bitiyor' diye soruyor" ihtiyaci icin. Once bu bilgi hicbir ekranda yoktu;
/// kullanici tarih araligini genisletip satirlari tek tek saymak zorunda kaliyordu.
/// </summary>
/// <param name="RemainingQuantity">
/// BUGUNDEN ITIBAREN kullanilabilir ogun sayisi. Veliye soylenecek rakam budur.
/// Gecmiste kalan kullanilmamis haklar buraya GIRMEZ: 16 Eylul'un yemegi 20 Eylul'de
/// yenmez, o hak yanmistir (bkz. <paramref name="ExpiredQuantity"/>).
/// </param>
/// <param name="ExpiredQuantity">
/// Gecmis gunlerde kullanilmadan yanan ogun sayisi. Ayri tutulur ki "kalan" sismesin
/// ama kullanici da hakkin bosa gittigini gorebilsin.
/// </param>
/// <param name="TotalQuantity">Donemin tamami (gecmis + gelecek, iptaller haric).</param>
/// <param name="ConsumedQuantity">Fiilen kullanilan ogun sayisi.</param>
/// <param name="FirstDate">Donemin ilk gunu.</param>
/// <param name="LastDate">
/// Donemin SON gunu. "Yuklemesi ne zaman bitiyor" sorusunun cevabi budur.
/// </param>
/// <param name="RenewFrom">
/// Yeni yuklemenin baslamasi gereken gun (<paramref name="LastDate"/> + 1 gun).
/// Kullanici "10 Ekim'de bitiyorsa 11 Ekim'de yenilemeliyim" hesabini kafadan
/// yapmak zorunda kalmasin diye hazir verilir.
/// </param>
/// <param name="DaysLeft">
/// Son gune kalan gun sayisi (bugun dahil degil). Donem bugun bitiyorsa 0,
/// GECMISTE bittiyse NEGATIFTIR -- "5 gun once bitmis" uyarisi buradan cikar.
/// </param>
public sealed record EntitlementPeriodSummary(
    Guid StudentId, string StudentNo, string StudentName, string? ClassName, string? ParentPhone,
    Guid MealTypeId, string MealName,
    int RemainingQuantity, int ExpiredQuantity, int TotalQuantity, int ConsumedQuantity,
    DateOnly FirstDate, DateOnly LastDate, DateOnly RenewFrom, int DaysLeft)
{
    /// <summary>Donem bugun ya da daha once bitti mi (yenileme gecikmis demektir).</summary>
    public bool IsExpired => DaysLeft < 0;
}

/// <param name="WithinDays">
/// Kac gun icinde bitecekler listelensin. Suresi COKTAN GECMIS olanlar esikten bagimsiz
/// olarak HER ZAMAN listeye girer: 10 Ekim'de bittigini 15 Ekim'de fark etmek de ayni
/// derttir, gozden kacmamalidir.
/// </param>
/// <param name="ClassKind">
/// Sinif turu suzgeci (null ise tumu). Anasinifi ayri takip edildigi icin ayirt edilebilir.
/// </param>
public sealed record ExpiringEntitlementQuery(int WithinDays = 10, Guid? MealTypeId = null,
    string? ClassKind = null, string? Search = null);
