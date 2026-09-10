namespace Yemekhane.Application.Maintenance;

/// <summary>
/// Yil sonu sifirlamasi: yeni egitim yilina temiz baslamak icin ISLETIM verileri (hakedisler,
/// kullanimlar, gecis kayitlari, devirler, izinler, grup uyelikleri, SMS ve toplu islem gecmisi,
/// kartlar) silinir; ogrenciler SILINMEZ, pasife alinir. Tahsilat ve bakiye hareketleri ile
/// veliler KORUNUR: veli 1-2 yil onceki odemesini sorabilir, Raporlar → Gelir ve ogrencinin
/// Ödemeler sekmesi bunu okumaya devam eder. Tanimlar (ogunler, siniflar, cihazlar,
/// kullanicilar, ayarlar, tatil takvimi) kalir. Once guvenlik yedegi alinir; alinamazsa hicbir
/// sey degismez.
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

/// <summary>Bir sifirlama adiminin kayda ne yaptigi.</summary>
public static class YearEndResetActions
{
    public const string Delete = "Delete";
    public const string Deactivate = "Deactivate";
}

/// <param name="Action"><see cref="YearEndResetActions.Delete"/> ya da <see cref="YearEndResetActions.Deactivate"/>.</param>
public sealed record YearEndResetItem(string Key, string Label, int Count, string Action = YearEndResetActions.Delete);

/// <param name="UnusedPaidQuantity">
/// Silinecek haklar icinde ODENMIS ama KULLANILMAMIS ogun sayisi. Bu haklarin
/// karsiligi olan tahsilat kasada AKTIF kalir: hak yok, para var, iade yok.
/// Kullanici bunu bilerek karar vermelidir; once yalnizca satir SAYISI gosteriliyordu.
/// </param>
/// <param name="UnusedPaidStudents">Bu durumdaki ogrenci sayisi.</param>
public sealed record YearEndResetPreview(IReadOnlyList<YearEndResetItem> Items,
    int UnusedPaidQuantity = 0, int UnusedPaidStudents = 0)
{
    /// <summary>Etkilenecek kayit sayisi (silinen + pasife alinan).</summary>
    public int Total => Items.Sum(item => item.Count);
    public int DeletedTotal => Items.Where(item => item.Action == YearEndResetActions.Delete).Sum(item => item.Count);
    public int DeactivatedTotal => Items.Where(item => item.Action == YearEndResetActions.Deactivate).Sum(item => item.Count);
}

public sealed record YearEndResetRequest(string? Confirmation);

/// <param name="Deleted">Uygulanan adimlar; pasife alma adimi da bu listede (Action ile ayrilir).</param>
public sealed record YearEndResetResult(string BackupFileName, IReadOnlyList<YearEndResetItem> Deleted, DateTimeOffset CompletedAt)
{
    public int Total => Deleted.Sum(item => item.Count);
    public int DeletedTotal => Deleted.Where(item => item.Action == YearEndResetActions.Delete).Sum(item => item.Count);
    public int DeactivatedTotal => Deleted.Where(item => item.Action == YearEndResetActions.Deactivate).Sum(item => item.Count);
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
