using System.Runtime.ExceptionServices;
using Yemekhane.Application.Cards;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Cards;

/// <summary>
/// Kartlar ekraninin XAML'i gercekten yuklenir ve ViewModel'e baglanir. Yalnizca ViewModel
/// testi hatali bir baglama yolunu ya da gecersiz XAML'i yakalamaz; o hata calisma
/// zamaninda, kullanicinin onunde cikardi.
/// </summary>
[Collection(Yemekhane.UnitTests.Desktop.UiCollection.Name)]
public sealed class CardListViewSmokeTests
{
    [Fact]
    public void ViewLoadsAndBindsToViewModelOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var viewModel = new CardListViewModel(new StubApi(), ["cards.manage"]);
                var view = new CardListView { DataContext = viewModel };
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

    private sealed class StubApi : ICardListApiClient
    {
        public Task<CardListResult> ListAsync(string? search, bool? isActive, int page, int pageSize, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CardListResult([], 1, pageSize, 0, 0, 0));
        public Task DeactivateAsync(Guid cardId, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CardDetails> ReactivateAsync(Guid cardId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CardDetails(cardId, Guid.NewGuid(), "1", "Test", "1", DateTimeOffset.UtcNow, null, null, true));
    }
}
