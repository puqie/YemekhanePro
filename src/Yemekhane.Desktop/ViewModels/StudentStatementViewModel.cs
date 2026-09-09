using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Statements;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

/// <summary>Ekstrenin tek satiri; tablo icin bicimlenmis alanlar.</summary>
public sealed class StatementRowViewModel(StatementLine line)
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeZoneInfo Istanbul = FindIstanbul();

    public string Date => TimeZoneInfo.ConvertTime(line.OccurredAt, Istanbul).ToString("dd.MM.yyyy", Turkish);
    public string Section => StatementSections.Label(line.Section);
    public string Title => line.Title;
    public string? Detail => line.Detail;
    public string Amount => line.Section == StatementSections.Meal
        ? line.Quantity.ToString("N0", Turkish) + " öğün"
        : line.Amount.ToString("C2", Turkish);
    public string? Status => line.Status;
    /// <summary>Iptal edilen satir soluk yazilir; veli neyin gecerli olmadigini gormeli.</summary>
    public bool IsCancelled => line.IsCancelled;

    private static TimeZoneInfo FindIstanbul()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
    }
}

/// <summary>
/// Ogrenci ekstresi ekrani: secilen tarih araligindaki odemeler, bakiye hareketleri,
/// taksitler ve yemek kullanimi. Veli "gecen yil ne odedim" diye sordugunda memur bu
/// ekrandan gosterir ya da PDF olarak verir.
/// </summary>
public sealed class StudentStatementViewModel : ObservableObject
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private readonly ITuitionApiClient api;
    private readonly IStatementFileDialog? dialogs;
    private Guid studentId;
    private StudentStatement? statement;
    private DateTime startDate = DateTime.Today.AddMonths(-12);
    private DateTime endDate = DateTime.Today;
    private string? errorMessage, statusMessage;
    private bool isLoading;

    public StudentStatementViewModel(ITuitionApiClient api, IStatementFileDialog? dialogs = null, bool canExport = true)
    {
        this.api = api;
        this.dialogs = dialogs;
        CanExport = canExport;
        LoadCommand = new AsyncCommand(LoadAsync, () => studentId != Guid.Empty);
        ExportPdfCommand = new AsyncCommand(ExportPdfAsync, () => CanExport && statement is not null);
    }

    public ObservableCollection<StatementRowViewModel> Rows { get; } = [];
    public ObservableCollection<StatementSectionSummary> Sections { get; } = [];

    public DateTime StartDate { get => startDate; set => Set(ref startDate, value); }
    public DateTime EndDate { get => endDate; set => Set(ref endDate, value); }
    public bool IsLoading { get => isLoading; private set { if (Set(ref isLoading, value)) RefreshCommands(); } }
    public bool CanExport { get; }
    public string? ErrorMessage { get => errorMessage; private set => Set(ref errorMessage, value); }
    public string? StatusMessage { get => statusMessage; private set => Set(ref statusMessage, value); }
    public bool HasRows => Rows.Count > 0;
    public string Title => statement is null ? "Öğrenci Ekstresi"
        : string.IsNullOrWhiteSpace(statement.StudentNo) ? statement.StudentName
        : $"{statement.StudentName} · No {statement.StudentNo}";

    /// <summary>Ozet satiri; veli en cok bu iki rakami sorar: ne odedim, ne kaldi.</summary>
    public string SummaryText => statement is null
        ? "Tarih aralığı seçip Getir'e basın."
        : $"Tahsil edilen {Money(statement.TotalPaid)} · Güncel bakiye {Money(statement.CurrentBalance)}"
          + (statement.TuitionDue > 0 ? $" · Kalan borç {Money(statement.TuitionOutstanding)}" : "")
          + (statement.TuitionOverdue > 0 ? $" · Gecikmiş {Money(statement.TuitionOverdue)}" : "")
          + $" · {statement.MealsUsed} öğün";

    public ICommand LoadCommand { get; }
    public ICommand ExportPdfCommand { get; }

    /// <summary>Ogrenci degisince onceki ekstre temizlenir; yanlis ogrencinin verisi gorunmesin.</summary>
    public void SetStudent(Guid id)
    {
        studentId = id;
        statement = null;
        Rows.Clear(); Sections.Clear();
        ErrorMessage = StatusMessage = null;
        RaiseAll();
    }

    public async Task LoadAsync()
    {
        if (studentId == Guid.Empty) return;
        if (EndDate.Date < StartDate.Date) { ErrorMessage = "Bitiş tarihi başlangıçtan önce olamaz."; return; }
        IsLoading = true; ErrorMessage = StatusMessage = null;
        try
        {
            statement = await api.StatementAsync(studentId, DateOnly.FromDateTime(StartDate.Date), DateOnly.FromDateTime(EndDate.Date));
            Rows.Clear();
            foreach (var line in statement.Lines) Rows.Add(new StatementRowViewModel(line));
            Sections.Clear();
            foreach (var section in statement.Sections) Sections.Add(section);
            if (Rows.Count == 0) StatusMessage = "Bu tarih aralığında kayıt bulunamadı.";
        }
        catch (ApiRequestException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException)
        { ErrorMessage = "Ekstre alınamadı. Bağlantıyı kontrol edip tekrar deneyin."; }
        finally { IsLoading = false; RaiseAll(); }
    }

    private async Task ExportPdfAsync()
    {
        if (statement is null || dialogs is null) return;
        var name = $"ekstre-{statement.StudentNo}-{statement.From:yyyyMMdd}";
        var path = dialogs.ChoosePdfPath(name);
        if (string.IsNullOrWhiteSpace(path)) return;
        IsLoading = true; ErrorMessage = StatusMessage = null;
        try
        {
            await api.DownloadStatementPdfAsync(studentId, statement.From, statement.To, path);
            StatusMessage = "Ekstre kaydedildi: " + path;
        }
        catch (ApiRequestException ex) { ErrorMessage = ex.Message; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or LoginRequiredException)
        { ErrorMessage = "Ekstre kaydedilemedi. Bağlantıyı ve dosya yolunu kontrol edin."; }
        finally { IsLoading = false; }
    }

    private static string Money(decimal value) => value.ToString("C2", Turkish);

    private void RaiseAll()
    {
        Raise(nameof(HasRows)); Raise(nameof(SummaryText)); Raise(nameof(Title));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        (LoadCommand as AsyncCommand)?.Refresh();
        (ExportPdfCommand as AsyncCommand)?.Refresh();
    }
}
