using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Yemekhane.Desktop.Controls;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Cekmece ve onay pencerelerinin karartmasi PENCERENIN TAMAMINI kaplamali.
///
/// Ekranlar MainWindow'da yan menunun yanindaki sutunda durur; karartma
/// Border'i yalnizca ekranin kendi Grid'ini kapliyordu. Kullanici cekmece
/// acikken yan menunun ve kenar bosluklarinin aydinlik kaldigini goruyordu.
/// WindowScrim.Extend karartmayi pencere adorner katmanina yayar.
/// </summary>
[Collection("UI")]
public sealed class WindowScrimTests
{
    private static Window MakeWindow(UIElement content) => new()
    {
        Content = content,
        Width = 400,
        Height = 300,
        WindowStyle = WindowStyle.None,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
        ShowActivated = false,
        Left = -3000,
        Top = -3000,
    };

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Yan menu (100px) + sayfa Grid'i: MainWindow'un kaba iskeleti.</summary>
    private static Grid Shell(out Border sidebar, out Grid page)
    {
        sidebar = new Border { Background = Brushes.Navy };
        page = new Grid { Background = Brushes.WhiteSmoke };
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(page, 1);
        root.Children.Add(sidebar);
        root.Children.Add(page);
        return root;
    }

    private static DependencyObject? HitAt(Window window, double x, double y) =>
        VisualTreeHelper.HitTest(window, new Point(x, y))?.VisualHit;

    [Fact]
    public void KarartmaYanMenuyuDeKaplarSayfaAlaniniIkinciKezBoyamaz() =>
        UiThread.Run(() =>
        {
            var root = Shell(out var sidebar, out var page);
            var scrim = new Border { Background = new SolidColorBrush(Color.FromArgb(0x66, 0x10, 0x18, 0x20)) };
            WindowScrim.SetExtend(scrim, true);
            var panel = new Border { Background = Brushes.White, Width = 100, HorizontalAlignment = HorizontalAlignment.Right };
            page.Children.Add(scrim);
            page.Children.Add(panel);

            var window = MakeWindow(root);
            try
            {
                window.Show();
                Pump();

                Assert.True(WindowScrim.IsExtended(scrim), "karartma pencereye yayilmadi");
                // Yan menunun ustunde artik adorner var: tiklama menuye ulasmaz.
                Assert.IsAssignableFrom<Adorner>(HitAt(window, 20, 20));
                // Sayfa alani adorner'in DELIGIDIR: orada sayfanin kendi karartmasi tiklanir,
                // renk iki kez binmez...
                Assert.Same(scrim, HitAt(window, 150, 150));
                // ...ve panel gorunur/tiklanabilir kalir.
                Assert.Same(panel, HitAt(window, 350, 150));

                scrim.Visibility = Visibility.Collapsed;
                Pump();
                Assert.False(WindowScrim.IsExtended(scrim), "karartma gizlenince adorner kaldirilmadi");
                Assert.Same(sidebar, HitAt(window, 20, 20));
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public void YanMenuUstundekiKarartmayaTiklamaCekmeceyiKapatir() =>
        UiThread.Run(() =>
        {
            var root = Shell(out _, out var page);
            var drawer = new Drawer { IsOpen = true };
            page.Children.Add(drawer);

            var window = MakeWindow(root);
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Yemekhane.Desktop;component/Themes/Drawer.xaml")
            });
            try
            {
                window.Show();
                Pump();

                var hit = Assert.IsAssignableFrom<Adorner>(HitAt(window, 20, 20));
                hit.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                });

                Assert.False(drawer.IsOpen, "yan menu ustundeki karartmaya tiklama cekmeceyi kapatmadi");
            }
            finally
            {
                window.Close();
            }
        });

    private sealed class Host : INotifyPropertyChanged
    {
        private bool open;

        public bool Open
        {
            get => open;
            set { open = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Open))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>
    /// Ekranlar IsOpen'i ViewModel'in OZEL setter'li ozelligine tek yonlu baglar.
    /// Close() yerel deger yazsaydi baglama kopar, ilk "Kapat"tan sonra cekmece
    /// bir daha ACILMAZDI.
    /// </summary>
    [Fact]
    public void KapatTekYonluBaglamayiKoparmaz() =>
        UiThread.Run(() =>
        {
            var host = new Host { Open = true };
            var drawer = new Drawer { DataContext = host };
            BindingOperations.SetBinding(drawer, Drawer.IsOpenProperty,
                new Binding(nameof(Host.Open)) { Mode = BindingMode.OneWay });
            Assert.True(drawer.IsOpen);

            drawer.Close();
            Assert.False(drawer.IsOpen);

            host.Open = false;
            host.Open = true;
            Assert.True(drawer.IsOpen, "Kapat sonrasi ViewModel yeniden acinca cekmece acilmadi: baglama kopmus");
        });
}
