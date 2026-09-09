using System.Globalization;
using System.Text.RegularExpressions;
using Yemekhane.Desktop.Converters;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Saatler HER EKRANDA ayni gorunmeli. Bir DateTimeOffset'i StringFormat ile dogrudan
/// baglamak, degeri KENDI ofsetiyle basar: kayitlar UTC saklandigi icin ekranda 3 saat
/// geri cikiyordu ("12:15'te okuttum, 09:15 yaziyor"). Ayni gecis kaydi Gunluk Takip'te
/// UTC, Ogrenci detayinda makine saati, Raporlar'da okul saati gorunuyordu.
/// </summary>
public sealed class TimeDisplayTests
{
    private static readonly TimeZoneInfo School = FindSchool();

    private static TimeZoneInfo FindSchool()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Yemekhane.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Yemekhane.sln bulunamadı.");
    }

    private static string View(string name) =>
        File.ReadAllText(Path.Combine(Root(), "src", "Yemekhane.Desktop", name));

    /// <summary>UTC kayit okul saatine cevrilir: 09:15 UTC → 12:15.</summary>
    [Fact]
    public void ConverterTurnsUtcIntoSchoolTime()
    {
        var utc = new DateTimeOffset(2026, 10, 12, 9, 15, 0, TimeSpan.Zero);

        var shown = Assert.IsType<DateTimeOffset>(new LocalTimeConverter().Convert(utc, typeof(string), null, CultureInfo.InvariantCulture));

        Assert.Equal(new TimeSpan(3, 0, 0), shown.Offset);
        Assert.Equal(12, shown.Hour);
        Assert.Equal(15, shown.Minute);
    }

    /// <summary>Kasa kayitlari +03:00 saklanir; cevrim onlari BOZMAMALI.</summary>
    [Fact]
    public void ConverterLeavesAlreadySchoolTimeValuesAlone()
    {
        var cash = new DateTimeOffset(2026, 10, 12, 12, 15, 0, TimeSpan.FromHours(3));

        var shown = Assert.IsType<DateTimeOffset>(new LocalTimeConverter().Convert(cash, typeof(string), null, CultureInfo.InvariantCulture));

        Assert.Equal(12, shown.Hour);
        Assert.Equal(15, shown.Minute);
    }

    [Fact]
    public void ConverterHandlesUtcDateTimeToo()
    {
        var utc = new DateTime(2026, 10, 12, 9, 15, 0, DateTimeKind.Utc);

        var shown = Assert.IsType<DateTime>(new LocalTimeConverter().Convert(utc, typeof(string), null, CultureInfo.InvariantCulture));

        Assert.Equal(12, shown.Hour);
    }

    /// <summary>
    /// Zaman gosteren HER baglama cevrimden gecmeli. Cevrimsiz bir StringFormat ham UTC
    /// basar ve kullanici 3 saat geri gorur; bu testin amaci o baglamalarin geri gelmemesi.
    /// </summary>
    [Theory]
    [InlineData("Views/DailyTrackingView.xaml")]
    [InlineData("Views/SmsView.xaml")]
    [InlineData("Views/DevicesView.xaml")]
    [InlineData("Views/SettingsView.xaml")]
    [InlineData("Views/CashView.xaml")]
    [InlineData("MainWindow.xaml")]
    public void EveryTimeBindingGoesThroughTheConverter(string view)
    {
        var xaml = View(view);
        var raw = new List<string>();

        // Saat iceren bir bicim ("HH:mm") kullanan ama cevrimi olmayan baglamalar.
        foreach (Match match in Regex.Matches(xaml, @"\{Binding [^}]*StringFormat=\{\}\{0:[^}]*HH:mm[^}]*\}[^}]*\}"))
            if (!match.Value.Contains("LocalTime", StringComparison.Ordinal)) raw.Add(match.Value);

        Assert.True(raw.Count == 0,
            $"{view}: {raw.Count} zaman bağlaması dönüşümsüz — ham UTC gösterir:{Environment.NewLine}"
            + string.Join(Environment.NewLine, raw));
    }

    /// <summary>Ogrenci detay sekmeleri de OKUL saatini gostermeli; once makine saatiydi.</summary>
    [Fact]
    public void StudentTabsUseSchoolTimeNotMachineTime()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Yemekhane.Desktop", "Services", "StudentTabFormatter.cs"));

        Assert.DoesNotContain("value.LocalDateTime.ToString", source, StringComparison.Ordinal);
        Assert.Contains("TimeZoneInfo.ConvertTime(value, SchoolTimeZone)", source, StringComparison.Ordinal);
    }

    /// <summary>Okul saati diliminin bulunamadigi makinede uygulama cokmemeli (yedek kimlik).</summary>
    [Fact]
    public void SchoolTimeZoneResolves() => Assert.Equal(new TimeSpan(3, 0, 0), School.GetUtcOffset(new DateTime(2026, 10, 12)));
}
