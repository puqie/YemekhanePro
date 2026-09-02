namespace Yemekhane.Domain.Entities;

/// <summary>
/// Ogunun birim ucreti (kurus). Eski programdaki "Ogun Tanim → Ucret TL" alaninin
/// karsiligi; hakedis onizlemesinde ogun bedeli/toplam ve bakiye dusumu bunu okur.
///
/// Ayri tablo: MealType siniflari kullanicinin uzerinde calistigi Entities.cs dosyasinda
/// duruyor; o dosyaya dokunmadan 1:1 yan tabloyla eklendi. Fiyati olmayan ogun 0 ₺ sayilir.
/// </summary>
public sealed class MealTypePrice
{
    public Guid MealTypeId { get; set; }
    public long PriceCents { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
