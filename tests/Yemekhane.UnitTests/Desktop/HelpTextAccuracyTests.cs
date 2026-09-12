using System.Text.RegularExpressions;
using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Yardim metinleri (F1 sayfa notlari ve AI kilavuzu) ekrandaki gercek etiketleri anlatmali.
/// Denetimde bulunan yaniltici ifadeler (olmayan "Cihaz Günlükleri" sekmesi, "Tarih belirle"
/// secenegi, "sağ üst" arama kutusu, "örnek şablon indir" dugmesi, yanlis sekme adlari) burada
/// kilitlenir; ayrica kilavuzun andigi kenar cubugu, sekme ve dugme adlari XAML'da aranir.
/// </summary>
public sealed class HelpTextAccuracyTests
{
    private static readonly string Root = FindRoot();
    /// <summary>Kilavuzda etiketler satir sonunda bolunebilir; bosluklar tek bosluga indirgenir.</summary>
    private static string Guide { get; } = Regex.Replace(AiUserGuidePrompt.Text, @"\s+", " ");
    private static string AllPageHelp => string.Join("\n", PageHelpTexts.ByRoute.Values.SelectMany(x => x.Bullets));

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Yemekhane.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Yemekhane.sln bulunamadı.");
    }

    private static string Xaml(string view) => File.ReadAllText(Path.Combine(Root, "src", "Yemekhane.Desktop", view));

    [Theory]
    [InlineData("Cihaz Günlükleri\" sekmesinden")]
    [InlineData("Tarih belirle")]
    [InlineData("Genel arama (sağ üst)")]
    [InlineData("örnek şablonu indirip")]
    [InlineData("Erişim Geçmişi")]
    [InlineData("Yanmasına izin ver")]
    [InlineData("Belirli tarihe aktar")]
    [InlineData("Manuel/Sınıf/Grup/Filtre")]
    [InlineData("periyodik güncellenir")]
    public void MisleadingPhrasesAreGone(string phrase)
    {
        Assert.DoesNotContain(phrase, Guide, StringComparison.Ordinal);
        Assert.DoesNotContain(phrase, AllPageHelp, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MainWindow.xaml", "Genel Bakış", "Günlük Takip", "Öğrenciler", "Kartlar", "Kasa", "Yemek Hakedişleri", "Takvim / Tatil", "Sicil Aktar", "Tanımlar", "Cihazlar / Turnikeler", "Kart Yükleme Durumu", "SMS Merkezi", "Raporlar", "Ayarlar", "Kısayollar  F1", "Öğrenci, kart, sınıf, tarih veya modül ara...")]
    [InlineData("Views/SettingsView.xaml", "Yardım / AI Kılavuzu", "Panoya Kopyala", "Cihazlar / Kart Okuyucular", "SMS sınama", "Test SMS Gönder", "Kontör Sorgula", "Veliye yemek hakkı uyarısı", "Gelir girişinde yetkiliye bildirim", "Kart yenileme", "Gönderim saati (SS:dd)", "Logları Yenile", "Şimdi Senkronize Et")]
    [InlineData("Views/CashView.xaml", "Anasınıfı Ücretleri", "Öğrenci Ekstresi", "Öğrenciye bağlı olmayan gelir", "PDF Kaydet", "Planı Kaydet", "Yeni Plan")]
    [InlineData("Views/DevicesView.xaml", "Loglar", "Cihaz logları")]
    [InlineData("Views/StudentImportView.xaml", "Hata Raporunu İndir", "Baştan Başla")]
    [InlineData("Views/SmsView.xaml", "Öğrenci ara (no, ad, soyad)", "Filtrele / Yenile", "SMS'leri kuyruğa al", "Pasifleri göster")]
    [InlineData("Views/LoginWindow.xaml", "Parolamı unuttum", "Giriş yap")]
    [InlineData("Views/PasswordResetWindow.xaml", "Lisans dosyası seç (.lic)", "Parolayı sıfırla")]
    [InlineData("Views/ActivationWindow.xaml", "Lisans anahtarı", "Etkinleştir", "Makine kodunu kopyala")]
    public void GuideLabelsExistInXaml(string view, params string[] labels)
    {
        var xaml = Xaml(view);
        foreach (var label in labels)
        {
            Assert.Contains(label, xaml, StringComparison.Ordinal);
            Assert.Contains(Regex.Replace(label, @"\s+", " "), Guide, StringComparison.Ordinal);
        }
    }

    /// <summary>Sihirbaz/tatil secenek adlari ceviriciden gelir; kilavuz gercek Turkce etiketleri kullanmali.</summary>
    [Theory]
    [InlineData("Hakları iptal et")]
    [InlineData("Sonraki iş gününe aktar")]
    [InlineData("Belirli bir tarihe aktar")]
    [InlineData("Hakları yak (iade yok)")]
    [InlineData("Resmî tatil")]
    [InlineData("Normal sınıf")]
    [InlineData("Anasınıfı")]
    public void OptionLabelsMatchConverter(string label)
    {
        var sources = File.ReadAllText(Path.Combine(Root, "src", "Yemekhane.Desktop", "Converters", "EnumTextConverter.cs"))
            + File.ReadAllText(Path.Combine(Root, "src", "Yemekhane.Domain", "Entities", "ClassKinds.cs"));
        Assert.Contains(label, sources, StringComparison.Ordinal);
        Assert.Contains(label, Guide, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryShellRouteWithPageHelpHasNonEmptyBullets() =>
        Assert.All(PageHelpTexts.ByRoute, entry => { Assert.False(string.IsNullOrWhiteSpace(entry.Value.Title)); Assert.NotEmpty(entry.Value.Bullets); });
}
