using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Cash;

[Collection(Yemekhane.UnitTests.Desktop.UiCollection.Name)]
public sealed class CashViewModelTests
{
    [Fact]
    public async Task InitializeLoadsSummariesTypesAndPagedTransactions()
    {
        var api = new FakeCashApi();
        var vm = new CashViewModel(api, ["cash.read"]);

        await vm.InitializeAsync();

        Assert.Equal(10m, vm.DailyTotal);
        Assert.Equal(20m, vm.WeeklyTotal);
        Assert.Equal(30m, vm.MonthlyTotal);
        Assert.Single(vm.Transactions);
        Assert.False(vm.CanWrite);
        Assert.False(vm.CanManage);
    }

    [Fact]
    public async Task CustomDateSelectionUsesInclusiveCalendarDates()
    {
        var api = new FakeCashApi();
        var vm = new CashViewModel(api, ["cash.read"]) { CustomFrom = new DateTime(2026, 8, 1), CustomTo = new DateTime(2026, 8, 9) };

        vm.LoadCustomCommand.Execute(null);
        await UntilAsync(() => api.LastSummaryPeriod == CashSummaryPeriod.Custom);

        Assert.Equal(new DateOnly(2026, 8, 1), api.LastSummaryFrom);
        Assert.Equal(new DateOnly(2026, 8, 9), api.LastSummaryTo);
    }

    [Fact]
    public async Task AddValidatesTurkishAmountConfirmationAndReusesOperationIdAfterNetworkFailure()
    {
        var api = new FakeCashApi { FailFirstAdd = true };
        var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
        await vm.InitializeAsync();
        vm.OpenAddCommand.Execute(null);
        vm.StudentNumber = "1001";
        vm.LookupStudentCommand.Execute(null); await UntilAsync(() => vm.LookupStudent is not null);
        vm.AmountText = "125,50";
        Assert.Equal("Kayıt bilgilerini onaylayın.", vm.ValidateAdd());
        vm.AddConfirmed = true;

        vm.AddCommand.Execute(null); await UntilAsync(() => api.AddAttempts == 1);
        vm.AddCommand.Execute(null); await UntilAsync(() => api.AddAttempts == 2);

        Assert.Equal(api.OperationIds[0], api.OperationIds[1]);
        Assert.Equal(125.50m, api.LastAdd!.Amount);
    }

    [Fact]
    public async Task VoidRequiresReasonAndExactConfirmation()
    {
        var api = new FakeCashApi();
        var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
        await vm.InitializeAsync(); vm.SelectedTransaction = vm.Transactions[0]; vm.OpenVoidCommand.Execute(null);

        Assert.Contains("10,00", vm.VoidConfirmationText);
        Assert.False(vm.VoidCommand.CanExecute(null));
        vm.VoidReason = "Hatalı tahsilat"; vm.VoidConfirmed = true; vm.VoidCommand.Execute(null);
        await UntilAsync(() => api.VoidCount == 1);

        Assert.Equal("Hatalı tahsilat", api.LastVoidReason);
    }

    [Fact]
    public async Task TypeCrudRequiresManagePermission()
    {
        var readOnly = new CashViewModel(new FakeCashApi(), ["cash.read"]);
        Assert.False(readOnly.SaveTypeCommand.CanExecute(null));

        var api = new FakeCashApi(); var manager = new CashViewModel(api, ["cash.read", "cash.manage"]);
        await manager.InitializeAsync(); manager.TypeName = "Bağış"; manager.SaveTypeCommand.Execute(null);
        await UntilAsync(() => api.TypeSaveCount == 1);

        Assert.True(manager.CanManage);
    }

    [Fact]
    public async Task NetworkFailureSetsOfflineStateWithoutFakeData()
    {
        var vm = new CashViewModel(new FakeCashApi { FailRefresh = true }, ["cash.read"]);
        await vm.InitializeAsync();
        Assert.True(vm.IsOffline);
        Assert.True(vm.HasError);
        Assert.Empty(vm.Transactions);
    }

    [Fact]
    public void CashXamlLoadsWithVirtualizedDenseGridAndRealReportRoute()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new CashView(); Yemekhane.UnitTests.Desktop.UiThread.ApplyResources(view);
                var grid = Assert.IsType<DataGrid>(view.FindName("TransactionsGrid"));
                Assert.True(grid.EnableRowVirtualization);
                Assert.True(grid.EnableColumnVirtualization);
                // Gorev 3: view'in kendi RowHeight="30" gecersiz kilmasi silindi;
                // artik DesignSystem.xaml'in DataGrid stili (34) gecerli.
                Assert.Equal(34, grid.RowHeight);
                Assert.Equal(8, grid.Columns.Count);
                var xaml = File.ReadAllText(Path.Combine(FindRoot(), "src", "Yemekhane.Desktop", "Views", "CashView.xaml"));
                Assert.Contains("Command=\"{Binding OpenReportsCommand}\"", xaml);
                Assert.DoesNotContain("TASK 049", xaml);
                Assert.DoesNotContain("fake", xaml, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public async Task ApiClientSerializesFiltersAndTurkishDecimalAsJsonNumber()
    {
        var handler = new RecordingHandler();
        var client = new CashApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") }, new Session());
        await client.TransactionsAsync(new IncomeTransactionFilter(CardNumber: "A 1", IsVoided: false, Page: 3, PageSize: 25));
        await client.AddAsync(new CreateIncomeTransactionRequest(Guid.NewGuid(), null, null, DateTimeOffset.UtcNow, Guid.NewGuid(), 125.50m));

        Assert.Contains("cardNumber=A%201", handler.Requests[0].Uri.Query);
        Assert.Contains("isVoided=false", handler.Requests[0].Uri.Query);
        Assert.Contains("page=3", handler.Requests[0].Uri.Query);
        Assert.Contains("125.50", handler.Requests[1].Body);
        Assert.DoesNotContain("125,50", handler.Requests[1].Body);
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 100 && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate());
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class FakeCashApi : ICashApiClient
    {
        private readonly Guid typeId = Guid.NewGuid();
        private readonly Guid studentId = Guid.NewGuid();
        public bool FailRefresh { get; init; }
        public bool FailFirstAdd { get; init; }
        public int AddAttempts { get; private set; }
        public int VoidCount { get; private set; }
        public int TypeSaveCount { get; private set; }
        public string? LastVoidReason { get; private set; }
        public CreateIncomeTransactionRequest? LastAdd { get; private set; }
        public List<Guid> OperationIds { get; } = [];
        public CashSummaryPeriod? LastSummaryPeriod { get; private set; }
        public DateOnly? LastSummaryFrom { get; private set; }
        public DateOnly? LastSummaryTo { get; private set; }

        public Task<CashSummary> SummaryAsync(CashSummaryPeriod period, DateOnly? anchorDate = null, DateOnly? startDate = null, DateOnly? endDate = null, CancellationToken cancellationToken = default)
        {
            if (FailRefresh) throw new HttpRequestException();
            LastSummaryPeriod = period; LastSummaryFrom = startDate; LastSummaryTo = endDate;
            var amount = period switch { CashSummaryPeriod.Daily => 10m, CashSummaryPeriod.IsoWeek => 20m, CashSummaryPeriod.Monthly => 30m, _ => 40m };
            return Task.FromResult(new CashSummary(period, startDate ?? anchorDate ?? new DateOnly(2026, 8, 31), endDate ?? anchorDate ?? new DateOnly(2026, 8, 31), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, amount, 1, 0, 0, [new(typeId, "Nakit", amount, 1)]));
        }
        public Task<PagedResult<IncomeTransactionDetails>> TransactionsAsync(IncomeTransactionFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<IncomeTransactionDetails>([Transaction()], filter.Page, filter.PageSize, 1));
        public Task<IReadOnlyList<IncomeTypeDetails>> TypesAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IncomeTypeDetails>>([new(typeId, "Nakit", true)]);
        public Task<IncomeTransactionDetails> AddAsync(CreateIncomeTransactionRequest request, CancellationToken cancellationToken = default)
        {
            AddAttempts++; OperationIds.Add(request.OperationId); LastAdd = request;
            if (FailFirstAdd && AddAttempts == 1) throw new HttpRequestException();
            return Task.FromResult(Transaction());
        }
        public Task<IncomeTransactionDetails> VoidAsync(Guid id, string reason, CancellationToken cancellationToken = default)
        { VoidCount++; LastVoidReason = reason; return Task.FromResult(Transaction() with { IsVoided = true, VoidReason = reason }); }
        public Task<IncomeTypeDetails> SaveTypeAsync(Guid? id, SaveIncomeTypeRequest request, CancellationToken cancellationToken = default)
        { TypeSaveCount++; return Task.FromResult(new IncomeTypeDetails(id ?? Guid.NewGuid(), request.Name, request.IsActive)); }
        public Task DeactivateTypeAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PagedResult<StudentListItem>> FindStudentAsync(string? studentNumber, string? cardNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<StudentListItem>([new(studentId, "1001", "CARD1", "Ada", "Yılmaz", null, null, null, null, true, 0, false, null)], 1, 2, 1));
        private IncomeTransactionDetails Transaction() => new(Guid.NewGuid(), Guid.NewGuid(), studentId, "Ada Yılmaz", "CARD1", DateTimeOffset.UtcNow, typeId, "Nakit", 10m, null, Guid.NewGuid(), false, null, null, null);
    }

    private sealed class Session : IJwtSession { public string? AccessToken => "token"; public bool IsAuthenticated => true; }
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!, body));
            var json = request.Method == HttpMethod.Get
                ? "{\"items\":[],\"page\":3,\"pageSize\":25,\"totalCount\":0}"
                : JsonSerializer.Serialize(new IncomeTransactionDetails(Guid.NewGuid(), Guid.NewGuid(), null, null, null, DateTimeOffset.UtcNow, Guid.NewGuid(), "Nakit", 125.50m, null, Guid.NewGuid(), false, null, null, null));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
