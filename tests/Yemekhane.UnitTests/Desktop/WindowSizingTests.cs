using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Acilis boyutu calisma alanina sigmali. Sahada 1366x768 dizustunde pencere alttan tasti:
/// XAML'deki 900 px yukseklik, gorev cubugu dusulmus 728 px'lik alana sigmiyordu.
/// </summary>
public sealed class WindowSizingTests
{
    private const double DesiredWidth = 1440, DesiredHeight = 900, MinWidth = 1024, MinHeight = 640;

    [Fact]
    public void LargeScreenKeepsTheDesiredSize()
    {
        var fit = WindowSizing.FitToWorkArea(DesiredWidth, DesiredHeight, MinWidth, MinHeight, 1920, 1040);

        Assert.Equal((DesiredWidth, DesiredHeight, MinWidth, MinHeight, false),
            (fit.Width, fit.Height, fit.MinWidth, fit.MinHeight, fit.Maximize));
    }

    [Fact]
    public void LaptopWithTaskbarShrinksTheWindowIntoTheWorkArea()
    {
        // 1366x768, gorev cubugu 40 px: calisma alani 1366x728.
        var fit = WindowSizing.FitToWorkArea(DesiredWidth, DesiredHeight, MinWidth, MinHeight, 1366, 728);

        Assert.Equal(1366 - WindowSizing.ScreenMargin, fit.Width);
        Assert.Equal(728 - WindowSizing.ScreenMargin, fit.Height);
        Assert.Equal(MinWidth, fit.MinWidth);
        Assert.Equal(MinHeight, fit.MinHeight);
        Assert.False(fit.Maximize);
    }

    /// <summary>Min boyut calisma alanindan buyukse WPF pencereyi yine Min'e buyutur; Min de kucultulmeli.</summary>
    [Fact]
    public void ScreenSmallerThanMinimumShrinksMinimumAndMaximizes()
    {
        var fit = WindowSizing.FitToWorkArea(DesiredWidth, DesiredHeight, MinWidth, MinHeight, 1024, 600);

        Assert.True(fit.Maximize);
        Assert.Equal(600 - WindowSizing.ScreenMargin, fit.MinHeight);
        Assert.Equal(600 - WindowSizing.ScreenMargin, fit.Height);
        Assert.Equal(1024 - WindowSizing.ScreenMargin, fit.MinWidth);
        Assert.Equal(1024 - WindowSizing.ScreenMargin, fit.Width);
    }

    [Fact]
    public void WorkAreaBarelyLargerThanDesiredStillLeavesTheMargin()
    {
        var fit = WindowSizing.FitToWorkArea(DesiredWidth, DesiredHeight, MinWidth, MinHeight, 1450, 910);

        Assert.Equal(1450 - WindowSizing.ScreenMargin, fit.Width);
        Assert.Equal(910 - WindowSizing.ScreenMargin, fit.Height);
        Assert.False(fit.Maximize);
    }
}
