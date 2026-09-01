using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;

namespace Yemekhane.Desktop.Converters;

/// <summary>
/// Ogrenciyi ayirt edici bicimde yazar: "AD SOYAD · No 5371 · 6E · Kart 8352094".
///
/// Ad soyad tek basina yetmez; veride ayni isimden birden fazla kisi vardir.
/// Deger sirasi: ad, soyad, numara, sinif, kart.
/// </summary>
public sealed class StudentIdentityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        static string? Clean(object? value)
        {
            if (value is null)
                return null;
            if (ReferenceEquals(value, DependencyProperty.UnsetValue))
                return null;
            var text = value.ToString();
            return !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        }

        var first = values.Length > 0 ? Clean(values[0]) : null;
        var last = values.Length > 1 ? Clean(values[1]) : null;
        var no = values.Length > 2 ? Clean(values[2]) : null;
        var className = values.Length > 3 ? Clean(values[3]) : null;
        var card = values.Length > 4 ? Clean(values[4]) : null;

        var builder = new StringBuilder();
        var name = string.Join(' ', new[] { first, last }.Where(part => part is not null));
        if (name.Length > 0) builder.Append(name);

        void Append(string text)
        {
            if (builder.Length > 0) builder.Append(" · ");
            builder.Append(text);
        }

        if (no is not null) Append($"No {no}");
        if (className is not null) Append(className);
        if (card is not null) Append($"Kart {card}");

        var result = builder.ToString();

        // Bulmus kontrol: Baglama hatasi veya yanlislikla 5'ten az alan gecirilirse,
        // yalnizca ad soyad gosterilir -- bu converter'in var olus sebebi yok.
        // Uretim kodunda throw etmeyin (kart-yoksun ogrenciler olasi), ancak
        // hata ayiklama yapiyorsaniz sorunu gormen gerekir.
        if (result.Length > 0 && !result.Contains('·'))
        {
            Debug.WriteLine(
                "StudentIdentityConverter: Kimlikte ayirt edici alan yok. " +
                "XAML'de MultiBinding'in tum 5 kaynagi ayarlandigindan emin olun.");
        }

        return result;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Kimlik metni yalnizca goruntuleme icindir.");
}
