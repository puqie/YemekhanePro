using System.Text;
using Yemekhane.KeyTool;
using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// Satis kaydi: sunucusuz modda kime ne sattiginizi baska hicbir yer bilmez.
/// Kayit bozulursa geri getirilemez, bu yuzden yazma/okuma dongusu testlidir.
/// </summary>
public sealed class SalesLogTests : IDisposable
{
    // Her testin KENDI kokune yazar. Ortam degiskeni degistirmek surec genelinde
    // etkilidir ve paralel kosan testler birbirinin dosyasina yazardi -- ilk
    // denemede tam bu oldu ve testler birbirinin kayitlarini sayiyordu.
    private readonly string root = Path.Combine(Path.GetTempPath(), "kt-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void YazilanKayitGeriOkunur()
    {
        var now = DateTimeOffset.Now;
        SalesLog.Append(new SaleRecord("YMK-2026-AAAA-BBBB-CCCC", "Atatürk İlkokulu", "0532 111 2233", now), root);

        var rows = SalesLog.Load(root);

        var row = Assert.Single(rows);
        Assert.Equal("YMK-2026-AAAA-BBBB-CCCC", row.Key);
        Assert.Equal("Atatürk İlkokulu", row.Customer);
        Assert.Equal("0532 111 2233", row.Note);
    }

    /// <summary>
    /// Kayitlar BIRIKIR: her uretimde dosyanin uzerine yazilsaydi onceki satislar
    /// silinir ve kime ne sattiginizi kaybederdiniz.
    /// </summary>
    [Fact]
    public void KayitlarBirikir()
    {
        for (var index = 0; index < 5; index++)
            SalesLog.Append(new SaleRecord($"YMK-2026-AAAA-BBBB-{index:D4}", $"Okul {index}", "", DateTimeOffset.Now), root);

        Assert.Equal(5, SalesLog.Load(root).Count);
    }

    /// <summary>En yeni satis basta: kullanici genellikle son urettigini arar.</summary>
    [Fact]
    public void EnYeniKayitBastaGelir()
    {
        SalesLog.Append(new SaleRecord("YMK-2026-ESKI-BBBB-CCCC", "Eski Okul", "", DateTimeOffset.Now), root);
        SalesLog.Append(new SaleRecord("YMK-2026-YENI-BBBB-CCCC", "Yeni Okul", "", DateTimeOffset.Now), root);

        Assert.Equal("YMK-2026-YENI-BBBB-CCCC", SalesLog.Load(root)[0].Key);
    }

    /// <summary>
    /// Ayirici, musteri adinin icinde gecerse sutunlar kayar ve kayit bozulur.
    /// "Ataturk Ilkokulu; Sube 2" gibi bir ad bunu tetikler.
    /// </summary>
    [Fact]
    public void AyiriciIcerenAdKaydiBozmaz()
    {
        SalesLog.Append(new SaleRecord("YMK-2026-AAAA-BBBB-CCCC",
            "Atatürk İlkokulu; Şube 2", "not; içinde ayırıcı", DateTimeOffset.Now), root);

        var row = Assert.Single(SalesLog.Load(root));
        Assert.Equal("YMK-2026-AAAA-BBBB-CCCC", row.Key);
        Assert.DoesNotContain(';', row.Customer);
        Assert.Contains("Şube 2", row.Customer, StringComparison.Ordinal);
    }

    /// <summary>
    /// Satir sonu iceren bir not, tek kaydi iki satira bolerek dosyayi bozardi.
    /// </summary>
    [Fact]
    public void SatirSonuIcerenNotKaydiBolmez()
    {
        SalesLog.Append(new SaleRecord("YMK-2026-AAAA-BBBB-CCCC", "Okul",
            "birinci satır\r\nikinci satır", DateTimeOffset.Now), root);

        Assert.Single(SalesLog.Load(root));
    }

    /// <summary>
    /// Excel BOM'suz UTF-8 dosyayi ANSI sanip Turkce harfleri bozar; satis kaydi
    /// Excel'de acilabilmelidir.
    /// </summary>
    [Fact]
    public void DosyaExcelIcinBomIleYazilir()
    {
        SalesLog.Append(new SaleRecord("YMK-2026-AAAA-BBBB-CCCC", "Şişli Şehit Öğretmen", "", DateTimeOffset.Now), root);

        var bytes = File.ReadAllBytes(SalesLog.Resolve(root));
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "UTF-8 BOM yok: Excel Türkçe harfleri bozuk gösterir.");
        Assert.Contains("Şişli", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    /// <summary>
    /// ARACIN TAM AKISI: sir kaydet -> anahtar uret -> kayda yaz -> masaustunde dogrula.
    ///
    /// Pencereyi surmek yerine ayni cagrilari yapar; UI otomasyonu WPF PasswordBox'ta
    /// guvenilir degildir ve testin asil sordugu sey mantiktir: uretilen anahtar
    /// gercekten musterinin kurulumunda gecerli mi?
    /// </summary>
    [Fact]
    public void UretilenAnahtarMusterininKurulumundaGecerlidir()
    {
        const string secret = "arac-deneme-sirri-en-az-32-bayt-1234567890";

        var key = OfflineLicenseKey.Create(DateTimeOffset.Now, secret);
        SalesLog.Append(new SaleRecord(key, "Atatürk İlkokulu", "0532 111 2233", DateTimeOffset.Now), root);

        Assert.True(OfflineLicenseKey.Verify(key, secret),
            "Aracin urettigi anahtar musterinin kurulumunda reddedildi.");

        var row = Assert.Single(SalesLog.Load(root));
        Assert.Equal(key, row.Key);
        Assert.Equal("Atatürk İlkokulu", row.Customer);
    }

    /// <summary>
    /// BASKA sirla uretilen anahtar musterinin kurulumunda REDDEDILMELIDIR: aksi halde
    /// yanlis sirla uretilen anahtarlari satar, sorunu ancak musteri sikayet edince
    /// ogrenirdiniz.
    /// </summary>
    [Fact]
    public void YanlisSirlaUretilenAnahtarKurulumdaReddedilir()
    {
        var key = OfflineLicenseKey.Create(DateTimeOffset.Now, "yanlis-sir-1234567890abcdefghij");

        Assert.False(OfflineLicenseKey.Verify(key, "dogru-sir-1234567890abcdefghij"));
    }

    [Fact]
    public void KayitYokkenBosListeDoner() => Assert.Empty(SalesLog.Load(root));
}
