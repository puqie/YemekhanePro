using Yemekhane.Desktop;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Giris sonrasi ekran yuklemesinin dayanikliligi.
///
/// Ana pencere gosterildikten sonra 13 ekran ayni anda yuklenir. Bunlardan biri hata
/// verirse -- bir yetki eksigi, gecici bir API hatasi, bos bir tablo -- Task.WhenAll
/// yalnizca ILK hatayi firlatir ve digerlerini yutar. Bu hata baslangic try blogunda
/// yakalanirsa uygulama, PENCERE ZATEN ACIKKEN kapanir.
///
/// Kullanicinin gordugu: kullanici adi ve parola yazip giris yapiyor, hicbir form
/// acilmiyor. Sahada gozlenen belirti tam olarak budur.
/// </summary>
public sealed class StartupResilienceTests
{
    [Fact]
    public async Task OneFailingScreenDoesNotPreventTheOthersFromLoading()
    {
        var loaded = new List<string>();
        Task Screen(string name, bool fails) => fails
            ? Task.FromException(new InvalidOperationException($"{name} yuklenemedi"))
            : Task.Run(() => { lock (loaded) loaded.Add(name); });

        var failures = await AppStartup.LoadScreensAsync(
        [
            ("Dashboard", Screen("Dashboard", false)),
            ("Cihazlar", Screen("Cihazlar", true)),
            ("Ogrenciler", Screen("Ogrenciler", false)),
            ("Raporlar", Screen("Raporlar", true)),
        ]);

        // Calisan ekranlar yuklenmis olmali: tek bir bozuk ekran tum uygulamayi goturmemeli.
        Assert.Contains("Dashboard", loaded);
        Assert.Contains("Ogrenciler", loaded);
        // Hatalar YUTULMAMALI: kullanici hangi ekranin gelmedigini bilmelidir.
        Assert.Equal(2, failures.Count);
        Assert.Contains(failures, failure => failure.Screen == "Cihazlar");
        Assert.Contains(failures, failure => failure.Screen == "Raporlar");
    }

    [Fact]
    public async Task AllScreensHealthyReportsNoFailures()
    {
        var failures = await AppStartup.LoadScreensAsync(
        [
            ("Dashboard", Task.CompletedTask),
            ("Cihazlar", Task.CompletedTask),
        ]);

        Assert.Empty(failures);
    }

    [Fact]
    public async Task EveryFailingScreenIsReportedNotJustTheFirst()
    {
        // Task.WhenAll yalnizca ilk hatayi firlatir; kalan 12 sessizce kaybolur.
        var failures = await AppStartup.LoadScreensAsync(
        [
            ("Bir", Task.FromException(new InvalidOperationException("bir"))),
            ("Iki", Task.FromException(new InvalidOperationException("iki"))),
            ("Uc", Task.FromException(new InvalidOperationException("uc"))),
        ]);

        Assert.Equal(3, failures.Count);
    }

    [Fact]
    public void FailureSummaryNamesTheScreensSoTheUserKnowsWhatIsMissing()
    {
        var message = AppStartup.DescribeFailures(
        [
            new StartupFailure("Cihazlar", new InvalidOperationException("baglanti yok")),
            new StartupFailure("Raporlar", new InvalidOperationException("zaman asimi")),
        ]);

        Assert.Contains("Cihazlar", message, StringComparison.Ordinal);
        Assert.Contains("Raporlar", message, StringComparison.Ordinal);
    }
}
