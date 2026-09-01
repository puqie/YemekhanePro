using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Yemekhane.Desktop;

/// <summary>
/// Tekrar sayisi rozetini yalnizca 1'den buyukse gosterir.
///
/// Sayi dogrudan Visibility'ye baglanirsa WPF donusumu sessizce basarisiz sayar ve
/// eleman GORUNUR kalir: tek kez gelen her bildirimin yaninda anlamsiz bir "×1" cikar.
/// </summary>
public sealed class RepeatCountToVisibilityConverter : IValueConverter
{
    /// <summary>XAML'den x:Static ile erisilir; Application kurmayan testlerde de calisir.</summary>
    public static readonly RepeatCountToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
