using Yemekhane.Application.Search;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Search;

public sealed class GlobalSearchViewModelTests
{
    [Fact]
    public async Task DebounceCancelsStaleResponse()
    {
        var api = new DelayedApi();
        using var vm = new GlobalSearchViewModel(api, Navigation(), new MemoryRecentStore());

        // "Ada" aramasi baslayana kadar beklenir. Sabit uyku yerine kosula gore beklenir:
        // yuk altinda 250 ms'lik debounce gecikir ve sabit sure testi kararsiz yapar.
        vm.Query = "Ada";
        await Until(() => api.Started > 0);

        // "Ada" hala ucusta iken yeni sorgu onu iptal etmelidir.
        vm.Query = "Ece";
        await Until(() => api.Canceled > 0);
        await Until(() => vm.Results.Count == 1);

        Assert.True(api.Canceled > 0);
        Assert.Equal("Ece", Assert.Single(vm.Results).Title);
    }

    [Fact]
    public async Task KeyboardSelectionNavigatesRealRouteAndStoresRecent()
    {
        var routes = new List<string>(); var navigation = Navigation(routes); var recent = new MemoryRecentStore();
        using var vm = new GlobalSearchViewModel(new FixedApi([
            Item("student", "Ada", "student-detail", new Dictionary<string, string> { ["id"] = Guid.Empty.ToString() }),
            Item("class", "5-A", "students", new Dictionary<string, string> { ["classId"] = Guid.Empty.ToString() })
        ]), navigation, recent);
        vm.Query = "Ada"; await vm.SearchNowAsync(); vm.MoveSelection(1); vm.ExecuteSelected();

        Assert.Equal("students/class/00000000-0000-0000-0000-000000000000", Assert.Single(routes));
        Assert.Single(recent.Load()); Assert.False(vm.IsOpen);
    }

    [Fact]
    public void EmptyPaletteShowsBoundedRecentAndRoutesDates()
    {
        var recent = new MemoryRecentStore();
        for (var index = 0; index < 12; index++) recent.Add(new($"q{index}", Item("module", $"M{index}", "reports")));
        using var vm = new GlobalSearchViewModel(new FixedApi([]), Navigation(), recent);
        vm.Open();
        Assert.Equal(8, vm.Results.Count);
        Assert.Equal("holiday-transfer/2026-09-11", GlobalSearchViewModel.BuildRoute(
            Item("date", "11 Eylül", "holiday-transfer", new Dictionary<string, string> { ["date"] = "2026-09-11" })));
    }

    private static SearchResultItem Item(string type, string title, string route, IReadOnlyDictionary<string, string>? parameters = null) =>
        new(type, title, "Alt", route, parameters ?? new Dictionary<string, string>(), "Icon");

    private static ShellNavigationService Navigation(List<string>? routes = null)
    {
        var navigation = new ShellNavigationService([ShellRoutes.Students, ShellRoutes.StudentDetail, ShellRoutes.HolidayTransfer, ShellRoutes.Reports]);
        if (routes is not null) navigation.NavigationRequested += (_, args) => routes.Add(args.Route);
        return navigation;
    }

    private sealed class FixedApi(IReadOnlyList<SearchResultItem> items) : IGlobalSearchApiClient
    {
        public Task<GlobalSearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GlobalSearchResponse(query, [new("result", "Sonuçlar", items)]));
    }

    private sealed class DelayedApi : IGlobalSearchApiClient
    {
        private int started;
        public int Canceled { get; private set; }
        public int Started => Volatile.Read(ref started);
        public async Task<GlobalSearchResponse> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            if (query == "Ada") Interlocked.Increment(ref started);
            try { await Task.Delay(query == "Ada" ? 500 : 10, cancellationToken); }
            catch (OperationCanceledException) { Canceled++; throw; }
            return new(query, [new("student", "Öğrenciler", [Item("student", query, "students")])]);
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < timeout) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class MemoryRecentStore : IRecentSearchStore
    {
        private readonly List<RecentSearchEntry> values = [];
        public IReadOnlyList<RecentSearchEntry> Load() => values.Take(8).ToArray();
        public void Add(RecentSearchEntry entry) { values.RemoveAll(value => value.Result.Title == entry.Result.Title); values.Insert(0, entry); }
    }
}
