using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Sms;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

// Alici secim listesinde ayni ad-soyada sahip birden fazla ogrenci bulunabilir
// (gercek okul verisinde dort ayri "ADA AKGUN"). Sinif ve sube gosterilmezse
// operator yanlis ogrenciyi secip YANLIS VELIYE SMS gonderir. Bu yuzden secim
// ogesi sinif/sube de tasir.
// ClassName/SectionName API'de nullable'dir (Student.ClassId/SectionId nullable;
// sinifi atanmamis ogrenci olabilir); burada bos stringe indirgenir ki
// DataGrid hucresi bos gorunsun, baglama hata vermesin ve hicbir yerde
// null referans cokmesi olusmasin.
public sealed class SmsStudentChoice(Guid id, string studentNo, string name, Action? changed = null,
    string? className = null, string? sectionName = null) : ObservableObject
{
    private bool isSelected;
    public Guid Id { get; } = id;
    public string StudentNo { get; } = studentNo;
    public string Name { get; } = name;
    public string ClassName { get; } = className ?? "";
    public string SectionName { get; } = sectionName ?? "";
    public bool IsSelected { get => isSelected; set { if (Set(ref isSelected, value)) changed?.Invoke(); } }
}

public sealed class SmsViewModel : ObservableObject, IDisposable
{
    private readonly ISmsApiClient api;
    private readonly CancellationTokenSource lifetime = new();
    private string targetType = "Manual", search = "", customMessage = "", sendError = "", templateError = "", historyError = "";
    private string historyStatus = "", historyPhone = "", historyProvider = "", historyStudent = "", templateName = "", templateBody = "";
    private string expiryDate = "", entryTime = "", amount = "";
    private bool useTemplate, isConfirmed, isSendLoading, isTemplatesLoading, isHistoryLoading, isOffline, includeInactive;
    private int historyPage = 1, historyTotal;
    private SmsTemplateDetails? selectedTemplate, editingTemplate;
    private SmsTargetOption? selectedClass;
    private SmsTargetOption? selectedGroup;
    private BulkSmsPreview? preview;
    private BulkSmsEnqueueResult? enqueueResult;
    private BulkSmsRequest? previewRequest;

    public SmsViewModel(ISmsApiClient api, IEnumerable<string> permissions)
    {
        this.api = api;
        var set = permissions.ToHashSet(StringComparer.Ordinal);
        CanRead = set.Contains("sms.read"); CanSend = set.Contains("sms.send"); CanManage = set.Contains("sms.manage");
        SearchStudentsCommand = new AsyncCommand(LoadStudentsAsync);
        PreviewCommand = new AsyncCommand(PreviewAsync, () => CanSend);
        EnqueueCommand = new AsyncCommand(EnqueueAsync, () => CanSend && Preview is not null && IsConfirmed);
        RefreshTemplatesCommand = new AsyncCommand(LoadTemplatesAsync, () => CanManage || CanSend);
        NewTemplateCommand = new RelayCommand(NewTemplate, () => CanManage);
        EditTemplateCommand = new RelayCommand(EditTemplate, () => CanManage && SelectedTemplate is not null);
        SaveTemplateCommand = new AsyncCommand(SaveTemplateAsync, () => CanManage);
        DeactivateTemplateCommand = new AsyncCommand(DeactivateTemplateAsync, () => CanManage && EditingTemplate is not null);
        RefreshHistoryCommand = new AsyncCommand(() => LoadHistoryAsync(1), () => CanRead);
        NextHistoryPageCommand = new AsyncCommand(() => LoadHistoryAsync(HistoryPage + 1), () => HistoryPage * 50 < HistoryTotal);
        PreviousHistoryPageCommand = new AsyncCommand(() => LoadHistoryAsync(HistoryPage - 1), () => HistoryPage > 1);
        RetryCommand = new ParameterCommand<SmsLogDetails>(item => _ = RetryAsync(item), item => CanSend && item.Status == SmsLogStatuses.Failed);
    }

    public IReadOnlyList<string> TargetTypes { get; } = ["Manual", "Class", "Group", "All", "Filter"];
    public IReadOnlyList<string> HistoryStatuses { get; } = ["", SmsLogStatuses.Pending, SmsLogStatuses.Sending, SmsLogStatuses.Sent, SmsLogStatuses.Failed, SmsLogStatuses.RetryScheduled];
    public ObservableCollection<SmsStudentChoice> Students { get; } = [];
    public ObservableCollection<SmsTargetOption> Classes { get; } = [];
    public ObservableCollection<SmsTargetOption> Groups { get; } = [];
    public ObservableCollection<SmsTemplateDetails> Templates { get; } = [];
    public ObservableCollection<SmsLogDetails> History { get; } = [];
    public bool IsSendTargetsEmpty => !IsSendLoading && Students.Count == 0 && string.IsNullOrEmpty(SendError);
    public bool IsTemplatesEmpty => !IsTemplatesLoading && Templates.Count == 0 && string.IsNullOrEmpty(TemplateError);
    public bool CanRead { get; }
    public bool CanSend { get; }
    public bool CanManage { get; }
    public string TargetType { get => targetType; set { if (Set(ref targetType, value)) InvalidatePreview(); } }
    public string Search { get => search; set { if (Set(ref search, value)) InvalidatePreview(); } }
    public SmsTargetOption? SelectedClass { get => selectedClass; set { if (Set(ref selectedClass, value)) InvalidatePreview(); } }
    public SmsTargetOption? SelectedGroup { get => selectedGroup; set { if (Set(ref selectedGroup, value)) InvalidatePreview(); } }
    public bool UseTemplate { get => useTemplate; set { if (Set(ref useTemplate, value)) { InvalidatePreview(); Raise(nameof(MessageText)); } } }
    public SmsTemplateDetails? SelectedTemplate { get => selectedTemplate; set { if (Set(ref selectedTemplate, value)) { InvalidatePreview(); Raise(nameof(MessageText)); Raise(nameof(CharacterCount)); Raise(nameof(SegmentCount)); (EditTemplateCommand as RelayCommand)?.Refresh(); } } }
    public string CustomMessage { get => customMessage; set { if (Set(ref customMessage, value)) { InvalidatePreview(); Raise(nameof(CharacterCount)); Raise(nameof(SegmentCount)); Raise(nameof(MessageText)); } } }
    public string MessageText => UseTemplate ? SelectedTemplate?.Body ?? "" : CustomMessage;
    public int CharacterCount => MessageText.Length;
    public int SegmentCount => SmsSegments(MessageText);
    public string ExpiryDate { get => expiryDate; set { if (Set(ref expiryDate, value)) InvalidatePreview(); } }
    public string EntryTime { get => entryTime; set { if (Set(ref entryTime, value)) InvalidatePreview(); } }
    public string Amount { get => amount; set { if (Set(ref amount, value)) InvalidatePreview(); } }
    public BulkSmsPreview? Preview { get => preview; private set { if (Set(ref preview, value)) { Raise(nameof(HasPreview)); (EnqueueCommand as AsyncCommand)?.Refresh(); } } }
    public bool HasPreview => Preview is not null;
    public bool IsConfirmed { get => isConfirmed; set { if (Set(ref isConfirmed, value)) (EnqueueCommand as AsyncCommand)?.Refresh(); } }
    public BulkSmsEnqueueResult? EnqueueResult { get => enqueueResult; private set { if (Set(ref enqueueResult, value)) Raise(nameof(EnqueueResultText)); } }
    public string EnqueueResultText => EnqueueResult is null ? "" : $"{EnqueueResult.QueuedCount:N0} SMS kuyruğa alındı, {EnqueueResult.ExistingCount:N0} kayıt zaten vardı.";
    public bool IsSendLoading { get => isSendLoading; private set => Set(ref isSendLoading, value); }
    public bool IsTemplatesLoading { get => isTemplatesLoading; private set => Set(ref isTemplatesLoading, value); }
    public bool IsHistoryLoading { get => isHistoryLoading; private set => Set(ref isHistoryLoading, value); }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public string SendError { get => sendError; private set => Set(ref sendError, value); }
    public string TemplateError { get => templateError; private set => Set(ref templateError, value); }
    public string HistoryError { get => historyError; private set => Set(ref historyError, value); }
    public bool IncludeInactive { get => includeInactive; set => Set(ref includeInactive, value); }
    public SmsTemplateDetails? EditingTemplate { get => editingTemplate; private set { Set(ref editingTemplate, value); (DeactivateTemplateCommand as AsyncCommand)?.Refresh(); } }
    public string TemplateName { get => templateName; set => Set(ref templateName, value); }
    public string TemplateBody { get => templateBody; set => Set(ref templateBody, value); }
    public string HistoryStatus { get => historyStatus; set => Set(ref historyStatus, value); }
    public string HistoryPhone { get => historyPhone; set => Set(ref historyPhone, value); }
    public string HistoryProvider { get => historyProvider; set => Set(ref historyProvider, value); }
    public string HistoryStudent { get => historyStudent; set => Set(ref historyStudent, value); }
    public DateTime? HistoryFrom { get; set; }
    public DateTime? HistoryTo { get; set; }
    public int HistoryPage { get => historyPage; private set { Set(ref historyPage, value); RefreshPaging(); } }
    public int HistoryTotal { get => historyTotal; private set { Set(ref historyTotal, value); RefreshPaging(); Raise(nameof(IsHistoryEmpty)); } }
    public bool IsHistoryEmpty => !IsHistoryLoading && HistoryTotal == 0 && string.IsNullOrEmpty(HistoryError);

    public ICommand SearchStudentsCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand EnqueueCommand { get; }
    public ICommand RefreshTemplatesCommand { get; }
    public ICommand NewTemplateCommand { get; }
    public ICommand EditTemplateCommand { get; }
    public ICommand SaveTemplateCommand { get; }
    public ICommand DeactivateTemplateCommand { get; }
    public ICommand RefreshHistoryCommand { get; }
    public ICommand NextHistoryPageCommand { get; }
    public ICommand PreviousHistoryPageCommand { get; }
    public ICommand RetryCommand { get; }

    public async Task InitializeAsync()
    {
        var tasks = new List<Task>();
        if (CanSend) tasks.Add(LoadTargetsAsync());
        if (CanSend || CanManage) tasks.Add(LoadTemplatesAsync());
        if (CanRead) tasks.Add(LoadHistoryAsync(1));
        await Task.WhenAll(tasks);
        _ = RefreshLoopAsync(lifetime.Token);
    }

    public void SelectStudent(Guid studentId)
    {
        var item = Students.FirstOrDefault(x => x.Id == studentId);
        if (item is not null) item.IsSelected = true;
        TargetType = "Manual";
        InvalidatePreview();
    }

    private async Task LoadTargetsAsync()
    {
        try
        {
            await LoadStudentsAsync();
        }
        catch (Exception ex) when (Handle(ex, value => SendError = value)) { }
    }

    private async Task LoadStudentsAsync()
    {
        IsSendLoading = true; SendError = "";
        try
        {
            var selected = Students.Where(x => x.IsSelected).Select(x => x.Id).ToHashSet();
            var result = await api.TargetsAsync(string.IsNullOrWhiteSpace(Search) ? null : Search, lifetime.Token);
            Students.Clear(); foreach (var item in result.Students)
            {
                var choice = new SmsStudentChoice(item.Id, item.StudentNo, item.Name, InvalidatePreview,
                    item.ClassName, item.SectionName);
                choice.IsSelected = selected.Contains(item.Id); Students.Add(choice);
            }
            Classes.Clear(); foreach (var item in result.Classes) Classes.Add(item);
            Groups.Clear(); foreach (var item in result.Groups) Groups.Add(item);
        }
        catch (Exception ex) when (Handle(ex, value => SendError = value)) { }
        finally { IsSendLoading = false; Raise(nameof(IsSendTargetsEmpty)); }
    }

    private async Task PreviewAsync()
    {
        IsSendLoading = true; SendError = ""; EnqueueResult = null;
        try { previewRequest = BuildRequest(); Preview = await api.PreviewAsync(previewRequest, lifetime.Token); IsConfirmed = false; }
        catch (Exception ex) when (Handle(ex, value => SendError = value)) { Preview = null; }
        finally { IsSendLoading = false; }
    }

    private async Task EnqueueAsync()
    {
        if (Preview is null || previewRequest is null || !IsConfirmed) return;
        IsSendLoading = true; SendError = "";
        try { EnqueueResult = await api.ApplyAsync(new(previewRequest, Preview.PreviewToken), lifetime.Token); Preview = null; previewRequest = null; IsConfirmed = false; await LoadHistoryAsync(1); }
        catch (Exception ex) when (Handle(ex, value => SendError = value)) { Preview = null; IsConfirmed = false; }
        finally { IsSendLoading = false; }
    }

    private BulkSmsRequest BuildRequest()
    {
        var id = TargetType == "Class" ? SelectedClass?.Id : TargetType == "Group" ? SelectedGroup?.Id : null;
        var ids = TargetType == "Manual" ? Students.Where(x => x.IsSelected).Select(x => x.Id).ToArray() : null;
        var variables = new Dictionary<string, object?>();
        if (DateOnly.TryParse(ExpiryDate, CultureInfo.CurrentCulture, out var date)) variables["ExpiryDate"] = date;
        if (TimeOnly.TryParse(EntryTime, CultureInfo.CurrentCulture, out var time)) variables["EntryTime"] = time;
        if (decimal.TryParse(Amount, NumberStyles.Number, CultureInfo.CurrentCulture, out var value)) variables["Amount"] = value;
        return new(Guid.NewGuid().ToString("N"), new(TargetType, id, ids, TargetType == "Filter" ? Search : null),
            UseTemplate ? null : CustomMessage, UseTemplate ? SelectedTemplate?.Id : null, variables);
    }

    private async Task LoadTemplatesAsync()
    {
        IsTemplatesLoading = true; TemplateError = "";
        try { var items = await api.TemplatesAsync(IncludeInactive, lifetime.Token); Templates.Clear(); foreach (var item in items) Templates.Add(item); }
        catch (Exception ex) when (Handle(ex, value => TemplateError = value)) { }
        finally { IsTemplatesLoading = false; Raise(nameof(IsTemplatesEmpty)); }
    }
    private void NewTemplate() { EditingTemplate = null; TemplateName = ""; TemplateBody = ""; }
    private void EditTemplate() { EditingTemplate = SelectedTemplate; TemplateName = EditingTemplate?.Name ?? ""; TemplateBody = EditingTemplate?.Body ?? ""; }
    private async Task SaveTemplateAsync()
    {
        IsTemplatesLoading = true; TemplateError = "";
        try { await api.SaveTemplateAsync(EditingTemplate?.Id, new(TemplateName, TemplateBody, EditingTemplate?.IsActive ?? true), lifetime.Token); NewTemplate(); await LoadTemplatesAsync(); }
        catch (Exception ex) when (Handle(ex, value => TemplateError = value)) { }
        finally { IsTemplatesLoading = false; }
    }
    private async Task DeactivateTemplateAsync()
    {
        if (EditingTemplate is null) return;
        try { await api.DeactivateTemplateAsync(EditingTemplate.Id, lifetime.Token); NewTemplate(); await LoadTemplatesAsync(); }
        catch (Exception ex) when (Handle(ex, value => TemplateError = value)) { }
    }

    private async Task LoadHistoryAsync(int page)
    {
        IsHistoryLoading = true; HistoryError = "";
        try
        {
            var result = await api.HistoryAsync(new(Empty(HistoryStatus), Empty(HistoryPhone),
                HistoryFrom.HasValue ? new DateTimeOffset(HistoryFrom.Value) : null,
                HistoryTo.HasValue ? new DateTimeOffset(HistoryTo.Value.Date.AddDays(1).AddTicks(-1)) : null,
                page, 50, Provider: Empty(HistoryProvider), Student: Empty(HistoryStudent)), lifetime.Token);
            History.Clear(); foreach (var item in result.Items) History.Add(item); HistoryPage = result.Page; HistoryTotal = result.TotalCount;
        }
        catch (Exception ex) when (Handle(ex, value => HistoryError = value)) { }
        finally { IsHistoryLoading = false; Raise(nameof(IsHistoryEmpty)); }
    }
    private async Task RetryAsync(SmsLogDetails item)
    {
        if (!CanSend || item.Status != SmsLogStatuses.Failed) return;
        try { await api.RetryAsync(item.Id, lifetime.Token); await LoadHistoryAsync(HistoryPage); }
        catch (Exception ex) when (Handle(ex, value => HistoryError = value)) { }
    }
    private async Task RefreshLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try { while (await timer.WaitForNextTickAsync(token)) if (CanRead && !IsHistoryLoading) await LoadHistoryAsync(HistoryPage); }
        catch (OperationCanceledException) { }
    }
    private void InvalidatePreview() { Preview = null; previewRequest = null; IsConfirmed = false; EnqueueResult = null; }
    private bool Handle(Exception exception, Action<string> setter)
    {
        if (exception is not (HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException)) return false;
        IsOffline = exception is not LoginRequiredException;
        setter(exception is LoginRequiredException ? "Bu işlem için gerekli SMS izni veya oturum bulunmuyor." : "SMS servisine ulaşılamadı.");
        return true;
    }
    private void RefreshPaging() { (NextHistoryPageCommand as AsyncCommand)?.Refresh(); (PreviousHistoryPageCommand as AsyncCommand)?.Refresh(); }
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static int SmsSegments(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        const string gsm = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ ÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
        const string extended = "^{}\\[~]|€";
        var unicode = text.Any(c => !gsm.Contains(c) && !extended.Contains(c));
        var length = unicode ? text.Length : text.Sum(c => extended.Contains(c) ? 2 : 1);
        var single = unicode ? 70 : 160; var multipart = unicode ? 67 : 153;
        return length <= single ? 1 : (int)Math.Ceiling(length / (double)multipart);
    }
    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); GC.SuppressFinalize(this); }
}
