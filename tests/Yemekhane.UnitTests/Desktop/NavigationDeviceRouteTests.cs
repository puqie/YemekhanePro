using System.Windows;
using System.Windows.Controls;
using Yemekhane.Desktop;
using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Bildirim merkezi cihaz hatalarini "devices/{id}" rotasiyla gonderir. MainWindow yalnizca
/// "devices" tam esitligine bakinca bu rota hicbir ekrana uymuyor, kullanici bildirime
/// tikladiginda Dashboard'a dusuyor ve hicbir menu ogesi secili gorunmuyordu.
/// </summary>
[Collection("UI")]
public sealed class NavigationDeviceRouteTests
{
    [Theory]
    [InlineData("devices")]
    [InlineData("devices/8ff9bf71-4047-4280-b638-db1cf88afc5f")]
    public void DeviceRouteWithIdOpensDevicesScreenAndSelectsMenuItem(string route) => UiThread.Run(() =>
    {
        var window = new MainWindow();
        UiThread.ApplyResources(window);

        window.Navigate(route);

        Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("DevicesHost")!).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("DashboardHost")!).Visibility);
        var buttons = ((Panel)window.FindName("NavigationButtons")!).Children.OfType<Button>().ToList();
        var selected = Assert.Single(buttons, NavigationSelection.GetIsSelected);
        Assert.Equal(ShellRoutes.Devices, selected.Tag);
        Assert.Equal(ShellRoutes.Devices, ((IShortcutCommandTarget)window).CurrentRoute);
    });

    /// <summary>
    /// Gercek klavyeden gelen Enter (Key.Return) palet secimini acmali. Onceden yalnizca
    /// "Enter" adi eslesiyordu; canli yolculukta Enter hicbir sey yapmiyordu.
    /// </summary>
    [Fact]
    public void RealEnterKeyOpensSelectedPaletteResult() => UiThread.Run(() =>
    {
        var window = new MainWindow { Left = -4000, Top = -4000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        UiThread.ApplyResources(window);
        var navigation = new ShellNavigationService([ShellRoutes.Reports]);
        var routes = new List<string>();
        navigation.NavigationRequested += (_, e) => routes.Add(e.Route);
        using var search = new Yemekhane.Desktop.ViewModels.GlobalSearchViewModel(new FixedApi(), navigation, new MemoryRecentStore());
        window.GlobalSearchDataContext = search;
        window.ConfigureShortcuts(new HashSet<string>());
        try
        {
            window.Show();
            search.Open(); search.Query = "rap";
            search.SearchNowAsync().GetAwaiter().GetResult();
            Assert.Equal(0, search.SelectedIndex);
            var box = (TextBox)window.FindName("GlobalSearchBox")!;
            box.Focus();
            var source = System.Windows.PresentationSource.FromVisual(box)!;
            box.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.InputManager.Current.PrimaryKeyboardDevice, source, 0, System.Windows.Input.Key.Return)
            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
            for (var i = 0; i < 20 && routes.Count == 0; i++)
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.Equal([ShellRoutes.Reports], routes);
            Assert.False(search.IsOpen);
        }
        finally { window.Close(); }
    });

    private sealed class FixedApi : IGlobalSearchApiClient
    {
        public Task<Yemekhane.Application.Search.GlobalSearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new Yemekhane.Application.Search.GlobalSearchResponse(query, [new("module", "Modüller",
                [new Yemekhane.Application.Search.SearchResultItem("module", "Raporlar", "Modülü aç", ShellRoutes.Reports, new Dictionary<string, string>(), "Report")])]));
    }

    private sealed class MemoryRecentStore : IRecentSearchStore
    {
        private readonly List<RecentSearchEntry> values = [];
        public IReadOnlyList<RecentSearchEntry> Load() => values.ToArray();
        public void Add(RecentSearchEntry entry) => values.Insert(0, entry);
    }

    [Fact]
    public void SessionExpiredLayerStaysAboveEveryScreenUntilHidden() => UiThread.Run(() =>
    {
        var window = new MainWindow();
        UiThread.ApplyResources(window);
        var layer = (FrameworkElement)window.FindName("SessionExpiredHost")!;
        Assert.Equal(Visibility.Collapsed, layer.Visibility);

        window.ShowSessionExpired();
        Assert.True(window.IsSessionExpiredVisible);
        window.Navigate(ShellRoutes.Cash);
        Assert.Equal(Visibility.Visible, layer.Visibility);

        var relogin = 0;
        window.ReloginRequested += (_, _) => relogin++;
        ((Button)window.FindName("ReloginButton")!).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(1, relogin);

        window.HideSessionExpired();
        Assert.False(window.IsSessionExpiredVisible);
    });
}
