namespace Yemekhane.Desktop;

/// <summary>Yuklenemeyen bir ekran ve nedeni.</summary>
public sealed record StartupFailure(string Screen, Exception Error);

/// <summary>
/// Giris sonrasi ekran yuklemesi.
///
/// Ana pencere gosterildikten sonra onlarca ekran ayni anda yuklenir. Task.WhenAll ile
/// beklemek iki soruna yol acar: yalnizca ILK hata firlatilir (kalanlar sessizce kaybolur)
/// ve bu hata baslangic try blogunda yakalandiginda uygulama, PENCERE ZATEN ACIKKEN kapanir.
/// Kullanici giris yapar ve hicbir form gormez.
///
/// Bir ekranin yuklenememesi tum uygulamayi calismaz kilmamalidir: calisan ekranlar
/// acilir, calismayanlar isimleriyle bildirilir.
/// </summary>
public static class AppStartup
{
    /// <summary>Tum ekranlari yukler; hicbiri digerini engellemez, hicbir hata yutulmaz.</summary>
    public static async Task<IReadOnlyList<StartupFailure>> LoadScreensAsync(
        IReadOnlyList<(string Screen, Task Load)> screens)
    {
        ArgumentNullException.ThrowIfNull(screens);
        var failures = new List<StartupFailure>();
        foreach (var (screen, load) in screens)
        {
            try
            {
                await load.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Her ekran tek tek beklenir ki hepsinin sonucu ogrenilebilsin;
                // yukleme islerinin kendisi zaten paralel baslatilmistir.
                failures.Add(new StartupFailure(screen, exception));
            }
        }
        return failures;
    }

    /// <summary>Kullaniciya hangi ekranlarin gelmedigini soyler; sessiz kalmak en kotusudur.</summary>
    public static string DescribeFailures(IReadOnlyList<StartupFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        if (failures.Count == 0) return string.Empty;
        var lines = failures.Select(failure => $"• {failure.Screen}: {failure.Error.Message}");
        return "Bazı ekranlar yüklenemedi. Uygulama açık kaldı; bu ekranları yenilemeyi deneyin." +
               Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}
