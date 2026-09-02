using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using Yemekhane.Application.Search;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

public sealed record SearchDisplayItem(string GroupTitle, SearchResultItem Result)
{
    public string Title => Result.Title;
    public string Subtitle => Result.Subtitle;
    /// <summary>
    /// Rozetteki kisa etiket. API'nin Icon alani ("Person", "Calendar") ham Ingilizce
    /// simge adidir ve 28 px'lik dairede kesilerek gorunuyordu; tur bazli Turkce kisaltma yazilir.
    /// </summary>
    public string Icon => Result.Type switch
    {
        "student" => "ÖĞR",
        "class" => "SNF",
        "group" => "GRP",
        "date" => "GÜN",
        "event" => "TTL",
        "module" => "MOD",
        _ => "•"
    };
}

public sealed class GlobalSearchViewModel : ObservableObject, IDisposable
{
    private readonly IGlobalSearchApiClient api;
    private readonly IShellNavigationService navigation;
    private readonly IRecentSearchStore recentStore;
    private CancellationTokenSource? pending;
    private string query = "", statusText = "Aramak için yazın";
    private bool isOpen, isLoading, isOffline;
    private int selectedIndex = -1;

    public GlobalSearchViewModel(IGlobalSearchApiClient api, IShellNavigationService navigation,
        IRecentSearchStore? recentStore = null)
    {
        this.api = api; this.navigation = navigation; this.recentStore = recentStore ?? new FileRecentSearchStore();
    }

    public ObservableCollection<SearchDisplayItem> Results { get; } = [];
    public string Query { get => query; set { if (Set(ref query, value)) { Replace([]); QueueSearch(); } } }
    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }
    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public int SelectedIndex { get => selectedIndex; set => Set(ref selectedIndex, value); }
    public bool HasResults => Results.Count > 0;

    public void Open()
    {
        IsOpen = true;
        if (string.IsNullOrWhiteSpace(Query)) ShowRecent();
    }

    public void Close()
    {
        pending?.Cancel(); IsOpen = false;
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        SelectedIndex = SelectedIndex < 0 ? (delta > 0 ? 0 : Results.Count - 1)
            : (SelectedIndex + delta + Results.Count) % Results.Count;
    }

    public void ExecuteSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return;
        var selected = Results[SelectedIndex].Result;
        var route = BuildRoute(selected);
        if (!navigation.IsAvailable(route)) return;
        recentStore.Add(new RecentSearchEntry(Query.Trim(), selected));
        navigation.Navigate(route);
        Close();
    }

    public async Task ExecuteOrSearchAsync()
    {
        if (SelectedIndex < 0 && !string.IsNullOrWhiteSpace(Query))
        {
            pending?.Cancel();
            await SearchNowAsync();
        }
        ExecuteSelected();
    }

    public async Task SearchNowAsync(CancellationToken cancellationToken = default)
    {
        var value = Query.Trim();
        if (value.Length == 0) { ShowRecent(); return; }
        IsLoading = true; IsOffline = false; StatusText = "Aranıyor...";
        try
        {
            var response = await api.SearchAsync(value, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Replace(response.Groups.SelectMany(group => group.Items.Select(item => new SearchDisplayItem(group.Title, item))));
            StatusText = Results.Count == 0 ? (value.Length < 2 ? "Genel arama için en az 2 karakter yazın." : "Sonuç bulunamadı.") : $"{Results.Count} sonuç";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (LoginRequiredException) { Replace([]); StatusText = "Arama için oturum gerekli."; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        { Replace([]); IsOffline = true; StatusText = "Arama servisine ulaşılamıyor."; }
        finally { if (!cancellationToken.IsCancellationRequested) IsLoading = false; }
    }

    private void QueueSearch()
    {
        pending?.Cancel(); pending?.Dispose(); pending = new CancellationTokenSource();
        var token = pending.Token;
        _ = DebouncedSearchAsync(token);
    }

    private async Task DebouncedSearchAsync(CancellationToken token)
    {
        try { await Task.Delay(250, token); await SearchNowAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ShowRecent()
    {
        Replace(recentStore.Load().Select(entry => new SearchDisplayItem("Son aramalar", entry.Result)));
        StatusText = Results.Count == 0 ? "Öğrenci, kart, sınıf, tarih veya modül arayın." : "Son aramalar";
        IsOffline = false; IsLoading = false;
    }

    private void Replace(IEnumerable<SearchDisplayItem> values)
    {
        Results.Clear(); foreach (var value in values) Results.Add(value);
        SelectedIndex = Results.Count == 0 ? -1 : 0; Raise(nameof(HasResults));
    }

    public static string BuildRoute(SearchResultItem item)
    {
        if (item.Route == ShellRoutes.StudentDetail && item.RouteParameters.TryGetValue("id", out var id))
            return $"{ShellRoutes.StudentDetail}/{id}";
        if (item.Route == ShellRoutes.Students && item.RouteParameters.TryGetValue("classId", out var classId))
            return $"{ShellRoutes.Students}/class/{classId}";
        if (item.Route == ShellRoutes.Students && item.RouteParameters.TryGetValue("groupId", out var groupId))
            return $"{ShellRoutes.Students}/group/{groupId}";
        if (item.Route == ShellRoutes.HolidayTransfer && item.RouteParameters.TryGetValue("date", out var date))
            return $"{ShellRoutes.HolidayTransfer}/{date}";
        return item.Route;
    }

    public void Dispose() { pending?.Cancel(); pending?.Dispose(); GC.SuppressFinalize(this); }
}
