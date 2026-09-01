using System.Runtime.ExceptionServices;
using Yemekhane.Application.Devices;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// XAML'in gercekten yuklenebildigini ve ViewModel'e baglanabildigini dogrular.
/// Yalnizca ViewModel testi, hatali bir binding yolunu veya gecersiz XAML'i yakalamaz;
/// bu ekranda hata calisma zamaninda ortaya cikardi.
/// </summary>
[Collection(Yemekhane.UnitTests.Desktop.UiCollection.Name)]
public sealed class DeviceCardsViewSmokeTests
{
    [Fact]
    public void ViewLoadsAndBindsToViewModelOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewModel = new DeviceCardsViewModel(new StubApi());
                var view = new DeviceCardsView { DataContext = viewModel };
                view.Measure(new System.Windows.Size(1280, 720));
                Assert.Same(viewModel, view.DataContext);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class StubApi : IDeviceCardsApiClient
    {
        public Task<IReadOnlyList<DeviceCardSummary>> GetSummaryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceCardSummary>>([]);
        public Task<IReadOnlyList<PendingDeviceCard>> GetPendingAsync(Guid deviceId, int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PendingDeviceCard>>([]);
        public Task<IReadOnlyList<DeviceCardStatusRow>> GetCardStatusAsync(Guid cardId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeviceCardStatusRow>>([]);
        public Task ResyncCardAsync(Guid cardId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PushNowAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
