using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Yemekhane.Application.StudentImports;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

/// <summary>
/// Sicil Aktar: Excel/CSV dosyasindan ogrenci ve kart kayitlarini ice aktarir.
///
/// Akis her zaman ONIZLEME -> UYGULA seklindedir. Kullanici, kac kaydin
/// olusturulacagini/guncellenecegini ve kac satirin hatali oldugunu GORMEDEN
/// hicbir sey yazilmaz; boylece yanlis bir dosya sessizce binlerce kaydi bozamaz.
/// </summary>
public sealed class StudentImportViewModel : ObservableObject
{
    private readonly IStudentImportApiClient api;
    private readonly IFileDialogService files;
    private readonly bool canImport;
    private bool isBusy, applyValidRows;
    private string? filePath, errorMessage, statusMessage;
    private ImportPreviewResult? preview;
    private ImportApplyResult? result;

    public StudentImportViewModel(IStudentImportApiClient api, IFileDialogService files,
        IEnumerable<string> permissions)
    {
        this.api = api;
        this.files = files;
        canImport = permissions.ToHashSet(StringComparer.Ordinal).Contains("students.write");
        ChooseFileCommand = new RelayCommand(ChooseFile, () => canImport && !IsBusy);
        PreviewCommand = new AsyncCommand(PreviewAsync, () => canImport && !IsBusy && HasFile);
        ApplyCommand = new AsyncCommand(ApplyAsync, () => canImport && !IsBusy && CanApply);
        DownloadErrorsCommand = new AsyncCommand(DownloadErrorsAsync, () => !IsBusy && HasErrorRows);
        ResetCommand = new RelayCommand(Reset, () => !IsBusy);
    }

    public ObservableCollection<ImportPreviewRow> Rows { get; } = [];

    public ICommand ChooseFileCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand DownloadErrorsCommand { get; }
    public ICommand ResetCommand { get; }

    public bool CanImport => canImport;
    public bool IsBusy { get => isBusy; private set { if (Set(ref isBusy, value)) RefreshCommands(); } }
    public string? FilePath { get => filePath; private set { if (Set(ref filePath, value)) { Raise(nameof(HasFile)); Raise(nameof(FileName)); RefreshCommands(); } } }
    public string? FileName => string.IsNullOrWhiteSpace(FilePath) ? null : Path.GetFileName(FilePath);
    public bool HasFile => !string.IsNullOrWhiteSpace(FilePath);

    public string? ErrorMessage { get => errorMessage; private set { if (Set(ref errorMessage, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string? StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }

    public ImportPreviewResult? Preview
    {
        get => preview;
        private set
        {
            if (!Set(ref preview, value)) return;
            Rows.Clear();
            if (value is not null) foreach (var row in value.Rows) Rows.Add(row);
            Raise(nameof(HasPreview)); Raise(nameof(TotalCount)); Raise(nameof(NewCount));
            Raise(nameof(UpdateCount)); Raise(nameof(ErrorCount)); Raise(nameof(HasErrorRows));
            Raise(nameof(SummaryText)); Raise(nameof(CanApply)); Raise(nameof(IsEmpty));
            RefreshCommands();
        }
    }

    public bool HasPreview => Preview is not null;
    public int TotalCount => Preview?.TotalCount ?? 0;
    public int NewCount => Preview?.NewCount ?? 0;
    public int UpdateCount => Preview?.UpdateCount ?? 0;
    public int ErrorCount => Preview?.ErrorCount ?? 0;
    public bool HasErrorRows => ErrorCount > 0;
    public bool IsEmpty => HasPreview && TotalCount == 0;

    /// <summary>Hatali satir varsa kullanici acikca "gecerli satirlari aktar" demelidir.</summary>
    public bool ApplyValidRows
    {
        get => applyValidRows;
        set { if (Set(ref applyValidRows, value)) { Raise(nameof(CanApply)); RefreshCommands(); } }
    }

    public bool CanApply => Preview is not null && TotalCount > 0 && (ErrorCount == 0 || ApplyValidRows);

    /// <summary>Yikici olmayan ama geri alinmasi zor bir islem: net sayilar gosterilir.</summary>
    public string SummaryText => Preview is null
        ? string.Empty
        : $"{TotalCount:N0} satır okundu · {NewCount:N0} yeni · {UpdateCount:N0} güncellenecek · {ErrorCount:N0} hatalı";

    public ImportApplyResult? Result { get => result; private set { if (Set(ref result, value)) Raise(nameof(HasResult)); } }
    public bool HasResult => Result is not null;

    private void ChooseFile()
    {
        ErrorMessage = null; StatusMessage = null;
        var chosen = files.OpenFile("Öğrenci listesi seç",
            "Excel ve CSV dosyaları (*.xlsx;*.csv)|*.xlsx;*.csv|Tüm dosyalar (*.*)|*.*");
        if (string.IsNullOrWhiteSpace(chosen)) return;
        FilePath = chosen;
        // Yeni dosya secildiginde eski onizleme gecersizdir; aksi halde kullanici
        // A dosyasinin ozetine bakip B dosyasini uygulayabilir.
        Preview = null; Result = null; ApplyValidRows = false;
    }

    private async Task PreviewAsync()
    {
        if (!HasFile) return;
        IsBusy = true; ErrorMessage = null; StatusMessage = null; Result = null;
        try
        {
            Preview = await api.PreviewAsync(FilePath!);
            StatusMessage = Preview.TotalCount == 0
                ? "Dosyada aktarılacak satır bulunamadı."
                : "Önizleme hazır. Uygulamadan önce sayıları kontrol edin.";
        }
        catch (Exception exception)
        {
            Preview = null;
            ErrorMessage = Friendly(exception, "Dosya okunamadı. Biçimi ve içeriğini kontrol edin.");
        }
        finally { IsBusy = false; }
    }

    private async Task ApplyAsync()
    {
        if (Preview is null) return;
        IsBusy = true; ErrorMessage = null;
        try
        {
            Result = await api.ApplyAsync(new ApplyStudentImportRequest(Preview.Token, ApplyValidRows));
            StatusMessage = $"{Result.CreatedCount:N0} öğrenci oluşturuldu, {Result.UpdatedCount:N0} öğrenci güncellendi" +
                (Result.ErrorCount > 0 ? $", {Result.ErrorCount:N0} satır aktarılamadı." : ".");
            // Onizleme jetonu tuketildi: ayni ozetin ikinci kez uygulanmasi engellenir.
            Preview = null;
        }
        catch (Exception exception)
        {
            ErrorMessage = Friendly(exception,
                "İçe aktarma tamamlanamadı. Önizlemenin süresi dolmuş olabilir; dosyayı yeniden önizleyin.");
            Preview = null;
        }
        finally { IsBusy = false; }
    }

    private async Task DownloadErrorsAsync()
    {
        if (Preview is null) return;
        var target = files.SaveFile("Hatalı satır raporunu kaydet", "CSV dosyası (*.csv)|*.csv",
            $"ice-aktarma-hatalari-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        if (string.IsNullOrWhiteSpace(target)) return;
        IsBusy = true; ErrorMessage = null;
        try
        {
            await api.DownloadErrorReportAsync(Preview.Token, target);
            StatusMessage = $"Hata raporu kaydedildi: {target}";
        }
        catch (Exception exception)
        {
            ErrorMessage = Friendly(exception, "Hata raporu indirilemedi.");
        }
        finally { IsBusy = false; }
    }

    private void Reset()
    {
        FilePath = null; Preview = null; Result = null;
        ErrorMessage = null; StatusMessage = null; ApplyValidRows = false;
    }

    private void RefreshCommands()
    {
        (ChooseFileCommand as RelayCommand)?.Refresh();
        (PreviewCommand as AsyncCommand)?.Refresh();
        (ApplyCommand as AsyncCommand)?.Refresh();
        (DownloadErrorsCommand as AsyncCommand)?.Refresh();
        (ResetCommand as RelayCommand)?.Refresh();
    }

    /// <summary>Sunucunun anlamli mesaji varsa gosterilir; yoksa baglama uygun bir aciklama.</summary>
    private static string Friendly(Exception exception, string fallback) => exception switch
    {
        // Sunucunun reddi ("Zorunlu başlıklar eksik: NO, KART NO, AD, SOYAD." gibi) aynen
        // gosterilir: kullanici dosyada neyi duzeltecegini ancak boyle ogrenir.
        ApiRequestException => exception.Message,
        InvalidDataException => exception.Message,
        FileNotFoundException => exception.Message,
        InvalidOperationException => exception.Message,
        _ => fallback
    };
}
