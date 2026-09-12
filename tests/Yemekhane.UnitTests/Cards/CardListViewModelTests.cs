using System.Net;
using Yemekhane.Application.Cards;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Cards;

/// <summary>
/// Kartlar ekrani: liste + suzgecten bagimsiz ozet, arama/durum suzgeci, iki adimli
/// pasiflestirme (neden zorunlu), geri acma, sunucu reddinin aynen gosterilmesi, yetki.
/// </summary>
public sealed class CardListViewModelTests
{
    private static CardListRow Row(string card, bool active, string no = "5001") =>
        new(Guid.NewGuid(), Guid.NewGuid(), no, "Ada Yılmaz", "5A", card, null, active, DateTimeOffset.UtcNow.AddDays(-10),
            active ? null : DateTimeOffset.UtcNow, active ? null : "Kayıp", StudentActive: true);

    [Fact]
    public async Task LoadsRowsAndFilterIndependentSummary()
    {
        var api = new FakeApi { Result = new CardListResult([Row("8350001", true), Row("8350002", false)], 1, 50, 2, 312, 14) };
        using var vm = new CardListViewModel(api, ["cards.manage"]);

        await vm.InitializeAsync();

        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("312 aktif, 14 pasif kart", vm.SummaryText);
        Assert.Contains("2 kart", vm.PageText);
        Assert.False(vm.IsEmpty);
        Assert.Equal("Aktif", vm.Rows[0].StatusText);
        Assert.Equal("Pasif", vm.Rows[1].StatusText);
        Assert.Equal("Kayıp", vm.Rows[1].ReasonText);
        Assert.EndsWith("devam ediyor", vm.Rows[0].PeriodText);
    }

    [Fact]
    public async Task SearchAndStatusFilterReachTheServer()
    {
        var api = new FakeApi();
        using var vm = new CardListViewModel(api, ["cards.manage"]);
        vm.Search = "ada";
        vm.SelectedStatus = vm.StatusOptions.Single(x => x.Name == "Pasif");

        await vm.SearchCommand.ExecuteAsync(null);

        Assert.Equal("ada", api.LastSearch);
        Assert.False(api.LastIsActive);
        Assert.Equal(1, api.LastPage);
    }

    /// <summary>Pasiflestirme iki adimdir; sunucu nedeni zorunlu tutar, bos nedenle istek gitmez.</summary>
    [Fact]
    public async Task DeactivationIsTwoStepAndRequiresAReason()
    {
        var api = new FakeApi { Result = new CardListResult([Row("8350001", true)], 1, 50, 1, 1, 0) };
        using var vm = new CardListViewModel(api, ["cards.manage"]);
        await vm.InitializeAsync();
        var row = vm.Rows[0];
        Assert.True(row.IsDeactivateIdle);

        vm.ArmDeactivateCommand.Execute(row);
        Assert.True(row.IsDeactivateArmed);
        Assert.False(row.CanConfirmDeactivate);

        vm.ConfirmDeactivateCommand.Execute(row);
        await Until(() => vm.Error is not null);
        Assert.Equal("Pasifleştirme nedeni zorunludur.", vm.Error);
        Assert.Equal(0, api.DeactivateCount);

        row.DeactivateReason = "Kayıp";
        Assert.True(row.CanConfirmDeactivate);
        vm.ConfirmDeactivateCommand.Execute(row);
        await Until(() => api.DeactivateCount == 1 && vm.StatusMessage is not null);
        Assert.Equal("Kayıp", api.LastReason);
        Assert.Equal(row.CardId, api.LastCardId);
        Assert.Contains("8350001", vm.StatusMessage);
        Assert.Null(vm.Error);
        Assert.Equal(2, api.ListCount);
    }

    [Fact]
    public async Task ReactivationCallsTheServerAndShowsItsRefusalVerbatim()
    {
        var api = new FakeApi { Result = new CardListResult([Row("8350002", false)], 1, 50, 1, 0, 1) };
        using var vm = new CardListViewModel(api, ["cards.manage"]);
        await vm.InitializeAsync();

        vm.ReactivateCommand.Execute(vm.Rows[0]);
        await Until(() => api.ReactivateCount == 1 && vm.StatusMessage is not null);
        Assert.Contains("8350002", vm.StatusMessage);

        api.ReactivateFailure = new ApiRequestException("Öğrencinin zaten aktif kartı var; önce onu pasifleştirin.", HttpStatusCode.Conflict);
        vm.ReactivateCommand.Execute(vm.Rows[0]);
        await Until(() => vm.Error is not null);
        Assert.Equal("Öğrencinin zaten aktif kartı var; önce onu pasifleştirin.", vm.Error);
    }

    [Fact]
    public async Task WithoutCardPermissionTheToggleCommandsAreDisabled()
    {
        var api = new FakeApi { Result = new CardListResult([Row("8350001", true), Row("8350002", false)], 1, 50, 2, 1, 1) };
        using var vm = new CardListViewModel(api, []);
        await vm.InitializeAsync();

        Assert.False(vm.CanManage);
        Assert.False(vm.ArmDeactivateCommand.CanExecute(vm.Rows[0]));
        Assert.False(vm.ReactivateCommand.CanExecute(vm.Rows[1]));
    }

    [Fact]
    public async Task LoginRequiredExplainsThePermission()
    {
        var api = new FakeApi { ListFailure = new LoginRequiredException() };
        using var vm = new CardListViewModel(api, ["cards.manage"]);

        await vm.InitializeAsync();

        Assert.Contains("cards.manage", vm.Error);
        // Hata varken "kayit yok" denmez: kullanici verisini kaybettigini sanmasin.
        Assert.False(vm.IsEmpty);
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Beklenen durum 2 saniye içinde oluşmadı.");
    }

    private sealed class FakeApi : ICardListApiClient
    {
        public CardListResult Result { get; set; } = new([], 1, 50, 0, 0, 0);
        public Exception? ListFailure { get; set; }
        public Exception? ReactivateFailure { get; set; }
        public int ListCount, DeactivateCount, ReactivateCount, LastPage;
        public string? LastSearch, LastReason;
        public bool? LastIsActive;
        public Guid LastCardId;

        public Task<CardListResult> ListAsync(string? search, bool? isActive, int page, int pageSize, CancellationToken cancellationToken = default)
        {
            ListCount++; LastSearch = search; LastIsActive = isActive; LastPage = page;
            return ListFailure is null ? Task.FromResult(Result) : Task.FromException<CardListResult>(ListFailure);
        }

        public Task DeactivateAsync(Guid cardId, string reason, CancellationToken cancellationToken = default)
        {
            DeactivateCount++; LastCardId = cardId; LastReason = reason;
            return Task.CompletedTask;
        }

        public Task<CardDetails> ReactivateAsync(Guid cardId, CancellationToken cancellationToken = default)
        {
            ReactivateCount++; LastCardId = cardId;
            var card = Result.Items.FirstOrDefault(x => x.CardId == cardId);
            return ReactivateFailure is null
                ? Task.FromResult(new CardDetails(cardId, Guid.NewGuid(), "5001", "Ada Yılmaz", card?.CardNumber ?? "?", DateTimeOffset.UtcNow, null, null, true))
                : Task.FromException<CardDetails>(ReactivateFailure);
        }
    }
}
