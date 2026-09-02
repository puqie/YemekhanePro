using System.Globalization;
using System.Windows.Data;

namespace Yemekhane.Desktop.Converters;

/// <summary>
/// UTC DateTimeOffset degerini Europe/Istanbul saatine cevirir. WPF StringFormat ofseti
/// oldugu gibi bicimler: API'den +00:00 gelen "03:09" ekranda 03:09 kaliyor, memur 06:09
/// bekliyordu. Kasa/Raporlar ViewModel'leri ayni cevrimi kendi icinde yapar; bildirim
/// merkezi paylasilan NotificationItem kaydini gosterdigi icin cevrim burada yapilir.
/// </summary>
public sealed class LocalTimeConverter : IValueConverter
{
    private static readonly TimeZoneInfo Istanbul = FindIstanbulZone();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset offset => TimeZoneInfo.ConvertTime(offset, Istanbul),
        DateTime { Kind: DateTimeKind.Utc } utc => TimeZoneInfo.ConvertTimeFromUtc(utc, Istanbul),
        _ => value
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static TimeZoneInfo FindIstanbulZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}
