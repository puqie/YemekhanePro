namespace Yemekhane.Application.Entitlements;

/// <summary>
/// Bir hakedis isleminin ogrenci basina ucretlendirmesi. Hakedis kaydinin kendisi para
/// tasimaz (bkz. <see cref="MealEntitlementService"/>); ucret ayri bir kasa islemi olarak
/// yazilir ve hakedis iptal edilince o islem de iptal edilir.
/// </summary>
/// <param name="OperationId">
/// Islem kimligi. Ayni kimlikle tekrar cagrilirsa (masaustu yeniden denemesi) ikinci kez
/// tahsilat YAZILMAZ; hakedis ekrani ayni onizleme icin ayni kimligi kullanir.
/// </param>
/// <param name="StudentIds">Ucretlendirilecek ogrenciler; her biri icin ayri kasa kaydi acilir.</param>
/// <param name="AmountPerStudent">Ogrenci basina toplam tutar (ogun bedeli x gun x adet).</param>
/// <param name="AmountOverrides">
/// Ogrenci basina tutar farkliysa (kismen ortusen hakedis: kimine 3 yeni gun, kimine 5)
/// o ogrencinin tutari buradan okunur; listede olmayan ogrenci
/// <paramref name="AmountPerStudent"/> tutarini alir, 0 olan ogrenciye tahsilat
/// YAZILMAZ.
///
/// <para>
/// Bu ayrim olmadan ucret aralik uzunlugu uzerinden hesaplaniyordu ve ayni hakedis
/// ikinci kez verildiginde -- hicbir YENI hak yaratilmadigi halde -- kasaya tam tutar
/// tekrar yaziliyordu.
/// </para>
/// </param>
public sealed record EntitlementChargeRequest(
    Guid OperationId,
    IReadOnlyCollection<Guid> StudentIds,
    Guid MealTypeId,
    decimal AmountPerStudent,
    DateOnly StartsOn,
    DateOnly EndsOn,
    int DayCount,
    bool NotifyParents,
    IReadOnlyDictionary<Guid, decimal>? AmountOverrides = null);

/// <param name="ChargedStudents">Kasaya yazilan ogrenci sayisi.</param>
/// <param name="Total">Yazilan toplam tutar.</param>
/// <param name="NotifiedParents">SMS kuyruguna alinan veli sayisi.</param>
public sealed record EntitlementChargeResult(int ChargedStudents, decimal Total, int NotifiedParents);

/// <summary>Hakedis ucretlerinin kasa tarafi. Uygulama katmanindan, hakedis yazildiktan SONRA cagrilir.</summary>
public interface IEntitlementBillingService
{
    /// <summary>Ogrenci basina tahsilat yazar ve istenirse veliye SMS kuyruklar.</summary>
    Task<EntitlementChargeResult> ChargeAsync(EntitlementChargeRequest request, Guid actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Iptal edilen hakedislerin tahsilatlarini geri alir (kasa islemi iptal edilir, silinmez).
    /// Geri alinan islem sayisini doner.
    /// </summary>
    Task<int> RefundAsync(IReadOnlyCollection<Guid> entitlementIds, Guid actorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bir iadeyi GERI ALIR: void isareti kaldirilir, kismi iadede yazilan telafi kaydi
    /// silinir. Toplu islem "Geri Al" ile geri alindiginda haklar geri gelir; tahsilat
    /// void kalirsa okul o yemegi BEDAVA vermis olur.
    /// </summary>
    Task<int> UndoRefundAsync(IReadOnlyCollection<Guid> entitlementIds, Guid actorId,
        CancellationToken cancellationToken = default);
}

/// <summary>Hakedis ucretlerinin yazildigi gelir turu; yoksa ilk tahsilatta olusturulur.</summary>
public static class EntitlementIncomeType
{
    public const string Name = "Yemek Hakedişi";
}
