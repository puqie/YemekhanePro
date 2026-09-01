using System.Globalization;
using System.Windows.Data;

namespace Yemekhane.Desktop.Converters;

/// <summary>
/// Birden fazla bool girdisinin HICBIRI true degilse true dondurur; IsEnabled'e
/// baglanarak bir dugmenin BIRDEN FAZLA rakip katmandan herhangi biri acikken
/// devre disi kalmasini saglar.
///
/// Kasa ekraninda IsEnabled MIRASININ KARDES KONTEYNERLER ARASINDA calismadigi
/// bir hata yasandi: bir dugme devre disi birakilmis bir kap disina tasindiginda
/// sessizce yeniden etkinlesti. Bu yuzden her tetikleyici dugmenin IsEnabled'i
/// rakip katmanlarin durumuna DOGRUDAN baglanir -- kapsayici bir konteynerin
/// IsEnabled'ina guvenilmez.
/// </summary>
public sealed class AllInverseBoolConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.All(value => value is not true);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Bu donusum yalnizca goruntuleme icindir.");
}
