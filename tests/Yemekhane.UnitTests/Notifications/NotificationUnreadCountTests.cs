using Yemekhane.Application.Notifications;
using Yemekhane.Application.Realtime;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Notifications;

/// <summary>
/// "Tumunu okundu isaretle" sunucuya gidip donerken yeni bir bildirim gelebilir.
/// Bu bildirim okunmamis olmasina ragmen rozetin sifirlanmasiyla gorunmez hale gelmemelidir.
/// </summary>
public sealed class NotificationUnreadCountTests
{
    [Fact]
    public async Task NotificationArrivingDuringMarkAllReadStaysUnread()
    {
        var existing = Item(Guid.NewGuid());
        var api = new GatedApi(existing);
        var realtime = new FakeRealtime();
        using var vm = new NotificationCenterViewModel(api, realtime, new ShellNavigationService([ShellRoutes.Devices]));
        await vm.InitializeAsync();
        Assert.Equal(1, vm.UnreadCount);

        // Sunucu cagrisi baslar ama tamamlanmaz.
        vm.MarkAllReadCommand.Execute(null);
        await api.Started.Task;

        // Cagri ucustayken yeni bir bildirim gelir.
        realtime.Push(Event(Guid.NewGuid()));

        api.Release.SetResult();
        // Komutun TAMAMI bitene kadar beklenir. Once yalnizca "okundu isaretlenmis bir kayit
        // var mi" bekleniyordu; bu kosul komut daha listeyi guncellemeden saglanabiliyor ve
        // yuklu bir toplu kosuda test rastgele dusuyordu (tek basina hep geciyordu).
        // Beklenen SON durum: iki kayit, biri okunmus.
        await WaitUntilAsync(() => vm.Items.Count == 2 && vm.Items.Any(item => item.ReadAt is not null));

        // Yeni bildirim okunmamis kalmalidir; aksi halde rozet 0 gosterirken listede okunmamis kayit durur.
        Assert.Equal(1, vm.UnreadCount);
        Assert.Equal(2, vm.Items.Count);
    }

    /// <summary>
    /// Kosul ucusta olan komutla AYNI ANDA calisir: dogrudan vm.Items uzerinde LINQ
    /// calistirmak, ViewModel listeyi guncellerken "Collection was modified" firlatir
    /// (tam kosuda rastgele dusme nedeni). Kosul once listenin kopyasini alir.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500; i++)
        {
            if (Safe(condition)) { Assert.True(Safe(condition)); return; }
            await Task.Delay(10);
        }
        Assert.True(Safe(condition));
    }

    private static bool Safe(Func<bool> condition)
    {
        try { return condition(); }
        catch (InvalidOperationException) { return false; }
    }

    private static NotificationItem Item(Guid id) => new(id, "Warning", "DeviceError", "Cihaz", "Hata",
        DateTimeOffset.UtcNow, "Device", "device-1", ShellRoutes.Devices, null, 1, DateTimeOffset.UtcNow, null, null);

    private static NotificationEvent Event(Guid id) => new(id, "Error", "DeviceError", "Cihaz", "Yeni hata",
        DateTimeOffset.UtcNow, "Device", "device-2", ShellRoutes.Devices, Count: 1);

    private sealed class GatedApi(NotificationItem item) : INotificationApiClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<NotificationPage> ListAsync(int pageSize = 30, string? cursor = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NotificationPage([item], null, 1));
        public Task MarkReadAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Release.Task;
        }
    }

    private sealed class FakeRealtime : INotificationRealtimeClient
    {
        public event EventHandler<NotificationEvent>? NotificationReceived;
#pragma warning disable CS0067 // Arayuz geregi; bu testte durum degisimi kullanilmiyor.
        public event EventHandler<RealtimeConnectionState>? StateChanged;
#pragma warning restore CS0067
        public void Push(NotificationEvent value) => NotificationReceived?.Invoke(this, value);
    }
}
