using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// WPF gorsel agaci yalnizca STA is parcaciginda kurulabilir.
///
/// Uygulama kaynaklari icin Application NESNESI OLUSTURULMAZ: Application olusturuldugu
/// is parcacigina baglanir, o parcacik olunce sonraki testler olu bir dispatcher'a bakar
/// ve test ana islemi KILITLENIR. Bunun yerine kaynaklar test edilen elemanin kendi
/// sozlugune merge edilir -- is parcacigindan bagimsizdir.
/// </summary>
public static class UiThread
{
    /// <summary>Bir elemani, uygulama stilleri yuklenmis halde saran kap dondurur.</summary>
    public static Border Host(FrameworkElement element, double width, double height)
    {
        var host = new Border { Width = width, Height = height, Child = element };
        ApplyResources(host);
        return host;
    }

    /// <summary>Uygulama sozlugunu ve XAML'de beklenen takma adlari elemana yukler.</summary>
    ///
    /// <remarks>
    /// Bazi kok elemanlar (orn. MainWindow) DesignSystem.xaml'i artik KENDI
    /// Resources'ina merge eder -- diger View dosyalariyla ayni desen. Boyle bir
    /// elemana temayi TEKRAR merge etmek ayni anahtarin IKI FARKLI Style ORNEGINI
    /// dogurur (StaticResource icerik olarak esit ama referans olarak farkli):
    /// testler Assert.Same ile kiyaslarsa yanlislikla patlar. Bu yuzden tema
    /// zaten cozulebiliyorsa (elemanin kendi sozlugunde NavItem gibi bilinen bir
    /// anahtar bulunuyorsa) ikinci merge atlanir.
    /// </remarks>
    public static void ApplyResources(FrameworkElement element)
    {
        if (!element.Resources.Contains("NavItem"))
        {
            element.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Yemekhane.Desktop;component/Themes/DesignSystem.xaml")
            });
        }
        element.Resources["BooleanToVisibilityConverter"] = new BooleanToVisibilityConverter();
        element.Resources["Bool"] = new BooleanToVisibilityConverter();
        element.Resources["BoolToVisibility"] = new BooleanToVisibilityConverter();
        element.Resources["InverseBool"] = new Yemekhane.Desktop.InverseBooleanConverter();
        element.Resources["Muted"] = element.Resources["MutedBrush"];
        element.Resources["Ink"] = element.Resources["InkBrush"];
        element.Resources["Accent"] = element.Resources["AccentBrush"];
    }

    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
