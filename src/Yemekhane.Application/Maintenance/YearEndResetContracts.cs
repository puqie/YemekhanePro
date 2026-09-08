namespace Yemekhane.Application.Maintenance;

/// <summary>
/// Yil sonu sifirlamasi: yeni egitim yilina bos ogrenci listesiyle baslamak icin ogrenciye bagli
/// TUM veriler (kartlar, veliler, hakedisler, kullanimlar, gecis kayitlari, izinler, bakiye ve
/// tahsilat hareketleri, SMS ve toplu islem gecmisi) silinir. Tanimlar (ogunler, siniflar,
/// cihazlar, kullanicilar, ayarlar, tatil takvimi) KALIR. Silmeden once guvenlik yedegi alinir;
/// yedek alinamazsa hicbir sey silinmez.
/// </summary>
public static class YearEndReset
{
    /// <summary>
    /// Onay metni ASCII (Turkce harf YOK); geri yukleme onayiyla ayni gerekce: klavye duzeni ve
    /// buyuk/kucuk I/ı farki onayi engellemesin.
    /// </summary>
    public const string ConfirmationPhrase = "SIFIRLA";

    public static bool IsConfirmed(string? confirmation) =>
        string.Equals(confirmation?.Trim(), ConfirmationPhrase, StringComparison.Ordinal);
}

public sealed record YearEndResetItem(string Key, string Label, int Count);

public sealed record YearEndResetPreview(IReadOnlyList<YearEndResetItem> Items)
{
    public int Total => Items.Sum(item => item.Count);
}

public sealed record YearEndResetRequest(string? Confirmation);

public sealed record YearEndResetResult(string BackupFileName, IReadOnlyList<YearEndResetItem> Deleted, DateTimeOffset CompletedAt)
{
    public int Total => Deleted.Sum(item => item.Count);
}

/// <summary>Sifirlamadan once alinan guvenlik yedegi; dosya adini doner. Basarisizsa istisna atar.</summary>
public interface IYearEndBackup
{
    Task<string> CreateSafetyBackupAsync(CancellationToken cancellationToken);
}

public interface IYearEndResetService
{
    Task<YearEndResetPreview> PreviewAsync(CancellationToken cancellationToken);
    Task<YearEndResetResult> ResetAsync(string? confirmation, CancellationToken cancellationToken);
}
