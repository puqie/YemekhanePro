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
        return System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Rozet rengi yalnizca goruntuleme icindir.");
}
