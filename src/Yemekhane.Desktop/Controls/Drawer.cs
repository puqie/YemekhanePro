using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Yemekhane.Desktop.Controls;

/// <summary>
/// Sagdan acilan panel.
///
/// Uc standart olcu vardir: dar (400) hizli bakis ve onay icin, orta (520)
/// coklu alanli formlar icin, genis (640) form ve detay icin. Esc kapatir,
/// odak acilista ilk alana gider ve kapanista geldigi yere doner.
/// </summary>
public sealed class Drawer : ContentControl
{
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(Drawer),
            new PropertyMetadata(false, OnIsOpenChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(Drawer),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DrawerWidthProperty =
        DependencyProperty.Register(nameof(DrawerWidth), typeof(double), typeof(Drawer),
            new PropertyMetadata(400d));

    public static readonly DependencyProperty CloseCommandProperty =
        DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(Drawer),
            new PropertyMetadata(null));

    private IInputElement? previousFocus;

    /// <summary>
    /// Her acilista bir artar. Acilistaki MoveFocus cagrisi Dispatcher.BeginInvoke
    /// ile ERTELENDIGI icin, kuyrukta beklerken cekmece kapanip TEKRAR acilabilir
    /// (orn. bir liste secimi degisince cekmece kapanip farkli icerikle yeniden
    /// aciliyor). Boyle bir durumda ilk acilistan kalan gecikmis cagri, sirf
    /// IsOpen tekrar true oldugu icin, artik gecerli olmayan bir "First" hedefine
    /// atlayip cagiran tarafin ikinci acilista BILEREK verdigi odagi ezebilir.
    /// Bare "if (!IsOpen) return" bunu onlemez -- IsOpen ikinci acilista zaten
    /// true'dur. Bu yuzden her acilisin kendi "nesli" damgalanir; gecikmis
    /// cagri yalnizca KENDI neslinin hala guncel oldugunu görürse calisir.
    /// </summary>
    private int openGeneration;

    /// <summary>
    /// Test gozlemi icin: gecikmis MoveFocus(First) cagrisi FIILEN calistiginda
    /// (nesil kontrolunu gectiginde) bir artar. Kara-kutu (public API) testler
    /// bunu goremez cunku iki MoveFocus cagrisi ayni nihai elemani hedeflerse
    /// WPF ikincisinde GotFocus raise etmez -- "kac kez calisti" sorusu son
    /// odak durumundan cikarilamaz. internal oldugu icin uretim kodunun genel
    /// yuzeyini kirletmez.
    /// </summary>
    internal int MoveFocusInvocationCountForTests { get; private set; }

    static Drawer() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Drawer),
            new FrameworkPropertyMetadata(typeof(Drawer)));

    public Drawer()
    {
        Visibility = Visibility.Collapsed;
        KeyDown += OnKeyDown;
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public double DrawerWidth
    {
        get => (double)GetValue(DrawerWidthProperty);
        set => SetValue(DrawerWidthProperty, value);
    }

    /// <summary>
    /// Kapanma niyetini disariya bildirmek icin opsiyonel komut (orn. ViewModel'de
    /// iptal/temizlik islemi calistirmak). Cekmece IsOpen'i kendisi false yapar;
    /// bu komut ek bir davranis eklemek isteyenler icindir.
    /// </summary>
    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    private ButtonBase? closeButtonPart;
    private UIElement? scrimPart;

    /// <summary>
    /// Template yeniden uygulanabilir (orn. Style/Template degisimi); WPF bu
    /// durumda OnApplyTemplate'i tekrar cagirir. Onceki parcalara anonim lambda
    /// ile abone olunsaydi bu abonelikler asla kaldirilamaz ve her yeniden
    /// template uygulamasinda BIRIKIRDI -- Close() kendisi IsOpen uzerinde
    /// idempotent oldugu icin bu sessizce gizli kalirdi, ama CloseCommand
    /// birikmis abonelik sayisi kadar (N kere) calisirdi. Bu yuzden parcalar
    /// alanda tutulur ve yeniden baglamadan once ONCEKI abonelikler kaldirilir;
    /// adli metotlar kullanilir cunku anonim lambdalardan abonelik kaldirilamaz.
    /// </summary>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (closeButtonPart is not null)
            closeButtonPart.Click -= OnCloseButtonClick;
        if (scrimPart is not null)
            scrimPart.MouseLeftButtonDown -= OnScrimMouseLeftButtonDown;

        closeButtonPart = GetTemplateChild("PART_Close") as ButtonBase;
        scrimPart = GetTemplateChild("PART_Scrim") as UIElement;

        if (closeButtonPart is not null)
            closeButtonPart.Click += OnCloseButtonClick;
        if (scrimPart is not null)
            scrimPart.MouseLeftButtonDown += OnScrimMouseLeftButtonDown;
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs args) => Close();

    private void OnScrimMouseLeftButtonDown(object sender, MouseButtonEventArgs args) => Close();

    /// <summary>Cekmeceyi kapatir ve odagi geldigi yere dondurur.</summary>
    public void Close()
    {
        // SetValue DEGIL: IsOpen hemen her yerde ViewModel'deki ozel-setter'li bir
        // ozellige tek yonlu baglidir (IsOpen="{Binding IsAddOpen}"). Tek yonlu
        // baglamaya yerel deger yazmak baglamayi KOPARIR: ilk "Kapat"tan sonra
        // ViewModel IsAddOpen'i tekrar true yapsa da cekmece bir daha acilmazdi.
        // SetCurrentValue baglamayi korur; CloseCommand ViewModel'i de kapatir.
        SetCurrentValue(IsOpenProperty, false);
        if (CloseCommand?.CanExecute(null) == true)
            CloseCommand.Execute(null);
    }

    private static void OnIsOpenChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var drawer = (Drawer)source;
        var opened = (bool)args.NewValue;

        drawer.Visibility = opened ? Visibility.Visible : Visibility.Collapsed;

        if (opened)
        {
            drawer.previousFocus = Keyboard.FocusedElement;
            var generation = ++drawer.openGeneration;
            drawer.Dispatcher.BeginInvoke(() =>
            {
                // Kuyrukta beklerken cekmece kapanip tekrar acilmis olabilir:
                // bu durumda openGeneration ilerlemistir ve bu cagri artik
                // gecersizdir -- calismasi, ikinci acilista cagiran tarafin
                // BILEREK verdigi odagi ezer.
                if (drawer.openGeneration != generation) return;
                drawer.MoveFocusInvocationCountForTests++;
                drawer.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            });
        }
        else if (drawer.previousFocus is not null)
        {
            Keyboard.Focus(drawer.previousFocus);
            drawer.previousFocus = null;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;

        Close();
        args.Handled = true;
    }
}
