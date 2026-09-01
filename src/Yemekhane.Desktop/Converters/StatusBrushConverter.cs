using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Yemekhane.Desktop.Converters;

/// <summary>Durum rozetinin zemin rengini secer.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is true ? "SuccessSoftBrush" : "SunkenBrush";
        var app = System.Windows.Application.Current;

        // Application.Current null ise (unit test veya tasarımcı): Transparent döndür.
        // DesignSystem.xaml birleştirilmemiş ise: Debug.WriteLine ile hata bildir.
        if (app is null)
            return Brushes.Transparent;

        var brush = app.TryFindResource(key) as Brush;
        if (brush is not null)
            return brush;

        // Kaynak yoksa: hata. Üretimde Transparent (rozet görünmez ama crash etmez),
        // hata ayıklamada geliştirici farkında olur.
        Debug.WriteLine(
            $"StatusBrushConverter: '{key}' kaynagi bulunamadi. " +
            "Themes/DesignSystem.xaml baglisindan kontrol edin.");
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Rozet rengi yalnizca goruntuleme icindir.");
}
