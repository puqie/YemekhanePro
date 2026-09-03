using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// Sunucusuz lisans anahtari: sunucu olmadan "bu anahtari satici verdi mi"
/// sorusunun yanitlanabilmesi gerekir.
/// </summary>
public sealed class OfflineLicenseKeyTests
{
    private const string Secret = "test-imza-sirri-en-az-32-bayt-olmali-1234567890";


    /// <summary>
    /// SATIS BETIGI ILE URUN AYNI HESABI YAPMALIDIR.
    ///
    /// Anahtarlar scripts/lisans-uret.ps1 ile uretilir, masaustunde bu sinif dogrular.
    /// Ikisi ayrilirsa SATILMIS anahtarlar sahada "gecersiz" gorunur ve bunu ancak
    /// musteri sikayet edince ogrenirsiniz. Asagidaki anahtarlar gercekten o betikle
    /// uretildi; bu test iki tarafi birbirine kilitler.
    /// </summary>
    [Theory]
    [InlineData("YMK-2026-HUG8-CG6C-CZ2C")]
    [InlineData("YMK-2026-RRJK-623S-9483")]
    [InlineData("YMK-2026-MX3P-67YA-VPZS")]
    public void SatisBetigininUrettigiAnahtarlarKabulEdilir(string key) =>
        Assert.True(OfflineLicenseKey.Verify(key, Secret),
            $"{key} reddedildi: lisans-uret.ps1 ile OfflineLicenseKey ayrismis. " +
            "Bu ayrisma satilmis anahtarlari sahada gecersiz kilar.");

    [Fact]
    public void UretilenAnahtarAyniSirlaDogrulanir()
    {
        var key = OfflineLicenseKey.Create(DateTimeOffset.UtcNow, Secret);

        Assert.True(OfflineLicenseKey.Verify(key, Secret));
    }

    /// <summary>
    /// ASIL KORUMA: sirri bilmeyen gecerli anahtar uretemez. Bu tutmazsa
    /// sunucusuz modun tamami anlamsizdir -- herkes kendine lisans yazar.
    /// </summary>
    [Fact]
    public void BaskaSirlaUretilenAnahtarReddedilir()
    {
        var key = OfflineLicenseKey.Create(DateTimeOffset.UtcNow, "baska-bir-sir-1234567890abcdefgh");

        Assert.False(OfflineLicenseKey.Verify(key, Secret));
    }

    [Theory]
    [InlineData("YMK-2026-AAAA-BBBB-CCCC")]   // uydurma
    [InlineData("YMK-2026-AAAA-BBBB")]        // imza blogu yok
    [InlineData("rastgele-metin")]
    [InlineData("")]
    [InlineData(null)]
    public void UydurmaAnahtarReddedilir(string? key) =>
        Assert.False(OfflineLicenseKey.Verify(key, Secret));

    /// <summary>
    /// Musteri anahtari telefonda duyup elle yazar: bosluk, kucuk harf ve eksik
    /// tire yuzunden "gecersiz" demek, dogru anahtari olan musteriyi destege yollar.
    /// </summary>
    [Fact]
    public void BoslukVeKucukHarfKabulEdilir()
    {
        var key = OfflineLicenseKey.Create(DateTimeOffset.UtcNow, Secret);
        var messy = "  " + key.ToLowerInvariant().Replace("-", " - ") + "  ";

        Assert.True(OfflineLicenseKey.Verify(messy, Secret));
    }

    /// <summary>
    /// Tek karakteri degistirilmis anahtar gecmemelidir: aksi halde musteri
    /// komsu bir kombinasyonu deneyerek gecerli anahtar bulabilirdi.
    /// </summary>
    [Fact]
    public void TekKarakterDegisimiYakalanir()
    {
        var key = OfflineLicenseKey.Create(DateTimeOffset.UtcNow, Secret);
        var characters = key.ToCharArray();
        // Son blogun ilk karakterini degistir (imza blogu).
        var index = key.LastIndexOf('-') + 1;
        characters[index] = characters[index] == 'A' ? 'B' : 'A';

        Assert.False(OfflineLicenseKey.Verify(new string(characters), Secret));
    }

    [Fact]
    public void AnahtarlarBirbirindenFarklidir()
    {
        var keys = Enumerable.Range(0, 200)
            .Select(_ => OfflineLicenseKey.Create(DateTimeOffset.UtcNow, Secret))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(200, keys.Count);
    }

    /// <summary>
    /// Karisabilecek karakterler (0/O, 1/I/L) anahtarda BULUNMAMALIDIR: anahtar
    /// telefonda okunup elle yaziliyor.
    /// </summary>
    [Fact]
    public void KarisabilecekKarakterlerKullanilmaz()
    {
        var key = OfflineLicenseKey.Create(DateTimeOffset.UtcNow, Secret);
        var body = key.Replace("-", "")[7..];   // "YMK" ve yil disindaki bloklar

        Assert.DoesNotContain('0', body);
        Assert.DoesNotContain('O', body);
        Assert.DoesNotContain('1', body);
        Assert.DoesNotContain('I', body);
        Assert.DoesNotContain('L', body);
    }
}
