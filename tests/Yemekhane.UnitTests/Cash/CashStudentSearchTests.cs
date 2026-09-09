using System.Net.Http;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Cash;

/// <summary>
/// Kasiyer ogrenci numarasi ezberleyemez: gelir eklerken ad/soyadla arayip listeden secebilmeli.
/// Ayrica her gelir bir ogrenciye ait degildir (kantin, bagis, personel yemegi); sunucu
/// StudentId'yi zaten istege bagli tutuyordu, ekran zorunlu kiliyordu.
/// </summary>
public sealed class CashStudentSearchTests
{
    private static CashViewModel ViewModel(FakeCashApi api) =>
        new(api, ["cash.read", "cash.write", "cash.manage"], TimeProvider.System);

    [Fact]
    public async Task SearchingByNameListsTheMatches()
    {
        var api = new FakeCashApi { Matches = [Student("1042", "Ayşe", "Yılmaz"), Student("2013", "Ayşe", "Yıldırım")] };
        var vm = ViewModel(api);
        vm.StudentSearch = "ayşe yıl";

        await ((AsyncCommand)vm.SearchStudentsCommand).ExecuteAsync(null);

        Assert.Equal("ayşe yıl", api.LastSearchTerm);
        Assert.Equal(2, vm.StudentMatches.Count);
        Assert.True(vm.HasStudentMatches);
        // Iki sonuc varsa kasiyer hangisi oldugunu kendisi secer.
        Assert.Null(vm.LookupStudent);
    }

    /// <summary>Tek sonuc varsa ayrica tiklatmaya gerek yok.</summary>
    [Fact]
    public async Task ASingleMatchIsSelectedAutomatically()
    {
        var api = new FakeCashApi { Matches = [Student("1042", "Ayşe", "Yılmaz")] };
        var vm = ViewModel(api);
        vm.StudentSearch = "1042";

        await ((AsyncCommand)vm.SearchStudentsCommand).ExecuteAsync(null);

        Assert.NotNull(vm.LookupStudent);
        Assert.Equal("1042", vm.LookupStudent!.StudentNo);
        Assert.Contains("Ayşe", vm.LookupStudentText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PickingAMatchCompletesTheVerification()
    {
        var api = new FakeCashApi { Matches = [Student("1042", "Ayşe", "Yılmaz"), Student("2013", "Ayşe", "Yıldırım")] };
        var vm = ViewModel(api);
        vm.StudentSearch = "ayşe";
        await ((AsyncCommand)vm.SearchStudentsCommand).ExecuteAsync(null);

        vm.SelectedMatch = vm.StudentMatches[1];

        Assert.Equal("2013", vm.LookupStudent!.StudentNo);
        Assert.Null(vm.ValidateAdd() is { } error && error.Contains("Öğrenci seçin", StringComparison.Ordinal) ? error : null);
    }

    [Fact]
    public async Task ShortTermIsRejectedWithoutCallingTheApi()
    {
        var api = new FakeCashApi();
        var vm = ViewModel(api);
        vm.StudentSearch = "a";

        await ((AsyncCommand)vm.SearchStudentsCommand).ExecuteAsync(null);

        Assert.Null(api.LastSearchTerm);
        Assert.Contains("en az 2 karakter", vm.AddError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoMatchTellsTheUserInsteadOfFailingSilently()
    {
        var api = new FakeCashApi { Matches = [] };
        var vm = ViewModel(api);
        vm.StudentSearch = "zzzz";

        await ((AsyncCommand)vm.SearchStudentsCommand).ExecuteAsync(null);

        Assert.False(vm.HasStudentMatches);
        Assert.Contains("eşleşen aktif öğrenci bulunamadı", vm.AddError!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiFailureIsReportedNotThrown()
    {
        var api = new FakeCashApi { FailSearch = true };
        var vm = ViewModel(api);
        vm.StudentSearch = "ayşe";

        await ((AsyncCommand)vm.SearchStudentsCommand).ExecuteAsync(null);

        Assert.Contains("Öğrenci aranamadı", vm.AddError!, StringComparison.Ordinal);
    }

    /// <summary>Her gelir ogrenciye ait degildir; kutu isaretliyken dogrulama istenmez.</summary>
    [Fact]
    public async Task GeneralIncomeNeedsNoStudent()
    {
        var api = new FakeCashApi();
        var vm = ViewModel(api);
        await vm.RefreshAsync();

        Assert.Contains("Öğrenci seçin", vm.ValidateAdd()!, StringComparison.Ordinal);

        vm.IsGeneralIncome = true;
        vm.AmountText = "250,00";
        vm.AddConfirmed = true;

        Assert.Null(vm.ValidateAdd());
        Assert.Contains("Öğrenciye bağlı olmayan gelir", vm.LookupStudentText, StringComparison.Ordinal);

        await ((AsyncCommand)vm.AddCommand).ExecuteAsync(null);

        Assert.NotNull(api.LastAdd);
        Assert.Null(api.LastAdd!.StudentId);
        Assert.Null(api.LastAdd.CardNumber);
        Assert.Equal(250m, api.LastAdd.Amount);
    }

    /// <summary>Kutu isaretlenince onceki secim temizlenmeli; yoksa "genel" gelir gizlice ogrenciye yazilir.</summary>
    [Fact]
    public async Task TickingGeneralIncomeClearsAPreviousSelection()
    {
        var api = new FakeCashApi { Matches = [Student("1042", "Ayşe", "Yılmaz")] };
        var vm = ViewModel(api);
        vm.StudentSearch = "1042";
        await ((AsyncCommand)vm.SearchStudentsCommand).ExecuteAsync(null);
        Assert.NotNull(vm.LookupStudent);

        vm.IsGeneralIncome = true;

        Assert.Null(vm.LookupStudent);
        Assert.Empty(vm.StudentMatches);
        Assert.Null(vm.StudentSearch);
    }

    /// <summary>Ogrenci secilmisken kutu isaretli degilse gelir o ogrenciye yazilir.</summary>
    [Fact]
    public async Task StudentIncomeStillCarriesTheStudentAndCard()
    {
        var api = new FakeCashApi { Matches = [Student("1042", "Ayşe", "Yılmaz", card: "8350042")] };
        var vm = ViewModel(api);
        await vm.RefreshAsync();
        vm.StudentSearch = "1042";
        await ((AsyncCommand)vm.SearchStudentsCommand).ExecuteAsync(null);
        var expected = vm.LookupStudent!.Id;
        vm.AmountText = "500";
        vm.AddConfirmed = true;

        await ((AsyncCommand)vm.AddCommand).ExecuteAsync(null);

        Assert.Equal(expected, api.LastAdd!.StudentId);
        Assert.Equal("8350042", api.LastAdd.CardNumber);
        // Kayit sonrasi form sifirlanir: secim, arama ve "genel gelir" kutusu temizlenmis olmali.
        Assert.Null(vm.LookupStudent);
        Assert.Empty(vm.StudentMatches);
        Assert.False(vm.IsGeneralIncome);
    }

    private static StudentListItem Student(string no, string first, string last, string? card = null) =>
        new(Guid.NewGuid(), no, card, first, last, "Anasınıfı A", "A", null, null, true, 0, false, null);

    private sealed class FakeCashApi : ICashApiClient
    {
        private readonly Guid typeId = Guid.NewGuid();
        public IReadOnlyList<StudentListItem> Matches { get; init; } = [];
        public bool FailSearch { get; init; }
        public string? LastSearchTerm { get; private set; }
        public CreateIncomeTransactionRequest? LastAdd { get; private set; }

        public Task<PagedResult<StudentListItem>> SearchStudentsAsync(string term, CancellationToken cancellationToken = default)
        {
            if (FailSearch) throw new HttpRequestException();
            LastSearchTerm = term;
            return Task.FromResult(new PagedResult<StudentListItem>(Matches, 1, 20, Matches.Count));
        }

        public Task<CashSummary> SummaryAsync(CashSummaryPeriod period, DateOnly? anchorDate = null, DateOnly? startDate = null,
            DateOnly? endDate = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CashSummary(period, new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 9),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0m, 0, 0m, 0, []));

        public Task<PagedResult<IncomeTransactionDetails>> TransactionsAsync(IncomeTransactionFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<IncomeTransactionDetails>([], filter.Page, filter.PageSize, 0));

        public Task<IReadOnlyList<IncomeTypeDetails>> TypesAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IncomeTypeDetails>>([new(typeId, "Nakit", true)]);

        public Task<IncomeTransactionDetails> AddAsync(CreateIncomeTransactionRequest request, CancellationToken cancellationToken = default)
        {
            LastAdd = request;
            return Task.FromResult(new IncomeTransactionDetails(Guid.NewGuid(), request.OperationId, request.StudentId, null, null,
                request.CardNumber, request.TransactionAt, request.IncomeTypeId, "Nakit", request.Amount, request.Description,
                Guid.NewGuid(), false, null, null, null));
        }

        public Task<IncomeTransactionDetails> VoidAsync(Guid id, string reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IncomeTypeDetails> SaveTypeAsync(Guid? id, SaveIncomeTypeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DeactivateTypeAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PagedResult<StudentListItem>> FindStudentAsync(string? studentNumber, string? cardNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<StudentListItem>(Matches, 1, 2, Matches.Count));
        public Task<BalanceTopUpResult> TopUpBalanceAsync(BalanceTopUpRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
