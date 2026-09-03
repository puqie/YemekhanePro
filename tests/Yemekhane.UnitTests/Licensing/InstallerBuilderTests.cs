using Yemekhane.KeyTool;

namespace Yemekhane.UnitTests.Licensing;

/// <summary>
/// Kurulum uretimi: arac, betigi kendisi cagirir. Uretimin KENDISI dakikalar surer
/// ve gercek bir derleme ister; burada test edilen, uretim oncesi kararlar --
/// depoyu bulma, surum dogrulama, hedef yol. Bunlar yanlissa uretim ya hic
/// baslamaz ya da yanlis dosyayi "hazir" diye gosterir.
/// </summary>
public sealed class InstallerBuilderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ib-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Depo kokunu derin bir alt klasorden bulabilmelidir.</summary>
    [Fact]
    public void DepoKokuAltKlasordenBulunur()
    {
        var deep = Path.Combine(root, "src", "Yemekhane.KeyTool", "bin", "Debug");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "build-installer.ps1"), "");
        File.WriteAllText(Path.Combine(root, "Yemekhane.sln"), "");

        Assert.Equal(root, InstallerBuilder.FindRepositoryRoot(deep));
    }

    /// <summary>
    /// Depo yoksa null donmelidir. Yayinlanmis klasorden calisan bir arac kurulum
    /// uretemez; kullaniciya bunu SOYLEMEK gerekir, sessizce denememek.
    /// </summary>
    [Fact]
    public void DepoYoksaNullDoner()
    {
        Directory.CreateDirectory(root);

        Assert.Null(InstallerBuilder.FindRepositoryRoot(root));
    }

    /// <summary>
    /// Yalnizca betik varken depo sayilmamalidir: iki isaret birden aranir, cunku
    /// tek bir dosyaya bakmak rastgele bir klasoru depo sanmaya yol acabilir.
    /// </summary>
    [Fact]
    public void YalnizcaBetikVarsaDepoSayilmaz()
    {
        Directory.CreateDirectory(Path.Combine(root, "scripts"));
        File.WriteAllText(Path.Combine(root, "scripts", "build-installer.ps1"), "");

        Assert.Null(InstallerBuilder.FindRepositoryRoot(root));
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.2.3")]
    [InlineData("10.20.30")]
    public void GecerliSurumKabulEdilir(string version) =>
        Assert.True(InstallerBuilder.IsValidVersion(version));

    /// <summary>
    /// Gecersiz surum ONCEDEN yakalanmalidir: betik zaten reddeder ama kullanici
    /// bunu ancak dakikalar suren uretim bastan basarisiz olunca ogrenirdi.
    /// </summary>
    [Theory]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("surum")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("1.0.0-beta")]
    public void GecersizSurumReddedilir(string? version) =>
        Assert.False(InstallerBuilder.IsValidVersion(version));

    /// <summary>Hedef yol, betigin gercekte yazdigi yerle ayni olmalidir.</summary>
    [Fact]
    public void HedefYolBetiginYazdigiYerdir()
    {
        var path = InstallerBuilder.OutputPathFor(@"C:\depo", "1.3.0");

        Assert.Equal(Path.Combine(@"C:\depo", "artifacts", "installer", "YemekhaneProKurulum-1.3.0.exe"), path);
    }

    /// <summary>
    /// Betik yoksa uretim CALISTIRILMADAN basarisiz donmelidir; PowerShell'i
    /// bosuna baslatmak, kullaniciya anlamsiz bir hata gosterirdi.
    /// </summary>
    [Fact]
    public async Task BetikYoksaCalistirilmadanBasarisizDoner()
    {
        Directory.CreateDirectory(root);

        var result = await InstallerBuilder.BuildAsync(root, "1.0.0", "acik-anahtar", CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPath);
        Assert.Contains("bulunamadi", result.Log, StringComparison.OrdinalIgnoreCase);
    }
}
