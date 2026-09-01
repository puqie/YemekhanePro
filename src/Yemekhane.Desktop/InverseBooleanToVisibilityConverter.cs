using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Yemekhane.Desktop;

/// <summary>
/// true -> Collapsed, false -> Visible.
///
/// InverseBooleanConverter bool dondurur; Visibility bekleyen bir ozelliye baglandiginda
/// WPF donusumu sessizce basarisiz sayar ve eleman GORUNUR kalir. Gizlenmesi gereken
/// katman ekranda kalinca butonlar ust uste biner.
/// </summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    /// <summary>
    /// XAML'den x:Static ile erisilir. Kaynak sozlugu uzerinden gitmek, Application
    /// kurmayan testlerde "kaynak bulunamiyor" hatasi verirdi.
    /// </summary>
    public static readonly InverseBooleanToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed or Visibility.Hidden;
}
