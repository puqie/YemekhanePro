using System.IO;
using System.Net;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Yerel API acilis zaman asimi.
///
/// <para>
/// SAHADA GORULEN HATA: eski bir bilgisayarda program acilmiyor ve kullaniciya
/// "Uygulama başlatılamadı: A task was canceled" deniyordu. Mesaj sebebi HIC
/// soylemiyor -- .NET'in kendi Ingilizce metni.
/// </para>
/// <para>
/// Kok neden: bekleme dongusundeki <c>Task.Delay(250, token)</c> sure dolunca
/// <c>TaskCanceledException</c> firlatiyor ve bu, hemen altta duran ANLASILIR
/// Turkce <c>TimeoutException</c> mesajina ULASMADAN disari siziyordu. Ozenle
/// yazilmis aciklama hicbir zaman gosterilmiyordu.
/// </para>
/// </summary>
public sealed class LocalApiStartupTimeoutTests
{
    /// <summary>
    /// Erisilemeyen ama YEREL OLMAYAN bir adres: EnsureReadyAsync surec baslatmayi
    /// denemeden once acik bir hata verir. Boylece test gercek bir API baslatmaz.
    /// </summary>
    [Fact]
    public async Task YerelOlmayanAdresAcikTurkceHataVerir()
    {
        await using var manager = new LocalApiProcessManager(new Uri("http://192.0.2.1:5099/"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.EnsureReadyAsync());

        Assert.Contains("erişilemiyor", exception.Message, StringComparison.Ordinal);
        // Kullaniciya .NET'in Ingilizce iptal metni GOSTERILMEMELIDIR.
        Assert.DoesNotContain("canceled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Bekleme suresi eski bilgisayarlar icin YETERLI olmalidir. Ilk acilista API
    /// veritabanini olusturup gocleri uygular; 30 saniye yavas diskte yetmiyordu.
    ///
    /// <para>
    /// Bu bir hiz ayari degil UST SINIRDIR: bekleme saglik kontroluyle biter, yani
    /// hizli makinede kullanici bu sureyi hic gormez.
    /// </para>
    /// </summary>
    [Fact]
    public void AcilisZamanAsimiEskiBilgisayarlaraYeter()
    {
        var field = typeof(LocalApiProcessManager).GetField("StartupTimeout",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.True(field is not null, "StartupTimeout alani bulunamadi (yeniden adlandirilmis olabilir).");
        var timeout = (TimeSpan)field!.GetValue(null)!;

        Assert.True(timeout >= TimeSpan.FromMinutes(20),
            $"Acilis zaman asimi {timeout.TotalMinutes:0.#} dakika. Ilk acilista veritabani "
            + "olusturulup gocler uygulanir; cok yavas diskli okul bilgisayarinda bu sure "
            + "asilirsa program HIC acilmaz. Okulun beklemesi, programin hic acilmamasindan iyidir.");
    }

    /// <summary>
    /// 30 dakika beklenirken ekranda ACIKLAMA olmalidir.
    ///
    /// <para>
    /// Zaman asimini uzatmak tek basina yetmez: kullanici bos ekrana bakip programin
    /// dondugunu sanip kapatirsa yarim kalmis bir veritabani birakir. Acilis ekrani
    /// ne olup bittigini ve beklemenin uzun surebilecegini ACIKCA soyler.
    /// </para>
    /// </summary>
    [Fact]
    public void AcilisEkraniBeklemeyiAciklar()
    {
        var message = StartupWindow.InitialMessage;

        Assert.Contains("dakika", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kapatmayın", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Acilis ekrani BEKLEMEDEN ONCE gosterilip sonra KAPATILMALIDIR. Gosterilmezse
    /// kullanici bos ekrana bakar; kapatilmazsa hata penceresinin arkasinda kalir ve
    /// kullanici hala "aciliyor" sanir.
    /// </summary>
    [Fact]
    public void AcilisEkraniBaslangicAkisinaBagli()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            directory = directory.Parent;
        var source = File.ReadAllText(Path.Combine(directory!.FullName,
            "src", "Yemekhane.Desktop", "App.xaml.cs"));

        Assert.Contains("new StartupWindow()", source, StringComparison.Ordinal);
        Assert.Contains("startup.Show()", source, StringComparison.Ordinal);
        Assert.Contains("startup.Close()", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Kullanici ISTEGIYLE iptal, "baslatilamadi" hatasi olarak gosterilmemelidir:
    /// iptal bir arizadir degil, kullanicinin kararidir.
    /// </summary>
    [Fact]
    public async Task KullaniciIptaliHataPenceresiOlarakGosterilmez()
    {
        await using var manager = new LocalApiProcessManager(new Uri("http://127.0.0.1:5098/"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Iptal edilmis jeton ile cagri: OperationCanceledException beklenir,
        // TimeoutException ya da genel bir hata DEGIL.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.EnsureReadyAsync(cancelled.Token));
    }
}
