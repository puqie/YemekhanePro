using System.Globalization;
using System.Windows.Data;

namespace Yemekhane.Desktop;

public sealed class InverseBooleanConverter : IValueConverter
{
    /// <summary>XAML'de sözlük kaydı olmadan doğrudan kullanılabilmesi için paylaşılan örnek.</summary>
    public static readonly InverseBooleanConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is false;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is false;
}
