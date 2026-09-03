using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// Makine kodu: musteriden saticiya giden, makineye kilitli lisans dosyasi
/// uretmek icin gereken donanim bilgisi.
///
/// Kod WhatsApp'ta elle kopyalanir; bozuk kopyalama SESSIZCE gecmemelidir --
/// aksi halde yanlis makineye kilitli bir dosya uretilir ve hata ancak musteri
/// "calismiyor" dediginde ortaya cikar.
/// </summary>
public sealed class MachineCodeTests
{
    [Fact]
    public void UretilenKodAyniParmakIzineCozulur()
    {
        var fingerprint = Fingerprint("A");

        var parsed = MachineCode.Parse(MachineCode.Create(fingerprint));

        Assert.Equal(fingerprint.Hashes, parsed);
    }

    /// <summary>
    /// Satici, kodun dogru makineye ait oldugunu musterinin ekranda gordugu
    /// kisa kimlikle karsilastirarak dogrular.
    /// </summary>
    [Fact]
    public void KodunKisaKimligiEkrandakiyleAynidir()
    {
        var fingerprint = Fingerprint("A");

        Assert.Equal(fingerprint.MachineId, MachineCode.MachineIdOf(MachineCode.Create(fingerprint)));
    }

    /// <summary>
    /// ASIL KALKAN: eksik veya bozuk kopyalanan kod REDDEDILMELIDIR.
    /// </summary>
    [Fact]
    public void EksikKopyalananKodReddedilir()
    {
        var code = MachineCode.Create(Fingerprint("A"));

        // Sondan kirpilmis (WhatsApp'ta satir kesilmesi).
        Assert.Null(MachineCode.Parse(code[..^8]));
        // Ortasindan bir karakter degismis.
        var characters = code.ToCharArray();
        characters[20] = characters[20] == 'A' ? 'B' : 'A';
        Assert.Null(MachineCode.Parse(new string(characters)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("rastgele metin")]
    [InlineData("YMK1.abc")]          // saglama yok
    [InlineData("XXXX.abc.123456")]   // yanlis onek
    public void UydurmaKodReddedilir(string? code) => Assert.Null(MachineCode.Parse(code));

    /// <summary>
    /// Musteri kodu WhatsApp'tan kopyalar; bosluk ve satir sonlari kacinilmazdir.
    /// Bunlar yuzunden "gecersiz" demek, dogru kodu olan musteriyi destege yollar.
    /// </summary>
    [Fact]
    public void BoslukVeSatirSonuTemizlenir()
    {
        var code = MachineCode.Create(Fingerprint("A"));
        var messy = "  " + code[..20] + "\r\n  " + code[20..] + "  ";

        Assert.NotNull(MachineCode.Parse(messy));
    }

    /// <summary>
    /// Kod elle kopyalandigi icin KISA olmalidir. Hash'ler onaltilik metin yerine
    /// ham bayt olarak kodlanir; aksi halde uzunluk iki katina cikardi.
    /// </summary>
    [Fact]
    public void KodMakulUzunluktadir()
    {
        var code = MachineCode.Create(Fingerprint("A"));

        Assert.True(code.Length < 200, $"Makine kodu çok uzun ({code.Length} karakter); elle kopyalanamaz.");
    }

    [Fact]
    public void FarkliMakinelerFarkliKodUretir() =>
        Assert.NotEqual(MachineCode.Create(Fingerprint("A")), MachineCode.Create(Fingerprint("B")));

    /// <summary>
    /// Donanim hic okunamiyorsa kod URETILMEZ: bos bir koddan uretilen lisans
    /// her makinede gecerli olurdu.
    /// </summary>
    [Fact]
    public void DonanimOkunamazsaKodUretilmez() =>
        Assert.Throws<InvalidOperationException>(() =>
            MachineCode.Create(new HardwareFingerprint([string.Empty, string.Empty])));

    private static HardwareFingerprint Fingerprint(string id) =>
        new([FingerprintHasher.Hash(id + "-anakart"), FingerprintHasher.Hash(id + "-disk"),
             FingerprintHasher.Hash(id + "-guid")]);
}
