using System.Text.Json;

namespace Yemekhane.UnitTests.Production;

/// <summary>
/// Log HACMININ testi.
///
/// Uretimde her SQL komutunu yazmak diski doldurur: olcum sirasinda birkac
/// dakikada 1.1 GB log uretildigi gorulmustur (22 dosya x 50 MB). Okul
/// bilgisayarinin diski dolarsa yalnizca loglama degil, VERITABANI YAZMA
/// da durur -- yani sistem tamamen calisamaz hale gelir.
///
/// Bu testler yapilandirmayi dosyadan okur; boylece birinin ileride
/// EF Core kisitini silmesi aninda yakalanir.
/// </summary>
public sealed class LoggingVolumeTests
{
    /// <summary>
    /// API'nin KAYNAK ayar dosyasini okur. Test cikti klasorunde masaustunun
    /// kendi appsettings.json'i bulunur; onu okumak yanlis dosyayi dogrulamak
    /// olurdu.
    /// </summary>
    private static JsonElement Logging(string file)
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Yemekhane.sln")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var path = Path.Combine(root!, "src", "Yemekhane.Api", file);
        Assert.True(File.Exists(path), $"{path} bulunamadı.");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(document.RootElement.TryGetProperty("Logging", out var logging),
            $"{file} içinde Logging bölümü yok.");
        return logging.Clone();
    }

    private static string LevelFor(JsonElement logging, string category)
    {
        Assert.True(logging.TryGetProperty("LogLevel", out var levels),
            "Logging.LogLevel bölümü yok.");
        Assert.True(levels.TryGetProperty(category, out var value),
            $"Logging.LogLevel.\"{category}\" tanımlı değil.");
        return value.GetString() ?? "";
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Production.json")]
    public void SqlCommandsAreNotLoggedAtInformationLevel(string file)
    {
        // EF Core her SQL komutunu Information seviyesinde yazar. Kisitlanmazsa
        // normal kullanimda dakikada onlarca MB log uretilir.
        var level = LevelFor(Logging(file), "Microsoft.EntityFrameworkCore.Database.Command");

        Assert.True(level is "Warning" or "Error" or "Critical" or "None",
            $"{file}: SQL komut logu '{level}' seviyesinde; disk dolar.");
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Production.json")]
    public void EntityFrameworkIsNotVerboseByDefault(string file)
    {
        var level = LevelFor(Logging(file), "Microsoft.EntityFrameworkCore");

        Assert.True(level is "Warning" or "Error" or "Critical" or "None",
            $"{file}: EF Core logu '{level}' seviyesinde.");
    }

    [Fact]
    public void TheTotalLogFootprintIsBoundedToAReasonableSize()
    {
        // retainedFileCountLimit DOSYA SAYISIDIR, gun degil: 30 dosya x 50 MB
        // = 1.5 GB. Okul bilgisayarinda kabul edilemez.
        var logging = Logging("appsettings.Production.json");
        Assert.True(logging.TryGetProperty("File", out var file),
            "Production ayarlarında Logging.File bölümü yok.");

        var retention = file.GetProperty("RetentionDays").GetInt32();
        var sizeLimit = file.GetProperty("FileSizeLimitBytes").GetInt64();
        var worstCaseBytes = (long)retention * sizeLimit;

        const long limit = 512L * 1024 * 1024;   // 512 MB
        Assert.True(worstCaseBytes <= limit,
            $"En kötü durumda {worstCaseBytes / 1024 / 1024} MB log tutulur; " +
            $"sınır {limit / 1024 / 1024} MB. (dosya sayısı {retention} x {sizeLimit / 1024 / 1024} MB)");
    }

    /// <summary>
    /// Serilog KENDI "Serilog:MinimumLevel" bolumunu okur; ASP.NET Core'un
    /// "Logging:LogLevel" bolumu Serilog devredeyken YOK SAYILIR.
    ///
    /// Bu yuzden ayar dosyasindaki kisit tek basina YETMEZ -- olcumde ayar
    /// dosyasi dogru oldugu halde 60 istek 500 KB log uretmisti. Asil koruma
    /// koddaki MinimumLevel.Override cagrilaridir; bu test onlarin
    /// silinmedigini garanti eder.
    /// </summary>
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.AspNetCore")]
    public void NoisySourcesAreThrottledInCodeNotOnlyInConfiguration(string source)
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Yemekhane.sln")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var configuration = File.ReadAllText(Path.Combine(
            root!, "src", "Yemekhane.Api", "Infrastructure", "ProductionConfiguration.cs"));

        var expected = $"MinimumLevel.Override(\"{source}\", LogEventLevel.Warning)";
        Assert.Contains(expected, configuration, StringComparison.Ordinal);
    }
}
