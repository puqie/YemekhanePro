namespace Yemekhane.Application.Reports;

/// <summary>
/// Disa aktarilan raporlarin basligindaki kurum adi. PDF/Excel servisleri bu adi
/// baslangicta appsettings.json'dan (Reports:Pdf:SchoolName) okuyordu; kullanici
/// Ayarlar > Okul'a gercek okul adini yazip kaydettiginde rapor basliklari yine
/// sabit "Okul Yemekhanesi" kaliyordu. Ad artik her rapor uretiminde canli olarak
/// System Settings'teki "School.Name" satirindan okunur; satir yoksa yapilandirmadaki
/// deger yedek kalir.
/// </summary>
public interface IReportBrandingProvider
{
    Task<string?> SchoolNameAsync(CancellationToken cancellationToken = default);
}
