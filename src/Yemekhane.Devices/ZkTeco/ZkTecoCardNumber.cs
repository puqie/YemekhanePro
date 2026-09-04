namespace Yemekhane.Devices.ZkTeco;

/// <summary>
/// Kart uzerine BASILI numara ile cihazin okudugu RFID degeri ayni olmak zorunda degildir
/// (donanim dokumantasyonu §10: "Physical printed number must not automatically be assumed to be
/// the RFID value"). Bu sinif yalnizca cihazdan gelen degeri KARSILASTIRILABILIR hale getirir;
/// basili numaradan RFID degeri TURETMEZ.
///
/// SC403 dahili okuyucusu 125 kHz proximity'dir. SDK kart numaralarini metin olarak dondurur
/// (GetStrCardNumber / SetStrCardNumber); farkli firmware surumleri ayni karti basta sifirli
/// ("0008573921") veya sifirsiz ("8573921") verebildiginden esitlik karsilastirmasi normalize
/// edilmis deger uzerinden yapilmalidir.
/// </summary>
public static class ZkTecoCardNumber
{
    /// <summary>SDK metin alani; asiri uzun degerler cihaz tarafindan kabul edilmez.</summary>
    public const int MaxLength = 20;

    /// <summary>
    /// Karsilastirma icin normalize eder: bosluklar kirpilir, tamamen rakamsa bastaki sifirlar
    /// atilir. Rakam disi karakter iceren degerler (MiFare varyantinda onaltilik olabilir)
    /// buyuk harfe cevrilir ama icerigi degistirilmez.
    /// </summary>
    public static string Normalize(string cardNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardNumber);
        var trimmed = cardNumber.Trim();

        if (!trimmed.All(char.IsAsciiDigit)) return trimmed.ToUpperInvariant();

        var trimmedZeros = trimmed.TrimStart('0');
        // Deger tamamen sifirsa ("0000") TrimStart bos dize dondurur; tek sifir korunur.
        return trimmedZeros.Length == 0 ? "0" : trimmedZeros;
    }

    /// <summary>Iki kart numarasinin ayni fiziksel karti gosterip gostermedigi.</summary>
    public static bool AreEquivalent(string left, string right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);

    /// <summary>
    /// Cihaza gonderilmeden once bicimsel dogrulama. Gecersiz bir kart numarasi cihazda kalici
    /// hata uretir; kuyrukta tutmak yalnizca gercek sorunlari gizler.
    /// </summary>
    public static void Validate(string cardNumber)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardNumber);
        var trimmed = cardNumber.Trim();
        if (trimmed.Length > MaxLength)
        {
            throw new ZkTecoProtocolException(
                $"Kart numarasi en fazla {MaxLength} karakter olabilir: '{trimmed}'.",
                isTransient: false, "ZK_INVALID_CARD");
        }

        if (!trimmed.All(static value => char.IsAsciiLetterOrDigit(value)))
        {
            throw new ZkTecoProtocolException(
                $"Kart numarasi yalnizca harf ve rakam icerebilir: '{trimmed}'.",
                isTransient: false, "ZK_INVALID_CARD");
        }
    }
}
