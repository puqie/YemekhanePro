using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Yemekhane.Desktop;

/// <summary>
/// Secili satir sayisini goruntu tercihine cevirir: secim VARSA ozet metni,
/// secim YOKSA elle kimlik giris kutusu gorunur.
///
/// Once "Hizli Hakedis" cekmecesinde ogrenci kimlikleri her zaman ham, virgulle
/// ayrilmis bir GUID listesi olarak gosteriliyordu; 200 ogrenci secildiginde bu
/// kutu kullanilamaz hale geliyordu ve kullanici satir secmenin islevsiz
/// oldugunu saniyordu. ManualStudentIds/SetSelection makinesi zaten calisiyor
/// -- yalnizca goruntu, secim durumunu yansitmiyordu.
///
/// ConverterParameter="Invert" verilirse mantik ters cevrilir (elle giris kutusu
/// icin: sadece secim YOKKEN gorunur).
/// </summary>
public sealed class SelectionCountToVisibilityConverter : IValueConverter
{
    /// <summary>XAML'den x:Static ile erisilir; Application kurmayan testlerde de calisir.</summary>
    public static readonly SelectionCountToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hasSelection = value is int count && count > 0;
        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        var show = invert ? !hasSelection : hasSelection;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
