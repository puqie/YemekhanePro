using System.Windows;
using System.Windows.Controls;

namespace Yemekhane.Desktop.Controls;

/// <summary>
/// Ortak sayfa iskeleti.
///
/// Her ekran ayni sekiz parcayi (baslik, alt baslik, cevrimdisi rozeti,
/// arac cubugu, yukleniyor gostergesi, bos liste yazisi, hata satiri,
/// sayfalama) ELLE kuruyordu ve zamanla birbirinden saptilar: bir ekranda
/// yukleniyor gostergesi tablonun ortasindaydi, digerinde tam sayfa
/// yaridan saydam bir katmandi; bir ekranin alt bandinda hata solda,
/// yikici dugme ortada, sayfalama sagda -- ayni satirda uc farkli hizalama.
///
/// PageShell dort bolgeyi sabitler: Header (Title/Subtitle solda, Actions
/// sagda), Filters (opsiyonel, null ise cokuyor), Content (ContentPresenter),
/// Footer (FooterLeft solda -- hata metni, FooterRight sagda -- sayfalama).
/// Bu, her ekranda AYNI konumdadir.
/// </summary>
public sealed class PageShell : ContentControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageShell),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty =
        DependencyProperty.Register(nameof(Subtitle), typeof(string), typeof(PageShell),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionsProperty =
        DependencyProperty.Register(nameof(Actions), typeof(object), typeof(PageShell),
            new PropertyMetadata(null));

    public static readonly DependencyProperty FiltersProperty =
        DependencyProperty.Register(nameof(Filters), typeof(object), typeof(PageShell),
            new PropertyMetadata(null));

    public static readonly DependencyProperty FooterLeftProperty =
        DependencyProperty.Register(nameof(FooterLeft), typeof(object), typeof(PageShell),
            new PropertyMetadata(null));

    public static readonly DependencyProperty FooterRightProperty =
        DependencyProperty.Register(nameof(FooterRight), typeof(object), typeof(PageShell),
            new PropertyMetadata(null));

    static PageShell() =>
        DefaultStyleKeyProperty.OverrideMetadata(typeof(PageShell),
            new FrameworkPropertyMetadata(typeof(PageShell)));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>Baslik satirinin sagindaki arac cubugu (dugmeler, rozetler...).</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>
    /// Opsiyonel filtre karti. Null ise sablon bolgeyi tamamen coker --
    /// bos bir kart yer kaplamaz.
    /// </summary>
    public object? Filters
    {
        get => GetValue(FiltersProperty);
        set => SetValue(FiltersProperty, value);
    }

    /// <summary>Alt bandin solu -- her ekranda ayni konumda hata metni.</summary>
    public object? FooterLeft
    {
        get => GetValue(FooterLeftProperty);
        set => SetValue(FooterLeftProperty, value);
    }

    /// <summary>Alt bandin sagi -- her ekranda ayni konumda sayfalama.</summary>
    public object? FooterRight
    {
        get => GetValue(FooterRightProperty);
        set => SetValue(FooterRightProperty, value);
    }
}
