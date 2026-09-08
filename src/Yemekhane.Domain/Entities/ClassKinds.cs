namespace Yemekhane.Domain.Entities;

/// <summary>
/// Sinif turu: okul, anasinifini normal siniflardan ayirmak istedi ("anasınıfı veya normal
/// sınıf olacak"). Deger metin olarak saklanir (GroupType / Status kaliplari gibi); yeni bir
/// tur (kres, hazirlik) sema degismeden eklenebilir. Anahtar ASCII yazilir ("Anasinifi"),
/// ekran etiketi Turkce ("Anasınıfı") — API ve klavye duzeni farkindan bagimsiz olsun diye.
/// </summary>
public static class ClassKinds
{
    public const string Normal = "Normal";
    public const string Preschool = "Anasinifi";

    public static readonly IReadOnlyList<string> All = [Normal, Preschool];

    /// <summary>Ekran etiketi; bilinmeyen/bos tur icin bos metin (sube/bolum satirlarinda sutun bos kalir).</summary>
    public static string Label(string? kind) => kind switch
    {
        Normal => "Normal sınıf",
        Preschool => "Anasınıfı",
        _ => string.Empty
    };

    /// <summary>Bos/bosluk → Normal; bilinen tur buyuk-kucuk harf duyarsiz eslenir; aksi halde null.</summary>
    public static string? Normalize(string? kind)
    {
        var value = kind?.Trim();
        if (string.IsNullOrEmpty(value)) return Normal;
        foreach (var known in All)
            if (string.Equals(known, value, StringComparison.OrdinalIgnoreCase)) return known;
        return null;
    }
}
