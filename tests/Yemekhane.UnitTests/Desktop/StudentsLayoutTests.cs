using System.Windows;
using System.Windows.Controls;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ogrenci ekraninda liste ve formun AYNI ANDA gorunur oldugunu dogrular.
///
/// Once form cekmecede aciliyordu; cekmece acilinca liste kapaniyordu.
/// Eski uygulama bu isi daha hizli yapiyordu cunku ikisi yan yanaydi.
/// </summary>
[Collection("UI")]
public sealed class StudentsLayoutTests
{
    [Fact]
    public void ListeVeFormAyniAndaGorunur() =>
        UiThread.Run(() =>
        {
            var view = new StudentsView();
            UiThread.ApplyResources(view);
            var host = new Border { Width = 1440, Height = 900, Child = view };
            host.Measure(new Size(1440, 900));
            host.Arrange(new Rect(0, 0, 1440, 900));
            host.UpdateLayout();

            var grid = (FrameworkElement)view.FindName("StudentsGrid")!;
            var form = (FrameworkElement)view.FindName("StudentFormPanel")!;

            Assert.True(grid.ActualWidth > 0, "Ogrenci listesi gorunur degil.");
            Assert.True(form.ActualWidth > 0, "Ogrenci formu gorunur degil.");
        });

    /// <summary>Kaldirilan alanlar formda bulunmamali.</summary>
    [Theory]
    [InlineData("FormNationalId")]
    [InlineData("FormAddress")]
    public void KaldirilanAlanFormdaYok(string bindingPath)
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Yemekhane.Desktop", "Views", "StudentsView.xaml"));

        Assert.DoesNotContain(bindingPath, xaml);
    }

    /// <summary>
    /// Cekmeceler kaldirildi: bu ekranda artik IsQuickDetailOpen/IsDetailOpen/IsFormOpen
    /// ile acilip kapanan bir panel olmamali. Kart okuma modali (IsCardWorkflowOpen)
    /// bilerek korunur; o gercek bir cihaz olayini bekledigi icin ayri tutulur.
    /// </summary>
    [Theory]
    [InlineData("IsQuickDetailOpen")]
    [InlineData("IsDetailOpen")]
    public void CekmeceBindingiKalmadi(string bindingPath)
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Yemekhane.Desktop", "Views", "StudentsView.xaml"));

        Assert.DoesNotContain(bindingPath, xaml);
    }

    /// <summary>
    /// Form alanlari IsFormOpen'a baglanarak duzenlenebilir olur: secili satiri
    /// gosterirken salt okunur, "Duzenle"/"Yeni Ogrenci" sonrasi yazilabilir.
    /// </summary>
    [Fact]
    public void FormAlanlariIsFormOpenaBagli()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Yemekhane.Desktop", "Views", "StudentsView.xaml"));

        Assert.Contains("IsFormOpen", xaml);
    }

    /// <summary>Kart okuma modali bilerek korunur.</summary>
    [Fact]
    public void KartOkumaModaliKorunur()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Yemekhane.Desktop", "Views", "StudentsView.xaml"));

        Assert.Contains("IsCardWorkflowOpen", xaml);
        Assert.Contains("CardWorkflowHost", xaml);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Depo koku bulunamadi.");
    }
}
