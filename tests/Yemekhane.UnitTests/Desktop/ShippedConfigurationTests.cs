using Microsoft.Extensions.Configuration;
using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Uygulamanin GERCEKTEN acilabildiginin dogrulanmasi.
///
/// Bu testler bir bosluktan dogdu: 1146 test yesilken uygulama HIC ACILMIYORDU.
/// Sebep, Licensing:SigningSecret'in bos yayinlanmasi ve LicenseGate'in bos
/// degerde acilisi durdurmasiydi. Hicbir test bunu goremezdi cunku tum testler
/// App.xaml.cs'i atlayip ViewModel'leri dogrudan kuruyor.
///
/// Sozlesme: imza sirri KAYNAKTA bos kalir (depoya gercek sir girerse herkes
/// sahte lisans uretebilir), yayin sirasinda enjekte edilir:
///     dotnet publish -p:LicensingSigningSecret=&lt;sir&gt;
/// </summary>
public sealed class ShippedConfigurationTests
{
    private static string SourceRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Yemekhane.sln")))
            directory = Path.GetDirectoryName(directory);
        Assert.NotNull(directory);
        return directory!;
    }

    private static IConfiguration DesktopConfiguration(string? signingSecret = null)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(SourceRoot(), "src", "Yemekhane.Desktop"))
            .AddJsonFile("appsettings.json", optional: false);

        if (signingSecret is not null)
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Licensing:SigningSecret"] = signingSecret,
            });

        return builder.Build();
    }

    /// <summary>
    /// Sir ENJEKTE EDILDIGINDE uygulama acilabilmelidir.
    ///
    /// LicenseGate.CreateService acilis yolunun ilk adimlarindandir; burada
    /// firlatirsa kullanici yalnizca bir hata kutusu gorur ve uygulama kapanir.
    /// Sirrin disindaki her ayar (ActivationUri gibi) bu testte sinanir.
    /// </summary>
    [Fact]
    public void TheDesktopStartsOnceTheSigningSecretIsInjected()
    {
        var configuration = DesktopConfiguration("yayin-sirasinda-enjekte-edilen-test-sirri");
        var dataDirectory = Path.Combine(Path.GetTempPath(), "yemekhane-lisans-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);

        try
        {
            var exception = Record.Exception(() => LicenseGate.CreateService(configuration, dataDirectory));

            Assert.True(exception is null,
                "Sır enjekte edilmiş olmasına rağmen uygulama açılmıyor: " +
                $"{exception?.Message}{Environment.NewLine}" +
                "Kullanıcı yalnızca bir hata kutusu görür ve uygulama kapanır.");
        }
        finally
        {
            try { Directory.Delete(dataDirectory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Sir YOKSA acilis durmalidir.
    ///
    /// Sessizce dogrulamasiz devam etmek, lisans korumasini anlamsiz kilardi;
    /// bu yuzden acik bir hata dogru davranistir. Ancak bu hatanin yayin
    /// sirasinda yakalanmasi gerekir -- kullanicida degil.
    /// </summary>
    [Fact]
    public void WithoutTheSecretStartupStopsInsteadOfSkippingValidation()
    {
        var configuration = DesktopConfiguration();   // kaynak hali: sir bos
        var dataDirectory = Path.Combine(Path.GetTempPath(), "yemekhane-lisans-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);

        try
        {
            var exception = Record.Exception(() => LicenseGate.CreateService(configuration, dataDirectory));

            Assert.NotNull(exception);
            Assert.Contains("SigningSecret", exception!.Message, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dataDirectory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Sirrin disindaki acilis ayarlari KAYNAKTA dolu olmalidir.</summary>
    [Theory]
    [InlineData("Api:BaseUri")]
    [InlineData("Licensing:ActivationUri")]
    public void RequiredStartupSettingsAreNotBlank(string key)
    {
        var value = DesktopConfiguration()[key];

        Assert.False(string.IsNullOrWhiteSpace(value),
            $"appsettings.json içinde '{key}' boş — uygulama açılışta durur.");
    }

    /// <summary>
    /// Imza sirri DEPOYA GIRMEMELIDIR: girerse herkes sahte lisans uretebilir
    /// ve lisans korumasi anlamini yitirir.
    /// </summary>
    [Fact]
    public void TheSigningSecretIsNotCommittedToTheRepository()
    {
        var secret = DesktopConfiguration()["Licensing:SigningSecret"];

        Assert.True(string.IsNullOrEmpty(secret),
            "Licensing:SigningSecret kaynağa yazılmış. Depodaki bir sır ile " +
            "herkes sahte lisans üretebilir; sır yayın sırasında enjekte edilmelidir " +
            "(dotnet publish -p:LicensingSigningSecret=...).");
    }

    /// <summary>
    /// Yayin adimi sirri enjekte edebilmelidir.
    ///
    /// Enjeksiyon hedefi silinirse yayinlanan uygulama ACILMAZ; bu test
    /// mekanizmanin yerinde oldugunu garanti eder.
    /// </summary>
    [Fact]
    public void ThePublishStepCanInjectTheSecret()
    {
        var project = File.ReadAllText(Path.Combine(
            SourceRoot(), "src", "Yemekhane.Desktop", "Yemekhane.Desktop.csproj"));

        Assert.Contains("LicensingSigningSecret", project, StringComparison.Ordinal);
        Assert.Contains("AfterTargets=\"Publish\"", project, StringComparison.Ordinal);
    }
}
