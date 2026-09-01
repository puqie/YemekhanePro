using System.Globalization;
using System.Windows.Data;

namespace Yemekhane.Desktop.Converters;

/// <summary>
/// Ham bool degerini okunabilir duruma cevirir.
/// Once ekranda "True" / "False" yaziyordu.
/// </summary>
public sealed class StatusBadgeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? "Aktif" : "Pasif";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Durum metni yalnizca goruntuleme icindir.");
}
