using System.Globalization;
using System.Text;
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
        static string? Clean(object? value) =>
            value?.ToString() is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;

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

        return builder.ToString();
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException("Kimlik metni yalnizca goruntuleme icindir.");
}
