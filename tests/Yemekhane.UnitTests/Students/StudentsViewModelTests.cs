using Yemekhane.Api.Controllers;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Common;
using Yemekhane.Application.Leaves;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.UnitTests.Students;

public sealed class StudentsViewModelTests
{
    [Fact]
    public async Task GeneralSearchDebouncesAndIgnoresSingleCharacter()
    {
        var api = new FakeApi();
        using var vm = Create(api);
        // Tek karakter aramayi tetiklememeli; debounce suresinin uzerinde beklenip dogrulanir.
        vm.Search = "A";
        await Task.Delay(420);
        Assert.Equal(0, api.SearchCount);

        // Hizli ardisik degisimler tek cagriya inmelidir. Sabit bekleme yerine kosula gore beklenir:
        // yuk altinda 350 ms'lik debounce gecikebilir ve sabit sure testi kararsiz yapar.
        vm.Search = "Ad";
        vm.Search = "Ada";
        await Until(() => api.SearchCount > 0);

        Assert.Equal(1, api.SearchCount);
        Assert.Equal("Ada", api.LastQuery!.Search);
    }

    [Fact]
    public async Task PaginationUsesServerPageAndTotal()
    {
        var api = new FakeApi { SearchResult = Page(2, 120) };
        using var vm = Create(api);
        await vm.LoadAsync(2);
        Assert.Equal(2, vm.Page);
        Assert.Equal(120, vm.TotalCount);
        Assert.Contains("120", vm.PageText);
    }

    [Fact]
    public async Task QuickDrawerAndDetailTabsLoadOnlyWhenSelected()
    {
        var api = new FakeApi(); using var vm = Create(api); var row = Row();
        vm.OpenQuickDetailCommand.Execute(row);
        Assert.True(vm.IsQuickDetailOpen);
        Assert.Equal(0, api.DetailCount);

        vm.OpenFullDetailCommand.Execute(row);
        await Until(() => vm.IsDetailOpen);
        Assert.Equal(10, vm.Tabs.Count);
        Assert.Equal(0, api.TabCount);
        vm.SelectedTab = vm.Tabs[1];
        await Until(() => api.TabCount == 1);
        Assert.True(vm.Tabs[1].IsLoaded);
    }

    [Fact]
    public async Task CreateUpdateAndDeactivateUseRealClientOperations()
    {
        var api = new FakeApi(); using var vm = Create(api, "students.write", "students.deactivate");
        vm.HandleRoute(ShellRoutes.StudentsCreate);
        vm.FormStudentNo = "42"; vm.FormFirstName = "Ada"; vm.FormLastName = "Yılmaz";
        vm.SaveStudentCommand.Execute(null);
        await Until(() => api.SaveCount == 1);
        Assert.Null(api.LastSavedId);

        vm.EditStudentCommand.Execute(null);
        vm.FormLastName = "Demir";
        vm.SaveStudentCommand.Execute(null);
        await Until(() => api.SaveCount == 2);
        Assert.Equal(api.Details.Id, api.LastSavedId);

        // Pasiflestir artik DELETE degil, IsActive=false ile yeniden yazmadir (geri alinabilir).
        vm.DeactivateCommand.Execute(null);
        await Until(() => api.SaveCount == 3);
        Assert.False(api.LastSaveRequest!.IsActive);
        Assert.Equal(0, api.DeactivateCount);

        // Silme iki adimli: ilk tiklama onay ister, ikincisi DELETE cagirir.
        vm.DeleteCommand.Execute(null);
        await Until(() => vm.IsDeleteArmed);
        Assert.Equal(0, api.DeactivateCount);
        Assert.Equal("Silmeyi Onayla", vm.DeleteButtonText);
        vm.CancelDeleteCommand.Execute(null);
        Assert.False(vm.IsDeleteArmed);

        vm.DeleteCommand.Execute(null);
        await Until(() => vm.IsDeleteArmed);
        var id = api.Details.Id;
        vm.DeleteCommand.Execute(null);
        await Until(() => api.DeactivateCount == 1);
        Assert.Equal(id, api.DeactivatedId);
        Assert.Null(vm.Details);
        Assert.Empty(vm.Tabs);
        Assert.Contains("silindi", vm.InfoMessage);
    }

    [Fact]
    public void PermissionsControlActionsAndSensitiveFields()
    {
        using var denied = Create(new FakeApi());
        Assert.False(denied.CanWrite); Assert.False(denied.CanDeactivate); Assert.False(denied.CanReadSensitive);
        using var allowed = Create(new FakeApi(), "students.write", "students.deactivate", "students.sensitive.read", "cards.manage");
        Assert.True(allowed.CanWrite); Assert.True(allowed.CanDeactivate); Assert.True(allowed.CanReadSensitive); Assert.True(allowed.CanManageCards);
        Assert.False(allowed.CanGrantEntitlement);
    }

    [Fact]
    public void SensitiveProjectionMasksPhoneAndDetails()
    {
        var row = Row() with { ParentPhone = "+905551234567" };
        var maskedPage = StudentSensitiveMasker.Mask(new PagedResult<StudentListItem>([row], 1, 50, 1));
        Assert.Equal("•••••••••4567", maskedPage.Items[0].ParentPhone);
        var details = StudentSensitiveMasker.Mask(Details() with { NationalId = "12345678901", Address = "Adres" });
        Assert.Equal("•••••••••••", details.NationalId);
        Assert.Equal("••••••", details.Address);
    }

    [Fact]
    public void StudentRoutesSupportListCreateAndDailyTrackingDetail()
    {
        var navigation = new ShellNavigationService([ShellRoutes.Students, ShellRoutes.StudentDetail]);
        var routes = new List<string>(); navigation.NavigationRequested += (_, e) => routes.Add(e.Route);
        navigation.Navigate(ShellRoutes.StudentsCreate);
        navigation.Navigate($"{ShellRoutes.StudentDetail}/{Guid.NewGuid():D}");
        Assert.Equal(2, routes.Count);
    }

    [Fact]
    public async Task CardWorkflowShowsHardwareMessageWhenReaderIsUnavailable()
    {
        using var vm = new StudentsViewModel(new FakeApi(), new ShellNavigationService([ShellRoutes.Students]),
            ["cards.manage"], cardReadSource: new FakeCardSource(false));

        await vm.OpenCardWorkflowAsync();

        Assert.True(vm.IsCardWorkflowOpen);
        Assert.Contains("aktif kart okuyucu bulunamadı", vm.CardWorkflowMessage);
    }

    [Fact]
    public async Task CardWorkflowUsesReaderEventAndSearchesExactCard()
    {
        var api = new FakeApi();
        using var vm = new StudentsViewModel(api, new ShellNavigationService([ShellRoutes.Students]),
            ["cards.manage"], cardReadSource: new FakeCardSource(true));

        await vm.OpenCardWorkflowAsync();

        Assert.Equal("CARD-REAL", vm.CardNumber);
        Assert.Equal("CARD-REAL", vm.NewCardNumber);
        Assert.Equal("CARD-REAL", api.LastQuery?.CardNumber);
        Assert.Equal(1, api.SearchCount);
    }

    /// <summary>
    /// PUT tam kaydi bekler: formda olmayan alanlar (sinif, sube, bolum, dogum tarihi...)
    /// Details'ten AYNEN gitmeli. Onceden gitmiyordu ve adini duzelttigimiz ogrencinin
    /// sinifi/subesi sunucuda siliniyordu (canli API'de 8B/B -> null/null olarak dogrulandi).
    /// </summary>
    [Fact]
    public async Task DuzenlemeSinifVeSubeyiKorur()
    {
        var api = new FakeApi(); using var vm = Create(api, "students.write");
        var classId = Guid.NewGuid(); var sectionId = Guid.NewGuid();
        api.SetDetails(Details() with { ClassId = classId, SectionId = sectionId, BirthDate = new DateOnly(2014, 5, 1), FingerprintId = "FP-1" });
        vm.OpenFullDetailCommand.Execute(Row());
        await Until(() => vm.Details is not null);

        vm.EditStudentCommand.Execute(null);
        vm.FormLastName = "Demir";
        vm.SaveStudentCommand.Execute(null);
        await Until(() => api.SaveCount == 1);

        Assert.Equal(classId, api.LastSaveRequest!.ClassId);
        Assert.Equal(sectionId, api.LastSaveRequest.SectionId);
        Assert.Equal(new DateOnly(2014, 5, 1), api.LastSaveRequest.BirthDate);
        Assert.Equal("FP-1", api.LastSaveRequest.FingerprintId);
        Assert.True(api.LastSaveRequest.IsActive);
    }

    /// <summary>Iptal, yazilanlari atip sunucudaki degerlere doner; form kapanir.</summary>
    [Fact]
    public async Task IptalDegisiklikleriGeriAlir()
    {
        var api = new FakeApi(); using var vm = Create(api, "students.write");
        vm.OpenFullDetailCommand.Execute(Row());
        await Until(() => vm.Details is not null);
        Assert.False(vm.CancelEditCommand.CanExecute(null));

        vm.EditStudentCommand.Execute(null);
        vm.FormFirstName = "YANLIŞ"; vm.FormNotes = "silinecek";
        Assert.True(vm.CancelEditCommand.CanExecute(null));
        vm.CancelEditCommand.Execute(null);

        Assert.False(vm.IsFormOpen);
        Assert.Equal("Ada", vm.FormFirstName);
        Assert.Null(vm.FormNotes);
        Assert.Equal(0, api.SaveCount);
    }

    /// <summary>
    /// Pasif ogrenci icin geri donus yolu: Aktiflestir, kaydi IsActive=true ile yeniden
    /// yazar; Pasiflestir gizlenir, Aktiflestir gorunur ve tersi.
    /// </summary>
    [Fact]
    public async Task PasifOgrenciAktiflestirilebilir()
    {
        var api = new FakeApi(); using var vm = Create(api, "students.write");
        api.SetDetails(Details() with { IsActive = false });
        vm.OpenFullDetailCommand.Execute(Row() with { IsActive = false });
        await Until(() => vm.Details is not null);
        Assert.True(vm.ShowActivate); Assert.False(vm.ShowDeactivate);
        Assert.True(vm.ActivateCommand.CanExecute(null)); Assert.False(vm.DeactivateCommand.CanExecute(null));

        vm.ActivateCommand.Execute(null);
        await Until(() => api.SaveCount == 1 && vm.Details?.IsActive == true);

        Assert.True(api.LastSaveRequest!.IsActive);
        Assert.Equal(api.Details.Id, api.LastSavedId);
        Assert.False(vm.ShowActivate); Assert.True(vm.ShowDeactivate);
    }

    /// <summary>
    /// Kartsiz ogrenciye "Kart Ata" ATAMA ucunu cagirir; kartli ogrencide DEGISTIRME.
    /// Onceden her zaman degistirme cagriliyordu ve kartsiz ogrenciye ilk kart verilemiyordu.
    /// </summary>
    [Fact]
    public async Task KartsizOgrenciyeKartAtanirKartliyaDegistirilir()
    {
        var api = new FakeApi(); using var vm = Create(api, "cards.manage");
        var cardless = Row() with { CardNumber = null };
        api.SearchResult = new PagedResult<StudentListItem>([cardless], 1, 50, 1);
        vm.OpenFullDetailCommand.Execute(cardless);
        await Until(() => vm.Details?.Id == cardless.Id);
        Assert.Equal("Kart Ata", vm.CardActionText);

        vm.NewCardNumber = "NEW-1";
        vm.ReplaceCardCommand.Execute(null);
        await Until(() => api.AssignCount == 1);
        Assert.Equal(0, api.ReplaceCount);
        Assert.Equal("", vm.NewCardNumber);

        var carded = Row();
        api.SearchResult = new PagedResult<StudentListItem>([carded], 1, 50, 1);
        vm.OpenFullDetailCommand.Execute(carded);
        await Until(() => vm.Details?.Id == carded.Id);
        Assert.Equal("Kart Değiştir", vm.CardActionText);
        vm.NewCardNumber = "NEW-2";
        vm.ReplaceCardCommand.Execute(null);
        await Until(() => api.ReplaceCount == 1);
        Assert.Equal(1, api.AssignCount);
    }

    /// <summary>Kart cakismasinda sunucu mesaji kullaniciya AYNEN ulasir; sessiz kalmaz.</summary>
    [Fact]
    public async Task KartCakismasiSunucuMesajiniGosterir()
    {
        var api = new FakeApi { ReplaceFailure = new ApiRequestException("Kart No daha önce sisteme tanımlanmış.", System.Net.HttpStatusCode.Conflict) };
        using var vm = Create(api, "cards.manage");
        vm.OpenFullDetailCommand.Execute(Row());
        await Until(() => vm.Details is not null);
        vm.NewCardNumber = "USED";
        vm.ReplaceCardCommand.Execute(null);
        await Until(() => vm.HasError);
        Assert.Equal("Kart No daha önce sisteme tanımlanmış.", vm.ErrorMessage);
    }

    /// <summary>
    /// Izin verilince Izinler sekmesi daha once acilmis olsa bile YENIDEN yuklenir ve
    /// secilir; eski liste yeni izni gostermezdi.
    /// </summary>
    [Fact]
    public async Task IzinVerilinceIzinlerSekmesiYenidenYuklenir()
    {
        var api = new FakeApi(); using var vm = Create(api, "students.write");
        vm.OpenFullDetailCommand.Execute(Row());
        await Until(() => vm.Tabs.Count == 10);
        vm.SelectedTab = vm.Tabs.First(t => t.Key == "Leaves");
        await Until(() => api.TabCount == 1);

        vm.GiveLeaveCommand.Execute(null);
        await Until(() => api.LeaveCount == 1 && api.TabCount == 2);
        Assert.Equal("Leaves", vm.SelectedTab!.Key);
        Assert.True(vm.SelectedTab.IsLoaded);
    }

    /// <summary>
    /// Kayit sonrasi kaydedilen ogrenci listede SECILI ve form dolu olmali. Liste
    /// yenilenirken DataGrid secimi null'a ceker; onceden form bomboş kaliyordu ve
    /// kullanici kaydin kaybolduğunu saniyordu.
    /// </summary>
    [Fact]
    public async Task KayitSonrasiOgrenciSeciliVeFormDolu()
    {
        var api = new FakeApi(); using var vm = Create(api, "students.write");
        var saved = Row() with { Id = api.Details.Id, StudentNo = "42", FirstName = "Ada" };
        api.SearchResult = new PagedResult<StudentListItem>([saved], 1, 50, 1);
        vm.NewStudentCommand.Execute(null);
        vm.FormStudentNo = "42"; vm.FormFirstName = "Ada"; vm.FormLastName = "Yılmaz"; vm.FormNotes = "not";
        vm.SaveStudentCommand.Execute(null);
        await Until(() => api.SaveCount == 1 && !vm.IsFormOpen);

        Assert.Equal(saved.Id, vm.SelectedStudent?.Id);
        Assert.Equal("42", vm.FormStudentNo);
        Assert.Equal("Ada", vm.FormFirstName);
        Assert.Equal("not", vm.FormNotes);
        Assert.Equal(10, vm.Tabs.Count);
    }

    /// <summary>
    /// Gecikmeli arama listeyi ARAYUZ baglaminda yenilemeli. Havuz is parcacigindan
    /// ObservableCollection degistirmek WPF'te NotSupportedException atar ve Task.Run
    /// icinde kaybolur: kullanici yazar, hicbir sey olmaz.
    /// </summary>
    [Fact]
    public async Task GecikmeliAramaArayuzBaglaminaDoner()
    {
        var context = new RecordingContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var api = new FakeApi(); using var vm = Create(api);
            vm.Search = "Ada";
            await Until(() => api.SearchCount > 0);
            // Arama, VM'in kurulusta yakaladigi baglam UZERINDEN calismali. Salt "Post
            // cagrildi mi" yetmez: testin kendi await'leri de bu baglama post eder.
            Assert.Same(context, api.LastSearchContext);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    /// <summary>
    /// Post edilen isi, Current'i kendisi olan bir is parcaciginda calistirir -- WPF
    /// Dispatcher'in yaptigi gibi. Boylece "hangi baglamda calisti" sorusu sorulabilir.
    /// </summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ => { SetSynchronizationContext(this); d(state); });
    }

    private static StudentsViewModel Create(FakeApi api, params string[] permissions) =>
        new(api, new ShellNavigationService([ShellRoutes.Students, ShellRoutes.StudentDetail]), permissions);
    private static StudentListItem Row() => new(Guid.NewGuid(), "42", "CARD42", "Ada", "Yılmaz", "5", "A", "Ortaokul", "+905551234567", true, 1, true, DateTimeOffset.UtcNow);
    private static StudentDetails Details() => new(Guid.NewGuid(), "42", null, "Ada", "Yılmaz", null, null, null, null, null, null, null, null, null, null, true, new DateOnly(2026, 8, 31));
    private static PagedResult<StudentListItem> Page(int page, int total) => new([Row()], page, 50, total);
    private static async Task Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < timeout) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeApi : IStudentApiClient
    {
        public int SearchCount, DetailCount, TabCount, SaveCount, DeactivateCount;
        public StudentQuery? LastQuery;
        public Guid? LastSavedId, DeactivatedId;
        public PagedResult<StudentListItem> SearchResult { get; set; } = Page(1, 1);
        public StudentDetails Details { get; private set; } = StudentsViewModelTests.Details();
        public SynchronizationContext? LastSearchContext;
        public Task<PagedResult<StudentListItem>> SearchAsync(StudentQuery query, CancellationToken cancellationToken = default)
        { LastSearchContext = SynchronizationContext.Current; SearchCount++; LastQuery = query; return Task.FromResult(SearchResult); }
        public Task<StudentDetails> GetAsync(Guid id, CancellationToken cancellationToken = default) { DetailCount++; Details = Details with { Id = id }; return Task.FromResult(Details); }
        public void SetDetails(StudentDetails value) => Details = value;
        public Task<StudentDetails> SaveAsync(Guid? id, SaveStudentRequest request, CancellationToken cancellationToken = default)
        {
            SaveCount++; LastSavedId = id; LastSaveRequest = request;
            Details = Details with { StudentNo = request.StudentNo, FirstName = request.FirstName, LastName = request.LastName, Notes = request.Notes, IsActive = request.IsActive, ClassId = request.ClassId, SectionId = request.SectionId };
            return Task.FromResult(Details);
        }
        public Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default) { DeactivateCount++; DeactivatedId = id; return Task.CompletedTask; }
        public Task<IReadOnlyList<object>> LoadTabAsync(string tab, Guid studentId, CancellationToken cancellationToken = default) { TabCount++; return Task.FromResult<IReadOnlyList<object>>([new StudentDetailRow(tab)]); }
        public int LeaveCount, ReplaceCount, AssignCount;
        public SaveStudentRequest? LastSaveRequest;
        public Exception? ReplaceFailure;
        public Task GiveLeaveAsync(CreateLeaveRequest request, CancellationToken cancellationToken = default) { LeaveCount++; return Task.CompletedTask; }
        public Task ReplaceCardAsync(Guid studentId, ReplaceCardRequest request, CancellationToken cancellationToken = default)
        { ReplaceCount++; return ReplaceFailure is null ? Task.CompletedTask : Task.FromException(ReplaceFailure); }
        public Task AssignCardAsync(Guid studentId, AssignCardRequest request, CancellationToken cancellationToken = default) { AssignCount++; return Task.CompletedTask; }
    }

    private sealed class FakeCardSource(bool available) : ICardReadEventSource
    {
        public bool IsAvailable => available;
        public Task<CardReadEvent?> ReadNextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CardReadEvent?>(new("CARD-REAL", DateTimeOffset.UtcNow, "test-reader"));
    }
}
