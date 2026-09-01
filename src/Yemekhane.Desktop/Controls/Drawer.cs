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

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_Close") is ButtonBase close)
            close.Click += (_, _) => Close();

        if (GetTemplateChild("PART_Scrim") is UIElement scrim)
            scrim.MouseLeftButtonDown += (_, _) => Close();
    }

    /// <summary>Cekmeceyi kapatir ve odagi geldigi yere dondurur.</summary>
    public void Close()
    {
        IsOpen = false;
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
            drawer.Dispatcher.BeginInvoke(() => drawer.MoveFocus(
                new TraversalRequest(FocusNavigationDirection.First)));
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
