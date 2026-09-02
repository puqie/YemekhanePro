using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Yemekhane.Desktop.Controls;

/// <summary>
/// Sayfa icindeki bir karartma katmanini (scrim) PENCERENIN TAMAMINA yayar.
///
/// Cekmeceler ve onay pencereleri her ekranin KENDI Grid'inde durur; karartma
/// katmani da o Grid'i kaplar. Ekranlar MainWindow'da yan menunun yanindaki
/// sutunda ve 22/18 px kenar bosluguyla yerlestigi icin karartma yan menuyu,
/// sayfa basligini ve kenar bosluklarini kaplamiyordu: kullanici cekmece
/// acikken menunun yarisinin aydinlik kaldigini goruyordu.
///
/// Cekmeceyi pencere seviyesine tasimak (yeniden ebeveynleme) ElementName
/// baglamalarini ve DataContext kalitimini kirar. Bunun yerine karartma
/// pencerenin kok AdornerLayer'ina cizilir: adorner katmani her seyin
/// ustundedir ve pencerenin tamamini kaplar. Sayfadaki karartma Border'inin
/// kendi alani DISARIDA BIRAKILIR (delik): o alani zaten sayfadaki karartma
/// boyar ve cekmece paneli onun ustunde durur. Boylece renk iki kez binmez ve
/// panel gorunur kalir.
///
/// Kullanim: sayfadaki karartma Border'ina <c>controls:WindowScrim.Extend="True"</c>.
/// Firca o Border'in kendi Background'undan alinir; Border gorunur olunca
/// adorner eklenir, gizlenince (ya da sayfa gizlenince/kaldirilinca) kaldirilir.
/// Adorner'a sol tiklama, ayni olayi sayfadaki Border uzerinde yeniden
/// tetikler: Drawer'in "disina tiklayinca kapan" davranisi pencerenin her
/// yerinde gecerli olur.
/// </summary>
public static class WindowScrim
{
    public static readonly DependencyProperty ExtendProperty =
        DependencyProperty.RegisterAttached("Extend", typeof(bool), typeof(WindowScrim),
            new PropertyMetadata(false, OnExtendChanged));

    private static readonly DependencyProperty AdornerProperty =
        DependencyProperty.RegisterAttached("Adorner", typeof(ScrimAdorner), typeof(WindowScrim),
            new PropertyMetadata(null));

    public static bool GetExtend(DependencyObject element) => (bool)element.GetValue(ExtendProperty);

    public static void SetExtend(DependencyObject element, bool value) => element.SetValue(ExtendProperty, value);

    /// <summary>Test gozlemi icin: bu karartma su an pencere adorner'i ile yayilmis mi?</summary>
    public static bool IsExtended(DependencyObject element) => element.GetValue(AdornerProperty) is not null;

    private static void OnExtendChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        if (source is not FrameworkElement scrim) return;

        // Abonelikler adli metotlarla kurulur ki tekrar tekrar eklenmesin ve kaldirilabilsin.
        scrim.IsVisibleChanged -= OnScrimVisibleChanged;
        scrim.Unloaded -= OnScrimUnloaded;
        scrim.Loaded -= OnScrimLoaded;

        if ((bool)args.NewValue)
        {
            scrim.IsVisibleChanged += OnScrimVisibleChanged;
            scrim.Unloaded += OnScrimUnloaded;
            scrim.Loaded += OnScrimLoaded;
            if (scrim.IsVisible) Attach(scrim);
        }
        else
        {
            Detach(scrim);
        }
    }

    private static void OnScrimVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        var scrim = (FrameworkElement)sender;
        if ((bool)args.NewValue) Attach(scrim); else Detach(scrim);
    }

    private static void OnScrimLoaded(object sender, RoutedEventArgs args)
    {
        var scrim = (FrameworkElement)sender;
        if (scrim.IsVisible) Attach(scrim);
    }

    private static void OnScrimUnloaded(object sender, RoutedEventArgs args) => Detach((FrameworkElement)sender);

    private static void Attach(FrameworkElement scrim)
    {
        if (scrim.GetValue(AdornerProperty) is not null) return;

        var window = Window.GetWindow(scrim);
        if (window?.Content is not UIElement root) return;

        // Pencerenin kendi AdornerDecorator'u (Window sablonundaki) kok icerigin
        // hemen ustundedir; ScrollViewer gibi ara katmanlarin kendi adorner
        // katmanlari degil, PENCERENIN katmani istenir -- o yuzden arama kok
        // icerikten baslar.
        var layer = AdornerLayer.GetAdornerLayer(root);
        if (layer is null) return;

        var brush = scrim switch
        {
            Border border => border.Background,
            Panel panel => panel.Background,
            Control control => control.Background,
            _ => null,
        };
        if (brush is null) return;

        var adorner = new ScrimAdorner(root, scrim, brush);
        layer.Add(adorner);
        scrim.SetValue(AdornerProperty, adorner);
    }

    private static void Detach(FrameworkElement scrim)
    {
        if (scrim.GetValue(AdornerProperty) is not ScrimAdorner adorner) return;

        scrim.ClearValue(AdornerProperty);
        adorner.Release();
        AdornerLayer.GetAdornerLayer(adorner.AdornedElement)?.Remove(adorner);
    }

    /// <summary>
    /// Pencerenin tamamini sayfadaki karartma Border'inin fircasiyla boyar;
    /// Border'in kendi alanini delik birakir.
    /// </summary>
    private sealed class ScrimAdorner : Adorner
    {
        private readonly FrameworkElement scrim;
        private readonly Brush brush;
        private readonly UIElement root;
        private Rect lastHole;
        private Size lastSize;

        public ScrimAdorner(UIElement root, FrameworkElement scrim, Brush brush) : base(root)
        {
            this.root = root;
            this.scrim = scrim;
            this.brush = brush;
            IsHitTestVisible = true;
            // Pencere boyutu degisince ya da sayfa yerlesimi kayinca delik de kayar:
            // yalnizca delik/boyut GERCEKTEN degistiginde yeniden cizilir.
            root.LayoutUpdated += OnRootLayoutUpdated;
        }

        public void Release() => root.LayoutUpdated -= OnRootLayoutUpdated;

        private void OnRootLayoutUpdated(object? sender, EventArgs args)
        {
            if (HoleRect() != lastHole || AdornedElement.RenderSize != lastSize)
                InvalidateVisual();
        }

        private Rect HoleRect()
        {
            if (!scrim.IsVisible || !scrim.IsDescendantOf(AdornedElement)) return Rect.Empty;
            if (scrim.RenderSize.Width <= 0 || scrim.RenderSize.Height <= 0) return Rect.Empty;
            return scrim.TransformToAncestor(AdornedElement).TransformBounds(new Rect(scrim.RenderSize));
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            lastSize = AdornedElement.RenderSize;
            lastHole = HoleRect();

            Geometry geometry = new RectangleGeometry(new Rect(lastSize));
            if (!lastHole.IsEmpty)
                geometry = Geometry.Combine(geometry, new RectangleGeometry(lastHole), GeometryCombineMode.Exclude, null);

            drawingContext.DrawGeometry(brush, null, geometry);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
        {
            base.OnMouseLeftButtonDown(args);
            args.Handled = true;
            // Sayfadaki karartma hangi olayi dinliyorsa (Drawer: kapat; kisayol
            // yardimi: kapat) ayni olay onun uzerinde tetiklenir.
            scrim.RaiseEvent(new MouseButtonEventArgs(args.MouseDevice, args.Timestamp, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
                Source = scrim,
            });
        }
    }
}
