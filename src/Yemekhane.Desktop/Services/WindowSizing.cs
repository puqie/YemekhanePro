namespace Yemekhane.Desktop.Services;

/// <summary>
/// Ana pencerenin acilis boyutunu ekranin CALISMA ALANINA (gorev cubugu haric) sigdirir.
///
/// XAML'deki 1440x900 tercih edilen boyuttur, zorunlu degil. 1366x768 bir dizustunde gorev
/// cubugu dusulunce 728 px kalir; 900 px'lik pencere alttan tasiyor ve durum cubugu ile alt
/// dugmeler ekran disinda kaliyordu. Min boyut da calisma alanini asamaz: aksi halde WPF
/// pencereyi Min'e buyutur ve tasma geri gelir; boyle ekranda pencere buyutulmus acilir.
/// </summary>
public static class WindowSizing
{
    /// <summary>Pencere kenari ile ekran kenari arasinda birakilan toplam pay (cihazdan bagimsiz birim).</summary>
    public const double ScreenMargin = 24;

    public readonly record struct Fit(double Width, double Height, double MinWidth, double MinHeight, bool Maximize);

    public static Fit FitToWorkArea(double desiredWidth, double desiredHeight, double minWidth, double minHeight,
        double workAreaWidth, double workAreaHeight)
    {
        var availableWidth = Math.Max(0, workAreaWidth - ScreenMargin);
        var availableHeight = Math.Max(0, workAreaHeight - ScreenMargin);

        var fittedMinWidth = Math.Min(minWidth, availableWidth);
        var fittedMinHeight = Math.Min(minHeight, availableHeight);
        var width = Math.Max(Math.Min(desiredWidth, availableWidth), fittedMinWidth);
        var height = Math.Max(Math.Min(desiredHeight, availableHeight), fittedMinHeight);
        var tooSmall = availableWidth < minWidth || availableHeight < minHeight;

        return new Fit(width, height, fittedMinWidth, fittedMinHeight, tooSmall);
    }
}
