using System.Xml.Linq;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Giris penceresi kapaninca uygulamanin kapanmamasi.
///
/// WPF'in varsayilan ShutdownMode degeri OnLastWindowClose'dur. Giris penceresi ShowDialog()
/// ile acilir ve KAPANDIGI ANDA uygulamanin tek penceresidir -- ana pencere henuz Show()
/// edilmemistir. WPF "son pencere kapandi" deyip uygulamayi kapatir.
///
/// Kullanicinin gordugu: kullanici adi ve parolayi yazip "Giris yap" a basiyor, giris
/// BASARILI oluyor, sonra uygulama kayboluyor. Hicbir hata mesaji yok, cikis kodu 0.
/// Sahada gozlenen belirti tam olarak budur.
///
/// Cozum: ShutdownMode="OnExplicitShutdown" -- uygulama yalnizca Shutdown() cagrildiginda
/// veya ana pencere kapandiginda kapanir.
/// </summary>
public sealed class ShutdownModeTests
{
    [Fact]
    public void ApplicationDoesNotExitWhenTheLoginDialogCloses()
    {
        var document = XDocument.Load(Path.Combine(FindRoot(), "src", "Yemekhane.Desktop", "App.xaml"));
        var application = document.Root!;
        var shutdownMode = (string?)application.Attribute("ShutdownMode");

        Assert.Equal("OnExplicitShutdown", shutdownMode);
    }

    [Fact]
    public void StartupExplicitlyShutsDownWhenLoginIsCancelled()
    {
        // OnExplicitShutdown ile artik kimse uygulamayi bizim yerimize kapatmaz:
        // giris iptal edildiginde Shutdown() ACIKCA cagrilmalidir, yoksa uygulama
        // penceresiz ve gorunmez bir sekilde asili kalir.
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "Yemekhane.Desktop", "App.xaml.cs"));

        Assert.Contains("Shutdown()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowIsAssignedBeforeTheLoginDialogWouldCloseTheApp()
    {
        // MainWindow atanmadan giris penceresi kapanirsa uygulamanin "ana penceresi" yoktur.
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "Yemekhane.Desktop", "App.xaml.cs"));
        var showDialog = source.IndexOf("ShowDialog()", StringComparison.Ordinal);
        var mainWindow = source.IndexOf("MainWindow = window", StringComparison.Ordinal);

        Assert.True(showDialog >= 0 && mainWindow >= 0, "Beklenen çağrılar bulunamadı.");
        Assert.True(mainWindow > showDialog,
            "MainWindow girişten sonra atanır; bu yüzden ShutdownMode açıkça ayarlanmalıdır.");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Solution root bulunamadı.");
    }
}
