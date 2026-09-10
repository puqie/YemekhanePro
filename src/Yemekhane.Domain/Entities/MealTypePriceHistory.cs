namespace Yemekhane.Domain.Entities;

/// <summary>
/// Ogun ucretinin GECMISI: her fiyat degisikligi bir satir birakir.
///
/// <para>
/// <see cref="MealTypePrice"/> ogun basina TEK satir tutar ve degisiklikte UZERINE
/// yazilir; eski deger kayboluyordu. Hakedis satiri da odenen fiyati saklamadigi icin
/// "bu hak kac liradan verildi" bilgisi sistemde HICBIR YERDE yoktu.
/// </para>
/// <para>
/// Sonuc: Eylul'de 200 TL'den verilen haklar, Ocak'ta fiyat 300 TL olunca gecmise donuk
/// olarak 300 TL gibi raporlaniyordu. Bu tablo o soruyu cevaplanabilir kilar: bir
/// tarihte gecerli fiyat, o tarihten ONCEKI en son kaydin fiyatidir.
/// </para>
/// <para>
/// Yalnizca GECMIS icindir; gunluk okuma yollari (turnike, hakedis onizlemesi)
/// <see cref="MealTypePrice"/> uzerinden guncel fiyati okumaya devam eder. Iade de
/// buna ihtiyac duymaz: tahsilatin kendi tutari ve gun sayisi saklanir.
/// </para>
/// </summary>
public sealed class MealTypePriceHistory : Entity
{
    public Guid MealTypeId { get; set; }

    /// <summary>Bu kayittan itibaren gecerli olan ucret (kurus).</summary>
    public long PriceCents { get; set; }

    /// <summary>Fiyatin gecerli olmaya basladigi an; gecmis sorgusu bunu kullanir.</summary>
    public DateTimeOffset EffectiveFrom { get; set; }

    /// <summary>Degisiklikten ONCEKI ucret (kurus); ilk kayitta <c>null</c>.</summary>
    public long? PreviousPriceCents { get; set; }

    /// <summary>Degisikligi yapan kullanici; denetim izi.</summary>
    public Guid? ChangedBy { get; set; }
}
