using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Yemekhane.Desktop.Converters;

/// <summary>
/// Iki Guid?/Guid degerinin AYNI ogrenciye ait olup olmadigini Visibility'e cevirir.
/// Ikisi de dolu ve esitse Visible, aksi halde (biri bos, ya da esit degiller) Collapsed.
///
/// Ogrenciler ekraninda dogdu (Gorev 7 duzeltme turu, Kritik 2): formun salt okunur
/// blogu SelectedStudent'i (liste secimi, aninda gelir), duzenlenebilir alanlar Details'i
/// (api.GetAsync'in donusunu bekler) izler. Tek "Details null mu" kontrolu YETERSIZDIR:
/// kullanici satir A'ya tiklayip api yaniti gelmeden satir B'ye tiklarsa, SelectedStudent
/// aninda B olur ama Details bir sure A'yi tasimaya devam eder -- iki farkli ogrenci
/// ayni panelde bir arada gorunur. Bu donusturucu Details.Id ile SelectedStudent.Id'yi
/// KARSILAŞTIRIR; eslesmedikleri her an (null dahil) salt okunur blok gizlenir.
/// </summary>
public sealed class SameStudentConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return Visibility.Collapsed;

        // Details null iken "Details.Id" yolu cozulemez; WPF bu durumda o slota
        // DependencyProperty.UnsetValue verir (duz null degil). Guid? donusumu
        // UnsetValue icin de null uretir, ama once tip kontrolu yapmak niyeti
        // acikca belli eder.
        static Guid? AsGuid(object value) => value is Guid guid ? guid : null;

        var left = AsGuid(values[0]);
        var right = AsGuid(values[1]);
        return left.HasValue && right.HasValue && left.Value == right.Value
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Bu donusum yalnizca goruntuleme icindir.");
}
