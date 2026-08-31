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

        vm.DeactivateCommand.Execute(null);
        await Until(() => api.DeactivateCount == 1);
        Assert.Equal(api.Details.Id, api.DeactivatedId);
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
        public Task<PagedResult<StudentListItem>> SearchAsync(StudentQuery query, CancellationToken cancellationToken = default) { SearchCount++; LastQuery = query; return Task.FromResult(SearchResult); }
        public Task<StudentDetails> GetAsync(Guid id, CancellationToken cancellationToken = default) { DetailCount++; Details = Details with { Id = id }; return Task.FromResult(Details); }
        public Task<StudentDetails> SaveAsync(Guid? id, SaveStudentRequest request, CancellationToken cancellationToken = default) { SaveCount++; LastSavedId = id; Details = Details with { StudentNo = request.StudentNo, FirstName = request.FirstName, LastName = request.LastName }; return Task.FromResult(Details); }
        public Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default) { DeactivateCount++; DeactivatedId = id; return Task.CompletedTask; }
        public Task<IReadOnlyList<object>> LoadTabAsync(string tab, Guid studentId, CancellationToken cancellationToken = default) { TabCount++; return Task.FromResult<IReadOnlyList<object>>([new StudentDetailRow(tab)]); }
        public Task GiveLeaveAsync(CreateLeaveRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReplaceCardAsync(Guid studentId, ReplaceCardRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeCardSource(bool available) : ICardReadEventSource
    {
        public bool IsAvailable => available;
        public Task<CardReadEvent?> ReadNextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CardReadEvent?>(new("CARD-REAL", DateTimeOffset.UtcNow, "test-reader"));
    }
}
