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
public sealed record EntitlementChargeRequest(
    Guid OperationId,
    IReadOnlyCollection<Guid> StudentIds,
    Guid MealTypeId,
    decimal AmountPerStudent,
    DateOnly StartsOn,
    DateOnly EndsOn,
    int DayCount,
    bool NotifyParents);

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
}

/// <summary>Hakedis ucretlerinin yazildigi gelir turu; yoksa ilk tahsilatta olusturulur.</summary>
public static class EntitlementIncomeType
{
    public const string Name = "Yemek Hakedişi";
}
