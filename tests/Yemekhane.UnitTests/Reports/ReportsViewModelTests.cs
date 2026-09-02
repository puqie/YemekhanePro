using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Controls;
using Yemekhane.Application.Reports;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Reports;

[Collection(Yemekhane.UnitTests.Desktop.UiCollection.Name)]
public sealed class ReportsViewModelTests
{
    [Fact]
    public async Task FiltersAreDraftUntilApplyAndMapToServerQuery()
    {
        var api = new FakeApi();
        using var vm = new ReportsViewModel(api, ["reports.read"], new MemoryLayouts(), new FakeDialogs());
        vm.StudentNo = " 42 "; vm.CardNo = " C 1 "; vm.FirstName = "Ada"; vm.PageSize = 100;
        Assert.Empty(api.Queries);

        await vm.ApplyAsync();

        var query = Assert.Single(api.Queries).Query;
        Assert.Equal("42", query.StudentNo); Assert.Equal("C 1", query.CardNo); Assert.Equal("Ada", query.FirstName);
        Assert.Equal(100, query.PageSize); Assert.Equal(TimeSpan.FromHours(3), query.Start!.Value.Offset);
        Assert.Equal(TimeSpan.FromDays(1) - TimeSpan.FromTicks(1), query.End!.Value.TimeOfDay);
    }

    [Fact]
    public async Task ResultMapsSummaryDynamicColumnsSortingAndPaging()
    {
        var api = new FakeApi { Result = Result(125) };
        using var vm = new ReportsViewModel(api, ["reports.read"], new MemoryLayouts(), new FakeDialogs());
        await vm.ApplyAsync();
        Assert.Equal(125, vm.Summary.TotalRecords); Assert.Equal(3, vm.Summary.Passed); Assert.Contains("Reddedilen", vm.SummaryText);
        Assert.Contains(vm.Columns, x => x.Key == "Device"); Assert.Single(vm.Rows);

        await vm.SortAsync("studentNo");
        vm.NextPageCommand.Execute(null); await UntilAsync(() => api.Queries.Count >= 3);
        Assert.Equal("studentNo", api.Queries[^1].Query.SortBy); Assert.Equal(2, api.Queries[^1].Query.Page);
    }

    [Fact]
    public void LayoutAndSelectedCopyUseVisibleDisplayOrder()
    {
        var layouts = new MemoryLayouts(); var dialogs = new FakeDialogs();
        using var vm = new ReportsViewModel(new FakeApi(), ["reports.read"], layouts, dialogs);
        var layout = vm.Columns.Select((x, i) => new ReportColumnLayout(x.Key, i, x.Width, false)).ToList();
        layout[layout.FindIndex(x => x.Key == "Name")] = new("Name", 0, 210, true);
        layout[layout.FindIndex(x => x.Key == "Date")] = new("Date", 1, 130, true);
        vm.SaveLayout(layout);
        vm.ReplaceSelection([new ReportGridRow(Row())]);
        vm.CopySelectedCommand.Execute(null);

        Assert.Equal(210, vm.Columns.Single(x => x.Key == "Name").Width);
        Assert.StartsWith("AD SOYAD\tTARİH", dialogs.Copied);
        Assert.Contains("Ada Yılmaz", dialogs.Copied);
        Assert.NotEmpty(layouts.Saved);
    }

    [Fact]
    public async Task ExportsUseAppliedFiltersWithoutPagingAndRespectPermission()
    {
        var api = new FakeApi(); var dialogs = new FakeDialogs { Path = "report.pdf" };
        using var vm = new ReportsViewModel(api, ["reports.read", "reports.export"], new MemoryLayouts(), dialogs);
        vm.StudentNo = "123"; await vm.ApplyAsync();
        vm.ExportPdfCommand.Execute(null); await UntilAsync(() => api.Exports.Count == 1);
        Assert.Equal("123", api.Exports[0].Query.StudentNo); Assert.Equal(ReportExportFormat.Pdf, api.Exports[0].Format);
        Assert.Contains("kaydedildi", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        using var denied = new ReportsViewModel(api, ["reports.read"], new MemoryLayouts(), dialogs);
        Assert.False(denied.ExportPdfCommand.CanExecute(null));
    }

    [Fact]
    public async Task ApiClientBuildsExactExportQueryAndWritesAtomically()
    {
        var handler = new ContentHandler(HttpStatusCode.OK, "file-data");
        var client = new ReportApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://localhost/") }, new Session());
        var directory = Path.Combine(Path.GetTempPath(), "yemekhane-report-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var target = Path.Combine(directory, "out.csv");
        try
        {
            var query = new ReportQuery(StudentNo: "A 1", SortBy: "studentNo", Descending: false, Page: 9, PageSize: 25);
            await client.ExportAsync(ReportType.DailyAccess, query, ReportExportFormat.Csv, target);
            Assert.Equal("file-data", await File.ReadAllTextAsync(target));
            Assert.Contains("studentNo=A%201", handler.Uri!.Query); Assert.Contains("sortBy=studentNo", handler.Uri.Query);
            Assert.DoesNotContain("page=", handler.Uri.Query); Assert.Empty(Directory.GetFiles(directory, "*.tmp"));

            handler.Status = HttpStatusCode.InternalServerError;
            await File.WriteAllTextAsync(target, "existing");
            await Assert.ThrowsAsync<HttpRequestException>(() => client.ExportAsync(ReportType.DailyAccess, query, ReportExportFormat.Csv, target));
            Assert.Equal("existing", await File.ReadAllTextAsync(target));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ReportsXamlLoadsWithVirtualizedMultiSelectDynamicGrid()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new ReportsView(); Yemekhane.UnitTests.Desktop.UiThread.ApplyResources(view); var grid = Assert.IsType<DataGrid>(view.FindName("ReportGrid"));
                Assert.True(grid.EnableRowVirtualization); Assert.True(grid.EnableColumnVirtualization);
                Assert.Equal(DataGridSelectionMode.Extended, grid.SelectionMode); Assert.Empty(grid.Columns);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>
    /// Gunluk Kasa ile Gelir ayni sutunlari gosteriyordu; sunucu artik Gunluk Kasa'yi gun +
    /// gelir turu kirilimi olarak dondurdugu icin ekran da islem sayisini ve turu gostermeli.
    /// </summary>
    [Fact]
    public async Task DailyCashShowsDailyBreakdownColumnsAndTransactionCount()
    {
        var api = new FakeApi { Result = Result(4) };
        using var vm = new ReportsViewModel(api, ["reports.read"], new MemoryLayouts(), new FakeDialogs());
        vm.SelectedReport = vm.ReportTypes.Single(x => x.Type == ReportType.DailyCash);
        await UntilAsync(() => api.Queries.Any(x => x.Type == ReportType.DailyCash) && !vm.IsLoading);

        Assert.Equal(["TARİH", "GELİR TÜRÜ", "İŞLEM", "DURUM", "TUTAR"], vm.Columns.Select(x => x.Header));
        Assert.Contains("İşlem 4", vm.SummaryText);
        Assert.Contains("Tutar", vm.SummaryText);

        vm.SelectedReport = vm.ReportTypes.Single(x => x.Type == ReportType.Income);
        Assert.Contains("AD SOYAD", vm.Columns.Select(x => x.Header));
        Assert.DoesNotContain("İŞLEM", vm.Columns.Select(x => x.Header));
    }

    private static ReportResult Result(int total) => new([Row()], 1, 50, new ReportSummary(total, 3, 2, 4, 125.50m));
    private static ReportRow Row() => new() { Id = Guid.NewGuid(), Type = ReportType.DailyAccess, Timestamp = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.FromHours(3)), FirstName = "Ada", LastName = "Yılmaz", StudentNo = "42", Decision = "ALLOW" };
    private static async Task UntilAsync(Func<bool> predicate) { for (var i = 0; i < 100 && !predicate(); i++) await Task.Delay(10); Assert.True(predicate()); }

    private sealed class FakeApi : IReportApiClient
    {
        public List<(ReportType Type, ReportQuery Query)> Queries { get; } = [];
        public List<(ReportType Type, ReportQuery Query, ReportExportFormat Format, string Path)> Exports { get; } = [];
        public ReportResult Result { get; set; } = ReportsViewModelTests.Result(1);
        public Task<ReportResult> QueryAsync(ReportType type, ReportQuery query, CancellationToken cancellationToken = default) { Queries.Add((type, query)); return Task.FromResult(Result with { Page = query.Page, PageSize = query.PageSize }); }
        public Task ExportAsync(ReportType type, ReportQuery query, ReportExportFormat format, string targetPath, CancellationToken cancellationToken = default) { Exports.Add((type, query, format, targetPath)); return Task.CompletedTask; }
    }
    private sealed class MemoryLayouts : IReportLayoutStore
    {
        public IReadOnlyList<ReportColumnLayout> Saved { get; private set; } = [];
        public IReadOnlyList<ReportColumnLayout> Load(ReportType type) => Saved;
        public void Save(ReportType type, IReadOnlyList<ReportColumnLayout> columns) => Saved = columns;
    }
    private sealed class FakeDialogs : IReportDialogService
    {
        public string? Path { get; set; }
        public string Copied { get; private set; } = "";
        public string? ChoosePath(ReportType type, ReportExportFormat format) => Path;
        public void CopyText(string value) => Copied = value;
    }
    private sealed class Session : IJwtSession { public string? AccessToken => "token"; public bool IsAuthenticated => true; }
    private sealed class ContentHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = status;
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Uri = request.RequestUri; return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(content, Encoding.UTF8) }); }
    }
}
