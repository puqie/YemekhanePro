using System.Net;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Cash;

/// <summary>
/// Canli arayuz denetiminde bulunan Kasa hatalarinin regresyon testleri. Her test, duzeltme geri
/// alindiginda DUSER (mutasyonla dogrulandi); sahte API yalnizca cagrilari kaydeder, veri uydurmaz.
/// </summary>
public sealed class CashViewModelRegressionTests
{
    /// <summary>"1250.50" tr-TR ile 125.050 okunuyordu: yuz kat fazla tahsilat kaydediliyordu.</summary>
    [Theory]
    [InlineData("1.250,50", 1250.50)]
    [InlineData("1250,50", 1250.50)]
    [InlineData("1250.50", 1250.50)]
    [InlineData("1,250.50", 1250.50)]
    [InlineData("1.250", 1250)]
    [InlineData("12.5", 12.5)]
    [InlineData("125", 125)]
    [InlineData("0,50", 0.50)]
    [InlineData(" ₺ 99,90 ", 99.90)]
    public void TutarTurkceVeNoktaliYazimlariAyniDegereCozer(string text, decimal expected)
    {
        Assert.True(CashViewModel.TryParseAmount(text, out var amount), text);
        Assert.Equal(expected, amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("12,345")]
    [InlineData("1.2505")]
    [InlineData("1.25.0")]
    [InlineData("1,2,3")]
    [InlineData("12,5,")]
    public void GecersizTutarReddedilir(string text) => Assert.False(CashViewModel.TryParseAmount(text, out _), text);

    [Fact]
    public async Task NoktaliOndalikTutarApiyeDogruGider()
    {
        var api = new FakeCashApi();
        var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
        await vm.InitializeAsync();
        vm.OpenAddCommand.Execute(null); vm.StudentNumber = "5016";
        await ((AsyncCommand)vm.LookupStudentCommand).ExecuteAsync(null);
        vm.AmountText = "1250.50"; vm.AddConfirmed = true;

        await ((AsyncCommand)vm.AddCommand).ExecuteAsync(null);

        Assert.Equal(1250.50m, api.LastAdd!.Amount);
    }

    /// <summary>Gunluk Kasa sekmesinde dune bakmak BUGUN kartini eziyordu.</summary>
    [Fact]
    public async Task GunlukKasaSekmesiBugunKartiniEzmez()
    {
        var api = new FakeCashApi();
        var vm = new CashViewModel(api, ["cash.read"], new FixedClock(new DateTimeOffset(2026, 9, 2, 9, 0, 0, TimeSpan.FromHours(3))));
        await vm.InitializeAsync();
        Assert.Equal(api.AmountFor(new DateOnly(2026, 9, 2)), vm.DailyTotal);
        Assert.Same(vm.Daily, vm.DailyReport);

        vm.DailyDate = new DateTime(2026, 9, 1);
        await ((AsyncCommand)vm.LoadDailyCommand).ExecuteAsync(null);

        Assert.Equal(api.AmountFor(new DateOnly(2026, 9, 1)), vm.DailyReport!.TotalAmount);
        Assert.Equal(api.AmountFor(new DateOnly(2026, 9, 2)), vm.DailyTotal);
        Assert.Equal(new DateOnly(2026, 9, 2), vm.Daily!.From);

        // Yenileme secili gunu korur: ekleme/iptal sonrasi sekme de tazelenir.
        await vm.RefreshAsync();
        Assert.Equal(new DateOnly(2026, 9, 1), vm.DailyReport!.From);
        Assert.Equal(new DateOnly(2026, 9, 2), vm.Daily!.From);
    }

    /// <summary>Filtre kutusu bos gorunuyordu; "Tümü" secenegi ve pasif turler filtrelenebilmeli.</summary>
    [Fact]
    public async Task GelirTuruFiltresiTumuIleBaslarVePasifTurleriIcerir()
    {
        var api = new FakeCashApi();
        var vm = new CashViewModel(api, ["cash.read", "cash.manage"]);
        await vm.InitializeAsync();

        Assert.Equal("Tümü", vm.SelectedFilterType!.Name);
        Assert.Null(vm.SelectedFilterType.Id);
        Assert.Equal(["Tümü", "Nakit", "Eski Tür (pasif)"], vm.FilterTypeOptions.Select(o => o.Name).ToArray());
        Assert.Single(vm.IncomeTypes); // ekleme kutusu yalnizca aktif

        vm.SelectedFilterType = vm.FilterTypeOptions[2];
        await ((AsyncCommand)vm.ApplyFiltersCommand).ExecuteAsync(null);
        Assert.Equal(api.InactiveTypeId, api.LastFilter!.IncomeTypeId);

        vm.SelectedFilterType = IncomeTypeOption.All;
        await ((AsyncCommand)vm.ApplyFiltersCommand).ExecuteAsync(null);
        Assert.Null(api.LastFilter!.IncomeTypeId);
    }

    /// <summary>Bos formda "Öğrenci doğrulanmadı" uyarisi yerine yonlendirme; dogrulaninca ayirt edici kimlik.</summary>
    [Fact]
    public async Task DogrulamaMetniBostaYonlendirirDogrulanincaKimlikGosterir()
    {
        var api = new FakeCashApi();
        var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
        await vm.InitializeAsync();
        vm.OpenAddCommand.Execute(null);

        Assert.False(vm.HasLookupStudent);
        Assert.DoesNotContain("doğrulanmadı", vm.LookupStudentText, StringComparison.OrdinalIgnoreCase);
        Assert.Null(vm.AddError);

        vm.StudentNumber = "5016";
        await ((AsyncCommand)vm.LookupStudentCommand).ExecuteAsync(null);
        Assert.True(vm.HasLookupStudent);
        Assert.Equal("ADA AKGÜN · No 5016 · 8B/B · Kart 8350016", vm.LookupStudentText);

        vm.FilterStudentNumber = "5016";
        await ((AsyncCommand)vm.LookupFilterStudentCommand).ExecuteAsync(null);
        Assert.Contains("No 5016", vm.FilterStudentText);
        Assert.Contains("8B/B", vm.FilterStudentText);
    }

    /// <summary>Sunucunun 409 mesaji (ayni adli tur) kullaniciya ulasmiyordu; AsyncCommand'a kadar kaciyordu.</summary>
    [Fact]
    public async Task AyniAdliGelirTuruSunucuMesajiylaReddedilir()
    {
        var api = new FakeCashApi { TypeConflict = true };
        var vm = new CashViewModel(api, ["cash.read", "cash.manage"]);
        await vm.InitializeAsync();
        Exception? escaped = null;
        AsyncCommand.UnhandledError += Capture;
        try
        {
            vm.NewTypeCommand.Execute(null); vm.TypeName = "Nakit";
            await ((AsyncCommand)vm.SaveTypeCommand).ExecuteAsync(null);
        }
        finally { AsyncCommand.UnhandledError -= Capture; }

        Assert.Null(escaped);
        Assert.Equal("Gelir türü adı zaten kayıtlı.", vm.ErrorMessage);
        void Capture(object? _, Exception ex) => escaped = ex;
    }

    [Fact]
    public async Task GelirEklemeSunucuMesajiniCekmecedeGosterir()
    {
        var api = new FakeCashApi { AddRejected = true };
        var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
        await vm.InitializeAsync();
        vm.OpenAddCommand.Execute(null); vm.StudentNumber = "5016";
        await ((AsyncCommand)vm.LookupStudentCommand).ExecuteAsync(null);
        vm.AmountText = "10"; vm.AddConfirmed = true;

        await ((AsyncCommand)vm.AddCommand).ExecuteAsync(null);

        Assert.Equal("Aktif gelir türü bulunamadı.", vm.AddError);
        Assert.True(vm.IsAddOpen);
    }

    /// <summary>Iptal sonrasi secim guncel (iptal edilmis) kayda tasinir; tekrar iptal acilamaz.</summary>
    [Fact]
    public async Task IptalSonrasiSecimGuncelKaydaTasinirVeTekrarIptalKapali()
    {
        var api = new FakeCashApi();
        var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
        await vm.InitializeAsync();
        vm.SelectedTransaction = vm.Transactions[0];
        vm.OpenVoidCommand.Execute(null);
        vm.VoidReason = "Hatalı"; vm.VoidConfirmed = true;

        await ((AsyncCommand)vm.VoidCommand).ExecuteAsync(null);

        Assert.NotNull(vm.SelectedTransaction);
        Assert.True(vm.SelectedTransaction!.IsVoided);
        Assert.False(vm.OpenVoidCommand.CanExecute(null));
        Assert.False(vm.IsVoidOpen);
    }

    /// <summary>Kaydet yalnizca "Secileni Duzenle" ile acilan turu gunceller; secili satir tek basina duzenleme degildir.</summary>
    [Fact]
    public async Task SeciliSatirDuzenleDenmedenKaydedilirseYeniTurOlusur()
    {
        var api = new FakeCashApi();
        var vm = new CashViewModel(api, ["cash.read", "cash.manage"]);
        await vm.InitializeAsync();

        vm.SelectedManagedType = vm.ManagedTypes[0]; vm.TypeName = "Havale";
        await ((AsyncCommand)vm.SaveTypeCommand).ExecuteAsync(null);
        Assert.Null(api.LastSavedTypeId);
        Assert.Equal("Yeni gelir türü", vm.TypeFormTitle);

        vm.SelectedManagedType = vm.ManagedTypes[0]; vm.EditTypeCommand.Execute(null);
        Assert.Equal("Gelir türünü düzenle", vm.TypeFormTitle);
        vm.TypeName = "Nakit Tahsilat";
        await ((AsyncCommand)vm.SaveTypeCommand).ExecuteAsync(null);
        Assert.Equal(vm.ManagedTypes[0].Id, api.LastSavedTypeId);
    }

    /// <summary>Basarili kayit sonrasi form temizlenir; cekmece yeniden acildiginda eski deger kalmaz.</summary>
    [Fact]
    public async Task KayitSonrasiFormTemizlenir()
    {
        var api = new FakeCashApi();
        var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
        await vm.InitializeAsync();
        vm.OpenAddCommand.Execute(null); vm.StudentNumber = "5016";
        await ((AsyncCommand)vm.LookupStudentCommand).ExecuteAsync(null);
        vm.AmountText = "10"; vm.Description = "x"; vm.AddConfirmed = true;

        await ((AsyncCommand)vm.AddCommand).ExecuteAsync(null);

        Assert.False(vm.IsAddOpen);
        Assert.Equal("", vm.AmountText); Assert.Null(vm.Description); Assert.Null(vm.LookupStudent); Assert.False(vm.AddConfirmed);
    }

    /// <summary>
    /// Gercek istemci: sunucunun 409/400/404 ProblemDetails basligi ApiRequestException olarak tasinir.
    /// EnsureSuccessStatusCode bu metni atiyordu; kullanici "Gelir türü adı zaten kayıtlı." yerine
    /// genel "kaydedilemedi" goruyordu.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Conflict, "Gelir türü adı zaten kayıtlı.")]
    [InlineData(HttpStatusCode.NotFound, "Aktif gelir işlemi bulunamadı.")]
    public async Task ApiIstemcisiSunucuMesajiniTasir(HttpStatusCode status, string title)
    {
        var handler = new ProblemHandler(status, title);
        var client = new CashApiClient(new System.Net.Http.HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, new Session());

        var ex = await Assert.ThrowsAsync<ApiRequestException>(() => client.SaveTypeAsync(null, new SaveIncomeTypeRequest("Nakit")));

        Assert.Equal(title, ex.Message);
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task ApiIstemcisiSunucuHatasiniCevrimdisiAkisinaBirakir()
    {
        var handler = new ProblemHandler(HttpStatusCode.InternalServerError, "Beklenmeyen hata");
        var client = new CashApiClient(new System.Net.Http.HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, new Session());

        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => client.TypesAsync(false));
    }

    private sealed class Session : IJwtSession { public string? AccessToken => "token"; public bool IsAuthenticated => true; }

    private sealed class ProblemHandler(HttpStatusCode status, string title) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(status)
            {
                Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(new { title, status = (int)status }), System.Text.Encoding.UTF8, "application/problem+json")
            });
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private sealed class FakeCashApi : ICashApiClient
    {
        private readonly Guid typeId = Guid.NewGuid();
        private readonly Guid studentId = Guid.NewGuid();
        private readonly Guid transactionId = Guid.NewGuid();
        private bool voided;
        public Guid InactiveTypeId { get; } = Guid.NewGuid();
        public bool TypeConflict { get; init; }
        public bool AddRejected { get; init; }
        public CreateIncomeTransactionRequest? LastAdd { get; private set; }
        public IncomeTransactionFilter? LastFilter { get; private set; }
        public Guid? LastSavedTypeId { get; private set; }

        public decimal AmountFor(DateOnly day) => day.Day * 100m;

        public Task<CashSummary> SummaryAsync(CashSummaryPeriod period, DateOnly? anchorDate = null, DateOnly? startDate = null, DateOnly? endDate = null, CancellationToken cancellationToken = default)
        {
            var day = anchorDate ?? startDate ?? new DateOnly(2026, 9, 2);
            return Task.FromResult(new CashSummary(period, day, endDate ?? day, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                AmountFor(day), 1, 0, 0, [new(typeId, "Nakit", AmountFor(day), 1)]));
        }
        public Task<PagedResult<IncomeTransactionDetails>> TransactionsAsync(IncomeTransactionFilter filter, CancellationToken cancellationToken = default)
        { LastFilter = filter; return Task.FromResult(new PagedResult<IncomeTransactionDetails>([Transaction()], filter.Page, filter.PageSize, 1)); }
        public Task<IReadOnlyList<IncomeTypeDetails>> TypesAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IncomeTypeDetails>>(includeInactive
                ? [new(typeId, "Nakit", true), new(InactiveTypeId, "Eski Tür", false)]
                : [new(typeId, "Nakit", true)]);
        public Task<IncomeTransactionDetails> AddAsync(CreateIncomeTransactionRequest request, CancellationToken cancellationToken = default)
        {
            LastAdd = request;
            if (AddRejected) throw new ApiRequestException("Aktif gelir türü bulunamadı.", HttpStatusCode.NotFound);
            return Task.FromResult(Transaction());
        }
        public Task<IncomeTransactionDetails> VoidAsync(Guid id, string reason, CancellationToken cancellationToken = default)
        { voided = true; return Task.FromResult(Transaction()); }
        public Task<IncomeTypeDetails> SaveTypeAsync(Guid? id, SaveIncomeTypeRequest request, CancellationToken cancellationToken = default)
        {
            LastSavedTypeId = id;
            if (TypeConflict) throw new ApiRequestException("Gelir türü adı zaten kayıtlı.", HttpStatusCode.Conflict);
            return Task.FromResult(new IncomeTypeDetails(id ?? Guid.NewGuid(), request.Name, request.IsActive));
        }
        public Task DeactivateTypeAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PagedResult<StudentListItem>> FindStudentAsync(string? studentNumber, string? cardNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<StudentListItem>([new(studentId, "5016", "8350016", "ADA", "AKGÜN", "8B", "B", null, null, true, 0, false, null)], 1, 2, 1));
        private IncomeTransactionDetails Transaction() => new(transactionId, Guid.NewGuid(), studentId, "ADA AKGÜN", "5016", "8350016", DateTimeOffset.UtcNow, typeId, "Nakit", 10m, null, Guid.NewGuid(), voided, null, null, voided ? "Hatalı" : null);
    }
}
