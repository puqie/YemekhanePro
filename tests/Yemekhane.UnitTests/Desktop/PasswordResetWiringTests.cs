using System.IO;
using System.Xml.Linq;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Sifirlama ozelliginin UCTAN UCA bagli oldugunu dogrular.
///
/// <para>
/// Bu depoda daha once tam olarak su yasandi: sunucuda uc vardi ama masaustu onu
/// HIC CAGIRMIYORDU; ozellik "yazilmis" gorunup sahada calismiyordu. Ayni sekilde
/// bir dugmenin Click isleyicisi yoksa ekran acilir, dugmeye basilir ve HICBIR SEY
/// OLMAZ -- calisan testlerle birlikte.
/// </para>
/// <para>
/// Bu testler zinciri parca parca degil, BIRBIRINE BAKARAK dogrular.
/// </para>
/// </summary>
public sealed class PasswordResetWiringTests
{
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Solution root bulunamadı.");
    }

    private static string ReadSource(params string[] parts) =>
        File.ReadAllText(Path.Combine([FindRoot(), .. parts]));

    private static XDocument LoadXaml(params string[] parts) =>
        XDocument.Parse(ReadSource(parts));

    /// <summary>
    /// Giris ekranindaki "Parolamı unuttum" dugmesi bir Click isleyicisine BAGLI
    /// olmalidir. Baglanmazsa dugme gorunur, basilir ve hicbir sey olmaz.
    /// </summary>
    [Fact]
    public void GirisEkranindakiUnuttumDugmesiIsleyiciyeBagli()
    {
        var xaml = LoadXaml("src", "Yemekhane.Desktop", "Views", "LoginWindow.xaml");

        var button = xaml.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .SingleOrDefault(element =>
                ((string?)element.Attribute("Content"))?.Contains("unuttum", StringComparison.OrdinalIgnoreCase) == true);

        Assert.True(button is not null, "Giriş ekranında 'Parolamı unuttum' düğmesi yok: parolasını unutan okul programa hiç giremez.");
        var handler = (string?)button!.Attribute("Click");
        Assert.False(string.IsNullOrWhiteSpace(handler),
            "'Parolamı unuttum' düğmesinin Click işleyicisi yok: düğme görünür, basılır ve hiçbir şey olmaz.");
        Assert.Contains($"private void {handler}(",
            ReadSource("src", "Yemekhane.Desktop", "Views", "LoginWindow.xaml.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Sifirlama penceresi istemciyi, istemci de API ucunu cagirmalidir. Zincirin
    /// herhangi bir halkasi kopuksa ozellik sahada calismaz.
    /// </summary>
    [Fact]
    public void SifirlamaZinciriPencereRestemciUcSeklindeBagli()
    {
        Assert.Contains("ResetPasswordAsync",
            ReadSource("src", "Yemekhane.Desktop", "Views", "PasswordResetWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("api/auth/reset-password",
            ReadSource("src", "Yemekhane.Desktop", "Services", "AuthenticationClient.cs"), StringComparison.Ordinal);
        Assert.Contains("reset-password",
            ReadSource("src", "Yemekhane.Api", "Controllers", "AuthenticationController.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Sifirlama penceresindeki her dugme bir isleyiciye baglanmalidir.
    /// </summary>
    [Fact]
    public void SifirlamaPenceresindekiDugmelerinHepsiBagli()
    {
        var xaml = LoadXaml("src", "Yemekhane.Desktop", "Views", "PasswordResetWindow.xaml");
        var codeBehind = ReadSource("src", "Yemekhane.Desktop", "Views", "PasswordResetWindow.xaml.cs");

        var buttons = xaml.Descendants().Where(element => element.Name.LocalName == "Button").ToList();
        Assert.NotEmpty(buttons);
        foreach (var button in buttons)
        {
            var name = (string?)button.Attribute("Content") ?? "(adsız)";
            var handler = (string?)button.Attribute("Click");
            Assert.False(string.IsNullOrWhiteSpace(handler), $"'{name}' düğmesinin Click işleyicisi yok.");
            Assert.Contains($"void {handler}(", codeBehind, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// API, acik anahtari ve parmak izini YAPILANDIRMADAN okumalidir; masaustu de
    /// bunlari gecirmelidir. Biri eksik kalirsa sifirlama her istegi reddeder ve
    /// sebebi kullaniciya "lisans gecersiz" diye YANLIS gorunur.
    /// </summary>
    [Fact]
    public void AcikAnahtarVeParmakIziApiyeGecirilir()
    {
        var program = ReadSource("src", "Yemekhane.Api", "Program.cs");
        Assert.Contains("Licensing:PublicKey", program, StringComparison.Ordinal);
        Assert.Contains("Licensing:MachineFingerprint", program, StringComparison.Ordinal);

        var manager = ReadSource("src", "Yemekhane.Desktop", "Services", "LocalApiProcessManager.cs");
        Assert.Contains("YEMEKHANE_Licensing__PublicKey", manager, StringComparison.Ordinal);
        Assert.Contains("YEMEKHANE_Licensing__MachineFingerprint", manager, StringComparison.Ordinal);

        // Masaustu bu degerleri gercekten VERMELIDIR; parametreler varsayilan null
        // oldugu icin cagri guncellenmezse sessizce bos gecerdi.
        var app = ReadSource("src", "Yemekhane.Desktop", "App.xaml.cs");
        Assert.Contains("new LocalApiProcessManager(", app, StringComparison.Ordinal);
        Assert.Contains("WindowsHardwareFingerprintReader().Read().Hashes", app, StringComparison.Ordinal);
    }

    /// <summary>
    /// Parmak izi ISTEK GOVDESINDEN alinmamalidir: alinsaydi saldirgan lisans
    /// dosyasindaki hash'leri oldugu gibi gonderip makine kontrolunu tamamen
    /// anlamsiz kilardi.
    /// </summary>
    [Fact]
    public void ParmakIziIstekGovdesindenAlinmaz()
    {
        var controller = ReadSource("src", "Yemekhane.Api", "Controllers", "AuthenticationController.cs");

        Assert.DoesNotContain("Fingerprint", controller, StringComparison.OrdinalIgnoreCase);
    }
}
