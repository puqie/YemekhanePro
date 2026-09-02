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

/// <summary>
/// Acilir kutu secenegi: ekranda <see cref="Name"/> (Turkce), API'ye <see cref="Value"/>
/// (sunucunun tanidigi İngilizce kod) gider.
/// </summary>
/// <remarks>
/// Once ComboBox dogrudan "Manual", "Class", "RetryScheduled" gibi ham kodlari
/// gosteriyordu. Ad ile deger ayrildi: sunucu sozlesmesi (BulkSmsScope.Type,
/// SmsHistoryFilter.Status) degismez, kullanici Turkce gorur.
/// </remarks>
public sealed record SmsChoiceOption(string Name, string Value);

/// <summary>Sablon degiskeni: metne yazilan jeton ve kullaniciya gosterilen Turkce aciklama.</summary>
public sealed record SmsTemplateVariable(string Token, string Label);

public sealed class SmsViewModel : ObservableObject, IDisposable
{
    private readonly ISmsApiClient api;
    private readonly CancellationTokenSource lifetime = new();
    // Secili ogrenciler ARAMADAN BAGIMSIZ tutulur: kullanici "ada" arayip Ada'yi,
    // sonra "ali" arayip Ali'yi isaretlerse ikisi de alicidir. Once yalnizca
    // ekranda gorunen liste kullaniliyordu; ilk secim sessizce dusuyordu.
    private readonly HashSet<Guid> selectedStudentIds = [];
    private string targetType = "Manual", search = "", customMessage = "", sendError = "", templateError = "", historyError = "";
    private string historyStatus = "", historyPhone = "", historyProvider = "", historyStudent = "", templateName = "", templateBody = "";
    private string expiryDate = "", entryTime = "", amount = "";
    private bool useTemplate, isConfirmed, isSendLoading, isTemplatesLoading, isHistoryLoading, isOffline, includeInactive;
    private int historyPage = 1, historyTotal;
    private SmsTemplateDetails? selectedTemplate, selectedTemplateRow, editingTemplate;
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
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => selectedStudentIds.Count > 0);
        PreviewCommand = new AsyncCommand(PreviewAsync, () => CanSend);
        EnqueueCommand = new AsyncCommand(EnqueueAsync, () => CanSend && Preview is not null && IsConfirmed);
        RefreshTemplatesCommand = new AsyncCommand(LoadTemplatesAsync, () => CanManage || CanSend);
        NewTemplateCommand = new RelayCommand(NewTemplate, () => CanManage);
        EditTemplateCommand = new RelayCommand(EditTemplate, () => CanManage && SelectedTemplateRow is not null);
        SaveTemplateCommand = new AsyncCommand(SaveTemplateAsync, () => CanManage);
        DeactivateTemplateCommand = new AsyncCommand(DeactivateTemplateAsync, () => CanManage && EditingTemplate is not null);
        InsertVariableCommand = new RelayCommand<string>(InsertVariable, _ => CanManage);
        RefreshHistoryCommand = new AsyncCommand(() => LoadHistoryAsync(1), () => CanRead);
        NextHistoryPageCommand = new AsyncCommand(() => LoadHistoryAsync(HistoryPage + 1), () => HistoryPage * 50 < HistoryTotal);
        PreviousHistoryPageCommand = new AsyncCommand(() => LoadHistoryAsync(HistoryPage - 1), () => HistoryPage > 1);
        RetryCommand = new ParameterCommand<SmsLogDetails>(item => _ = RetryAsync(item), item => CanSend && item.Status == SmsLogStatuses.Failed);
    }

    // Value'lar BulkSmsService.ScopeTypes ile birebir aynidir; sunucuya bu gider.
    public IReadOnlyList<SmsChoiceOption> TargetTypes { get; } =
    [
        new("Manuel seçim", "Manual"), new("Sınıf", "Class"), new("Grup", "Group"),
        new("Tüm öğrenciler", "All"), new("Arama filtresi", "Filter")
    ];
    // Bos deger "Tümü": sunucuya status gonderilmez. Once bos satir gorunuyordu.
    public IReadOnlyList<SmsChoiceOption> HistoryStatuses { get; } =
    [
        new("Tümü", ""), new("Bekliyor", SmsLogStatuses.Pending), new("Gönderiliyor", SmsLogStatuses.Sending),
        new("Gönderildi", SmsLogStatuses.Sent), new("Başarısız", SmsLogStatuses.Failed),
        new("Yeniden denenecek", SmsLogStatuses.RetryScheduled)
    ];
    // SmsTemplateRenderer.AllowedVariables ile ayni jetonlar; kullaniciya Turkce anlamiyla sunulur.
    public static IReadOnlyList<SmsTemplateVariable> TemplateVariables { get; } =
    [
        new("{{StudentName}}", "Öğrenci adı"), new("{{ParentName}}", "Veli adı"),
        new("{{ExpiryDate}}", "Son tarih"), new("{{EntryTime}}", "Giriş saati"), new("{{Amount}}", "Tutar")
    ];
    public ObservableCollection<SmsStudentChoice> Students { get; } = [];
    public ObservableCollection<SmsTargetOption> Classes { get; } = [];
    public ObservableCollection<SmsTargetOption> Groups { get; } = [];
    /// <summary>Sablonlar sekmesindeki liste; "Pasifleri göster" acikken pasifler de gelir.</summary>
    public ObservableCollection<SmsTemplateDetails> Templates { get; } = [];
    /// <summary>
    /// Gonder sekmesinin sablon kutusu: YALNIZCA aktif sablonlar. Once iki sekme ayni
    /// listeyi paylasiyordu; "Pasifleri göster" isaretlenince pasif sablon Gonder
    /// kutusunda da beliriyor, secilince sunucu "Aktif SMS şablonu bulunamadı" diyordu.
    /// </summary>
    public ObservableCollection<SmsTemplateDetails> SendTemplates { get; } = [];
    public ObservableCollection<SmsLogDetails> History { get; } = [];
    public bool IsSendTargetsEmpty => !IsSendLoading && Students.Count == 0 && string.IsNullOrEmpty(SendError);
    public bool IsTemplatesEmpty => !IsTemplatesLoading && Templates.Count == 0 && string.IsNullOrEmpty(TemplateError);
    public bool CanRead { get; }
    public bool CanSend { get; }
    public bool CanManage { get; }
    public string TargetType { get => targetType; set { if (Set(ref targetType, value)) { InvalidatePreview(); Raise(nameof(IsManualTarget)); Raise(nameof(IsClassTarget)); Raise(nameof(IsGroupTarget)); } } }
    public bool IsManualTarget => TargetType == "Manual";
    public bool IsClassTarget => TargetType == "Class";
    public bool IsGroupTarget => TargetType == "Group";
    public string Search { get => search; set { if (Set(ref search, value)) InvalidatePreview(); } }
    public SmsTargetOption? SelectedClass { get => selectedClass; set { if (Set(ref selectedClass, value)) InvalidatePreview(); } }
    public SmsTargetOption? SelectedGroup { get => selectedGroup; set { if (Set(ref selectedGroup, value)) InvalidatePreview(); } }
    public int SelectedStudentCount => selectedStudentIds.Count;
    public IReadOnlyCollection<Guid> SelectedStudentIds => selectedStudentIds;
    public string SelectedStudentText => selectedStudentIds.Count == 0 ? "Seçili öğrenci yok" : $"Seçili: {selectedStudentIds.Count} öğrenci";
    public bool UseTemplate { get => useTemplate; set { if (Set(ref useTemplate, value)) { InvalidatePreview(); RaiseMessageMetrics(); } } }
    public SmsTemplateDetails? SelectedTemplate { get => selectedTemplate; set { if (Set(ref selectedTemplate, value)) { InvalidatePreview(); RaiseMessageMetrics(); } } }
    /// <summary>Sablonlar sekmesinde tiklanan satir; Gonder sekmesindeki secimden bagimsizdir.</summary>
    public SmsTemplateDetails? SelectedTemplateRow { get => selectedTemplateRow; set { if (Set(ref selectedTemplateRow, value)) (EditTemplateCommand as RelayCommand)?.Refresh(); } }
    public string CustomMessage { get => customMessage; set { if (Set(ref customMessage, value)) { InvalidatePreview(); RaiseMessageMetrics(); } } }
    public string MessageText => UseTemplate ? SelectedTemplate?.Body ?? "" : CustomMessage;
    public int CharacterCount => MessageText.Length;
    public int SegmentCount => SmsSegments(MessageText);
    /// <summary>
    /// Kullaniciya segment sayisinin NEDEN degistigini soyler: ğ, ş, ı gibi harfler
    /// GSM alfabesinde yoktur, mesaj UCS-2 ile kodlanir ve segment 160'tan 70'e duser.
    /// </summary>
    public string EncodingHint => MessageText.Length == 0 ? "" : UsesUnicode(MessageText)
        ? "Türkçe karakter içeriyor: segment başına 70 karakter"
        : "Standart alfabe: segment başına 160 karakter";
    public string ExpiryDate { get => expiryDate; set { if (Set(ref expiryDate, value)) InvalidatePreview(); } }
    public string EntryTime { get => entryTime; set { if (Set(ref entryTime, value)) InvalidatePreview(); } }
    public string Amount { get => amount; set { if (Set(ref amount, value)) InvalidatePreview(); } }
    public BulkSmsPreview? Preview { get => preview; private set { if (Set(ref preview, value)) { Raise(nameof(HasPreview)); (EnqueueCommand as AsyncCommand)?.Refresh(); } } }
    public bool HasPreview => Preview is not null;
    public bool IsConfirmed { get => isConfirmed; set { if (Set(ref isConfirmed, value)) (EnqueueCommand as AsyncCommand)?.Refresh(); } }
    public BulkSmsEnqueueResult? EnqueueResult { get => enqueueResult; private set { if (Set(ref enqueueResult, value)) Raise(nameof(EnqueueResultText)); } }
    public string EnqueueResultText => EnqueueResult is null ? "" : $"{EnqueueResult.QueuedCount:N0} SMS kuyruğa alındı, {EnqueueResult.ExistingCount:N0} kayıt zaten vardı. Teslimatı Geçmiş sekmesinden izleyebilirsiniz.";
    public bool IsSendLoading { get => isSendLoading; private set => Set(ref isSendLoading, value); }
    public bool IsTemplatesLoading { get => isTemplatesLoading; private set => Set(ref isTemplatesLoading, value); }
    public bool IsHistoryLoading { get => isHistoryLoading; private set => Set(ref isHistoryLoading, value); }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public string SendError { get => sendError; private set { if (Set(ref sendError, value)) Raise(nameof(HasSendError)); } }
    /// <summary>Hata, sol sutunun altinda kaydirmadan gorunmeyebilir; sag paneldeki bant bu yuzden var.</summary>
    public bool HasSendError => !string.IsNullOrEmpty(SendError);
    public string TemplateError { get => templateError; private set => Set(ref templateError, value); }
    public string HistoryError { get => historyError; private set => Set(ref historyError, value); }
    // Isaret degisince liste kendiliginden yenilenir; once kullanici ayrica "Yenile"ye
    // basmak zorundaydi ve kutunun hicbir sey yapmadigini saniyordu.
    public bool IncludeInactive { get => includeInactive; set { if (Set(ref includeInactive, value)) _ = LoadTemplatesAsync(); } }
    public SmsTemplateDetails? EditingTemplate { get => editingTemplate; private set { Set(ref editingTemplate, value); Raise(nameof(TemplateEditorTitle)); (DeactivateTemplateCommand as AsyncCommand)?.Refresh(); } }
    public string TemplateEditorTitle => EditingTemplate is null ? "Yeni şablon" : $"Şablonu düzenle: {EditingTemplate.Name}";
    public string TemplateName { get => templateName; set => Set(ref templateName, value); }
    public string TemplateBody { get => templateBody; set => Set(ref templateBody, value); }
    public string HistoryStatus { get => historyStatus; set => Set(ref historyStatus, value); }
    public string HistoryPhone { get => historyPhone; set => Set(ref historyPhone, value); }
    public string HistoryProvider { get => historyProvider; set => Set(ref historyProvider, value); }
    public string HistoryStudent { get => historyStudent; set => Set(ref historyStudent, value); }
    public DateTime? HistoryFrom { get; set; }
    public DateTime? HistoryTo { get; set; }
    public int HistoryPage { get => historyPage; private set { Set(ref historyPage, value); RefreshPaging(); Raise(nameof(HistoryPageText)); } }
    public int HistoryTotal { get => historyTotal; private set { Set(ref historyTotal, value); RefreshPaging(); Raise(nameof(IsHistoryEmpty)); Raise(nameof(HistoryPageText)); } }
    public string HistoryPageText => HistoryTotal == 0 ? "Kayıt yok" : $"Sayfa {HistoryPage} / {Math.Max(1, (HistoryTotal + 49) / 50)} • {HistoryTotal:N0} kayıt";
    public bool IsHistoryEmpty => !IsHistoryLoading && HistoryTotal == 0 && string.IsNullOrEmpty(HistoryError);

    public ICommand SearchStudentsCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand EnqueueCommand { get; }
    public ICommand RefreshTemplatesCommand { get; }
    public ICommand NewTemplateCommand { get; }
    public ICommand EditTemplateCommand { get; }
    public ICommand SaveTemplateCommand { get; }
    public ICommand DeactivateTemplateCommand { get; }
    public ICommand InsertVariableCommand { get; }
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
        selectedStudentIds.Add(studentId);
        var item = Students.FirstOrDefault(x => x.Id == studentId);
        if (item is not null) item.IsSelected = true;
        TargetType = "Manual";
        InvalidatePreview();
        RaiseSelection();
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
            var result = await api.TargetsAsync(string.IsNullOrWhiteSpace(Search) ? null : Search, lifetime.Token);
            Students.Clear(); foreach (var item in result.Students)
            {
                SmsStudentChoice choice = null!;
                choice = new SmsStudentChoice(item.Id, item.StudentNo, item.Name, () => OnChoiceChanged(choice),
                    item.ClassName, item.SectionName);
                if (selectedStudentIds.Contains(item.Id)) choice.IsSelected = true;
                Students.Add(choice);
            }
            Classes.Clear(); foreach (var item in result.Classes) Classes.Add(item);
            Groups.Clear(); foreach (var item in result.Groups) Groups.Add(item);
        }
        catch (Exception ex) when (Handle(ex, value => SendError = value)) { }
        finally { IsSendLoading = false; Raise(nameof(IsSendTargetsEmpty)); }
    }

    private void OnChoiceChanged(SmsStudentChoice choice)
    {
        if (choice.IsSelected) selectedStudentIds.Add(choice.Id); else selectedStudentIds.Remove(choice.Id);
        InvalidatePreview();
        RaiseSelection();
    }

    private void ClearSelection()
    {
        selectedStudentIds.Clear();
        foreach (var item in Students) item.IsSelected = false;
        InvalidatePreview();
        RaiseSelection();
    }

    private void RaiseSelection()
    {
        Raise(nameof(SelectedStudentCount)); Raise(nameof(SelectedStudentText)); Raise(nameof(SelectedStudentIds));
        (ClearSelectionCommand as RelayCommand)?.Refresh();
    }

    private async Task PreviewAsync()
    {
        EnqueueResult = null;
        // Sunucuya gitmeden yakalanan hatalar: kullanici hangi alani doldurmasi
        // gerektigini aninda, Turkce ve alan adiyla ogrenir.
        var problem = ValidateSend();
        if (problem is not null) { Preview = null; SendError = problem; return; }
        IsSendLoading = true; SendError = "";
        try { previewRequest = BuildRequest(); Preview = await api.PreviewAsync(previewRequest, lifetime.Token); IsConfirmed = false; }
        catch (Exception ex) when (Handle(ex, value => SendError = value)) { Preview = null; }
        finally { IsSendLoading = false; }
    }

    /// <summary>Onizleme oncesi yerel dogrulama; sorun yoksa null doner.</summary>
    public string? ValidateSend()
    {
        if (TargetType == "Manual" && selectedStudentIds.Count == 0) return "En az bir öğrenci seçin: listedeki 'Seç' kutusunu işaretleyin.";
        if (TargetType == "Class" && SelectedClass is null) return "Sınıf hedefi için bir sınıf seçin.";
        if (TargetType == "Group" && SelectedGroup is null) return "Grup hedefi için bir grup seçin.";
        if (TargetType == "Filter" && string.IsNullOrWhiteSpace(Search)) return "Arama filtresi hedefi için arama metni girin.";
        if (UseTemplate && SelectedTemplate is null) return "Bir şablon seçin ya da 'Şablon kullan' işaretini kaldırıp mesajı elle yazın.";
        if (!UseTemplate && string.IsNullOrWhiteSpace(CustomMessage)) return "Mesaj metni boş olamaz.";
        if (!UseTemplate && CustomMessage.Trim().Length > 1600) return "Mesaj en fazla 1600 karakter olabilir.";
        if (UseTemplate)
        {
            var body = SelectedTemplate!.Body;
            if (body.Contains("{{ExpiryDate}}", StringComparison.Ordinal) && !DateOnly.TryParse(ExpiryDate, CultureInfo.CurrentCulture, out _))
                return "Şablon 'Son tarih' değişkeni kullanıyor; gg.aa.yyyy biçiminde bir tarih girin.";
            if (body.Contains("{{EntryTime}}", StringComparison.Ordinal) && !TimeOnly.TryParse(EntryTime, CultureInfo.CurrentCulture, out _))
                return "Şablon 'Giriş saati' değişkeni kullanıyor; SS:dd biçiminde bir saat girin.";
            if (body.Contains("{{Amount}}", StringComparison.Ordinal) && !decimal.TryParse(Amount, NumberStyles.Number, CultureInfo.CurrentCulture, out _))
                return "Şablon 'Tutar' değişkeni kullanıyor; sayısal bir tutar girin (örn. 250,50).";
        }
        return null;
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
        var ids = TargetType == "Manual" ? selectedStudentIds.Order().ToArray() : null;
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
        try
        {
            var items = await api.TemplatesAsync(IncludeInactive, lifetime.Token);
            var sendSelection = SelectedTemplate?.Id;
            Templates.Clear(); foreach (var item in items) Templates.Add(item);
            SendTemplates.Clear(); foreach (var item in items.Where(x => x.IsActive)) SendTemplates.Add(item);
            // Liste yenilenince ComboBox secimi duser; ayni sablon hala aktifse geri secilir.
            SelectedTemplate = SendTemplates.FirstOrDefault(x => x.Id == sendSelection);
        }
        catch (Exception ex) when (Handle(ex, value => TemplateError = value)) { }
        finally { IsTemplatesLoading = false; Raise(nameof(IsTemplatesEmpty)); }
    }
    private void NewTemplate() { EditingTemplate = null; TemplateName = ""; TemplateBody = ""; TemplateError = ""; }
    private void EditTemplate() { EditingTemplate = SelectedTemplateRow; TemplateName = EditingTemplate?.Name ?? ""; TemplateBody = EditingTemplate?.Body ?? ""; TemplateError = ""; }
    private void InsertVariable(string token) => TemplateBody = string.IsNullOrEmpty(TemplateBody) || TemplateBody.EndsWith(' ') ? TemplateBody + token : TemplateBody + " " + token;
    private async Task SaveTemplateAsync()
    {
        if (string.IsNullOrWhiteSpace(TemplateName)) { TemplateError = "Şablon adı boş olamaz."; return; }
        if (string.IsNullOrWhiteSpace(TemplateBody)) { TemplateError = "Şablon metni boş olamaz."; return; }
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
    private void RaiseMessageMetrics() { Raise(nameof(MessageText)); Raise(nameof(CharacterCount)); Raise(nameof(SegmentCount)); Raise(nameof(EncodingHint)); }

    /// <summary>
    /// Hatayi kullaniciya gosterilecek metne cevirir. Sunucunun reddi (ApiRequestException)
    /// mesajiyla AYNEN gosterilir ve ekran "Çevrimdışı" sayilmaz: 400 bir dogrulama
    /// hatasidir, baglanti kopmasi degil. Yalnizca gercek ag hatalari cevrimdisi sayilir.
    /// </summary>
    private bool Handle(Exception exception, Action<string> setter)
    {
        switch (exception)
        {
            case ApiRequestException api:
                IsOffline = false; setter(Localize(api.Message)); return true;
            case LoginRequiredException:
                IsOffline = false; setter("Bu işlem için gerekli SMS izni veya oturum bulunmuyor."); return true;
            case HttpRequestException or TaskCanceledException or InvalidDataException:
                IsOffline = true; setter("SMS servisine ulaşılamadı."); return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Sunucu mesajlari degisken adlarini İngilizce jetonla anar ("'ExpiryDate' şablon
    /// değişkeni..."); kullanicinin ekranda gordugu ad "Son tarih"tir. Eslestirme yapilir.
    /// </summary>
    public static string Localize(string message)
    {
        foreach (var variable in TemplateVariables)
        {
            var name = variable.Token.Trim('{', '}');
            message = message.Replace($"'{name}'", $"'{variable.Label}'", StringComparison.Ordinal);
        }
        return message;
    }

    private void RefreshPaging() { (NextHistoryPageCommand as AsyncCommand)?.Refresh(); (PreviousHistoryPageCommand as AsyncCommand)?.Refresh(); }
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // GSM 03.38 temel alfabesi + genisletme tablosu. Turkce ğ, ş, ı, İ bu tabloda YOKTUR;
    // biri bile gecerse tum mesaj UCS-2 olur (70 / 67 karakterlik segmentler).
    private const string GsmAlphabet = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞ ÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string GsmExtended = "^{}\\[~]|€";
    public static bool UsesUnicode(string text) => text.Any(c => !GsmAlphabet.Contains(c) && !GsmExtended.Contains(c));
    public static int SmsSegments(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var unicode = UsesUnicode(text);
        var length = unicode ? text.Length : text.Sum(c => GsmExtended.Contains(c) ? 2 : 1);
        var single = unicode ? 70 : 160; var multipart = unicode ? 67 : 153;
        return length <= single ? 1 : (int)Math.Ceiling(length / (double)multipart);
    }
    public void Dispose() { lifetime.Cancel(); lifetime.Dispose(); GC.SuppressFinalize(this); }
}
