using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Notifications;
using Yemekhane.Application.Realtime;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

public sealed class NotificationCenterViewModel : ObservableObject, IDisposable
{
    private const int Capacity = 50;
    private readonly INotificationApiClient api;
    private readonly INotificationRealtimeClient realtime;
    private readonly IShellNavigationService navigation;
    private readonly HashSet<Guid> known = [];
    private bool isOpen;
    private bool isLoading;
    private bool isOffline;
    private string? error;
    private int unreadCount;

    public NotificationCenterViewModel(INotificationApiClient api, INotificationRealtimeClient realtime,
        IShellNavigationService navigation)
    {
        this.api = api; this.realtime = realtime; this.navigation = navigation;
        ToggleCommand = new AsyncCommand(ToggleAsync);
        MarkAllReadCommand = new AsyncCommand(MarkAllReadAsync, () => UnreadCount > 0);
        OpenCommand = new AsyncCommand<NotificationItem>(OpenAsync);
        realtime.NotificationReceived += OnRealtime;
        realtime.StateChanged += OnStateChanged;
    }

    public ObservableCollection<NotificationItem> Items { get; } = [];
    public bool IsOpen { get => isOpen; private set => Set(ref isOpen, value); }
    public bool IsLoading { get => isLoading; private set => Set(ref isLoading, value); }
    public bool IsOffline { get => isOffline; private set => Set(ref isOffline, value); }
    public string? Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error is not null;
    public bool IsEmpty => !IsLoading && Items.Count == 0;
    public int UnreadCount { get => unreadCount; private set { if (Set(ref unreadCount, value)) { Raise(nameof(HasUnread)); MarkAllReadCommand.Refresh(); } } }
    public bool HasUnread => UnreadCount > 0;
    public ICommand ToggleCommand { get; }
    public AsyncCommand MarkAllReadCommand { get; }
    public ICommand OpenCommand { get; }

    public async Task InitializeAsync() => await LoadAsync();

    private async Task ToggleAsync()
    {
        IsOpen = !IsOpen;
        if (IsOpen) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true; Error = null;
        try
        {
            var page = await api.ListAsync(Capacity);
            Items.Clear(); known.Clear();
            foreach (var item in page.Items) { Items.Add(item); known.Add(item.Id); }
            UnreadCount = page.UnreadCount;
            Raise(nameof(IsEmpty));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or LoginRequiredException)
        { Error = "Bildirimler alınamadı."; }
        finally { IsLoading = false; Raise(nameof(IsEmpty)); }
    }

    private async Task MarkAllReadAsync()
    {
        // Sunucu cagrisi ucustayken yeni bildirim gelebilir. Yalnizca cagriyi baslattigimiz anda
        // var olan kayitlar okundu sayilir; sayaci kosulsuz sifirlamak, arada gelen okunmamis
        // bildirimi rozette gorunmez yapar ve kullanici onu hic fark etmez.
        var markedIds = Items.Where(item => item.ReadAt is null).Select(item => item.Id).ToHashSet();
        await api.MarkAllReadAsync();
        var readAt = DateTimeOffset.UtcNow;
        for (var i = 0; i < Items.Count; i++)
            if (markedIds.Contains(Items[i].Id) && Items[i].ReadAt is null)
                Items[i] = Items[i] with { ReadAt = readAt };
        UnreadCount = Items.Count(item => item.ReadAt is null);
    }

    private async Task OpenAsync(NotificationItem item)
    {
        if (item.ReadAt is null) { await api.MarkReadAsync(item.Id); UnreadCount = Math.Max(0, UnreadCount - 1); }
        if (!string.IsNullOrWhiteSpace(item.RelatedRoute) && navigation.IsAvailable(item.RelatedRoute)) navigation.Navigate(item.RelatedRoute);
        IsOpen = false;
    }

    private void OnRealtime(object? sender, NotificationEvent value) => RunOnUi(() =>
    {
        var existing = Items.FirstOrDefault(x => x.Id == value.NotificationId);
        if (existing is not null) Items.Remove(existing);
        else { known.Add(value.NotificationId); UnreadCount++; }
        Items.Insert(0, new NotificationItem(value.NotificationId, value.Severity, value.Type, value.Title,
            value.Message, value.OccurredAt, value.RelatedEntityType, value.RelatedEntityId,
            value.RelatedRoute, value.RouteParametersJson, value.Count,
            value.LatestAt ?? value.OccurredAt, null, null));
        while (Items.Count > Capacity) { known.Remove(Items[^1].Id); Items.RemoveAt(Items.Count - 1); }
        Raise(nameof(IsEmpty));
    });

    private void OnStateChanged(object? sender, RealtimeConnectionState state) => RunOnUi(() => IsOffline = state != RealtimeConnectionState.Connected);
    private static void RunOnUi(Action action)
    { var dispatcher = System.Windows.Application.Current?.Dispatcher; if (dispatcher is null || dispatcher.CheckAccess()) action(); else dispatcher.Invoke(action); }
    public void Dispose() { realtime.NotificationReceived -= OnRealtime; realtime.StateChanged -= OnStateChanged; }
}
