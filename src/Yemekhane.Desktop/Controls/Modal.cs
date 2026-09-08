using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Yemekhane.Desktop.Controls;

/// <summary>
/// Ortalanmis, engelleyici diyalog: <see cref="Drawer"/>'in ortadaki esi.
///
/// Once yedi ekran ayni seyi elle yaziyordu (karartma + beyaz Border + baslik + Kapat) ve
/// yedi farkli genislik, uc farkli kose yaricapi, kimi karartmasiz, kimi yalnizca sayfayi
/// karartan sonuclar cikiyordu. Uc standart olcu vardir: dar (440) onay ve kisa mesaj,
/// orta (520) form, genis (680) sihirbaz ve liste. Esc ve karartmaya tiklama kapatir
/// (<see cref="CanClose"/> false ise kapatmaz: oturum suresi doldu diyalogu gibi). Odak
/// acilista ilk alana gider, kapanista geldigi yere doner.
/// </summary>
public sealed class Modal : ContentControl
{
    public static readonly DependencyProperty IsOpenProperty =
        DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(Modal),
            new PropertyMetadata(false, OnIsOpenChanged));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(Modal),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(Modal),
            new PropertyMetadata(null));

    public static readonly DependencyProperty ModalWidthProperty =
        DependencyProperty.Register(nameof(ModalWidth), typeof(double), typeof(Modal),
            new PropertyMetadata(520d));

    public static readonly DependencyProperty FooterProperty =
        DependencyProperty.Register(nameof(Footer), typeof(object), typeof(Modal),
            new PropertyMetadata(null));

    public static readonly DependencyProperty CanCloseProperty =
        DependencyProperty.Register(nameof(CanClose), typeof(bool), typeof(Modal),
            new PropertyMetadata(true));

    public static readonly DependencyProperty CloseCommandProperty =
        DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(Modal),
            new PropertyMetadata(null));

    private IInputElement? previousFocus;
    private int openGeneration;
    private ButtonBase? closeButtonPart;
    private UIElement? scrimPart;

    static Modal() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Modal),
            new FrameworkPropertyMetadata(typeof(Modal)));

    public Modal()
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

    /// <summary>Basligin altinda kisa aciklama; bos ise satir gizlenir.</summary>
    public string? Subtitle
    {
        get => (string?)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public double ModalWidth
    {
        get => (double)GetValue(ModalWidthProperty);
        set => SetValue(ModalWidthProperty, value);
    }

    /// <summary>Alt satir (dugmeler); null ise gizlenir. Icerik kaydirilsa da alt satir sabit kalir.</summary>
    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    /// <summary>
    /// False ise Kapat dugmesi gizlenir, Esc ve karartma kapatmaz: kullanicinin bir karar
    /// vermeden gecemeyecegi diyaloglar icin (oturum suresi doldu).
    /// </summary>
    public bool CanClose
    {
        get => (bool)GetValue(CanCloseProperty);
        set => SetValue(CanCloseProperty, value);
    }

    /// <summary>Kapanma niyetini ViewModel'e bildirir; IsOpen'i diyalog kendisi false yapar.</summary>
    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // Drawer ile ayni gerekce: template yeniden uygulaninca eski abonelikler birikmesin.
        if (closeButtonPart is not null) closeButtonPart.Click -= OnCloseButtonClick;
        if (scrimPart is not null) scrimPart.MouseLeftButtonDown -= OnScrimMouseLeftButtonDown;

        closeButtonPart = GetTemplateChild("PART_Close") as ButtonBase;
        scrimPart = GetTemplateChild("PART_Scrim") as UIElement;

        if (closeButtonPart is not null) closeButtonPart.Click += OnCloseButtonClick;
        if (scrimPart is not null) scrimPart.MouseLeftButtonDown += OnScrimMouseLeftButtonDown;
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs args) => Close();

    private void OnScrimMouseLeftButtonDown(object sender, MouseButtonEventArgs args) => Close();

    /// <summary>Diyalogu kapatir (CanClose false ise hicbir sey yapmaz) ve odagi geri verir.</summary>
    public void Close()
    {
        if (!CanClose) return;
        // SetValue DEGIL: IsOpen cogunlukla tek yonlu baglidir; yerel deger baglamayi koparirdi.
        SetCurrentValue(IsOpenProperty, false);
        if (CloseCommand?.CanExecute(null) == true) CloseCommand.Execute(null);
    }

    private static void OnIsOpenChanged(DependencyObject source, DependencyPropertyChangedEventArgs args)
    {
        var modal = (Modal)source;
        var opened = (bool)args.NewValue;
        modal.Visibility = opened ? Visibility.Visible : Visibility.Collapsed;

        if (opened)
        {
            modal.previousFocus = Keyboard.FocusedElement;
            var generation = ++modal.openGeneration;
            modal.Dispatcher.BeginInvoke(() =>
            {
                if (modal.openGeneration != generation) return;
                modal.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            });
        }
        else if (modal.previousFocus is not null)
        {
            Keyboard.Focus(modal.previousFocus);
            modal.previousFocus = null;
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape || !CanClose) return;
        Close();
        args.Handled = true;
    }
}
