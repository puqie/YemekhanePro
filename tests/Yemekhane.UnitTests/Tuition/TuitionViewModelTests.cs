using System.Net.Http;
using Yemekhane.Application.Common;
using Yemekhane.Application.Organization;
using Yemekhane.Application.Statements;
using Yemekhane.Application.Tuition;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Domain.Entities;

namespace Yemekhane.UnitTests.Tuition;

/// <summary>
/// Ucret plani ekrani: memur anasinifi sinifina toplam/taksit ya da aylik ucret tanimlar.
/// Ekran dogrulamasi sunucununkiyle ayni olmali; yoksa kullanici hatayi ancak kaydedince gorur.
/// </summary>
public sealed class TuitionViewModelTests
{
    private static readonly Guid PreschoolId = Guid.NewGuid();

    private static TuitionViewModel ViewModel(FakeApi api, bool manage = true) =>
        new(api, manage ? ["cash.read", "cash.manage"] : ["cash.read"]);

    private static TuitionViewModel Ready(FakeApi api, bool manage = true)
    {
        var vm = ViewModel(api, manage);
        vm.SetClasses(
        [
            new LookupRecord(PreschoolId, "Anasınıfı A", 12, ClassKinds.Preschool),
            new LookupRecord(Guid.NewGuid(), "5/A", 30, ClassKinds.Normal)
        ]);
        return vm;
    }

    /// <summary>Ucret plani anasinifi icin; normal sinif listeye girmemeli.</summary>
    [Fact]
    public void OnlyPreschoolClassesAreOffered()
    {
        var vm = Ready(new FakeApi());

        Assert.Single(vm.Classes);
        Assert.Equal("Anasınıfı A", vm.Classes[0].Name);
        Assert.Equal(PreschoolId, vm.SelectedClass!.Id);
    }

    [Fact]
    public void InstallmentFieldsHideForTheDailyRate()
    {
        var vm = Ready(new FakeApi());

        Assert.True(vm.ShowInstallmentFields);
        Assert.True(vm.ShowDownPayment);
        Assert.Equal("Toplam ücret (₺)", vm.AmountLabel);

        vm.SelectedKind = TuitionViewModel.Kinds.Single(x => x.Key == TuitionPlanKinds.Monthly);
        Assert.True(vm.ShowInstallmentFields);
        Assert.False(vm.ShowDownPayment);
        Assert.Equal("Aylık ücret (₺)", vm.AmountLabel);

        vm.SelectedKind = TuitionViewModel.Kinds.Single(x => x.Key == TuitionPlanKinds.Daily);
        Assert.False(vm.ShowInstallmentFields);
        Assert.Equal("Günlük ücret (₺)", vm.AmountLabel);
    }

    [Fact]
    public void ValidRequestCarriesTheFormValues()
    {
        var vm = Ready(new FakeApi());
        vm.AmountText = "48.000,00";
        vm.DownPaymentText = "8.000,00";
        vm.InstallmentCountText = "10";
        vm.DueDayText = "5";
        vm.Note = "Anasınıfı yıllık ücreti";

        var request = vm.BuildRequest(out var error);

        Assert.Null(error);
        Assert.Equal(48_000m, request!.Amount);
        Assert.Equal(8_000m, request.DownPayment);
        Assert.Equal(10, request.InstallmentCount);
        Assert.Equal(5, request.DueDayOfMonth);
        Assert.Equal(PreschoolId, request.ClassId);
        Assert.Null(request.StudentId);
        Assert.Equal("Anasınıfı yıllık ücreti", request.Note);
    }

    [Theory]
    [InlineData("", "Tutar sıfırdan")]
    [InlineData("0", "Tutar sıfırdan")]
    [InlineData("10,555", "Tutar sıfırdan")]
    public void AmountIsValidatedOnScreen(string amount, string expected)
    {
        var vm = Ready(new FakeApi());
        vm.AmountText = amount;

        Assert.Null(vm.BuildRequest(out var error));
        Assert.StartsWith(expected, error!, StringComparison.Ordinal);
    }

    /// <summary>29-31 her ayda yok; ekran da sunucu gibi 28'de sinirlar.</summary>
    [Theory]
    [InlineData("29")]
    [InlineData("31")]
    [InlineData("0")]
    public void DueDayIsBounded(string dueDay)
    {
        var vm = Ready(new FakeApi());
        vm.AmountText = "48.000,00";
        vm.DueDayText = dueDay;

        Assert.Null(vm.BuildRequest(out var error));
        Assert.StartsWith("Ayın günü", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void DownPaymentCannotSwallowTheWholeFee()
    {
        var vm = Ready(new FakeApi());
        vm.AmountText = "10.000,00";
        vm.DownPaymentText = "10.000,00";

        Assert.Null(vm.BuildRequest(out var error));
        Assert.Equal("Peşinat toplam ücretten küçük olmalıdır.", error);
    }

    [Fact]
    public void DailyPlanSendsNoInstallmentFields()
    {
        var vm = Ready(new FakeApi());
        vm.SelectedKind = TuitionViewModel.Kinds.Single(x => x.Key == TuitionPlanKinds.Daily);
        vm.AmountText = "250,00";
        vm.InstallmentCountText = "10";

        var request = vm.BuildRequest(out var error);

        Assert.Null(error);
        Assert.Equal(0, request!.InstallmentCount);
        Assert.Equal(0m, request.DownPayment);
    }

    [Fact]
    public async Task SavingReportsHowManyInstallmentsWereCreated()
    {
        var api = new FakeApi();
        var vm = Ready(api);
        vm.AmountText = "48.000,00";

        await ((AsyncCommand)vm.SaveCommand).ExecuteAsync(null);

        Assert.NotNull(api.LastSaved);
        Assert.Equal(48_000m, api.LastSaved!.Amount);
        Assert.Contains("2 taksit oluşturuldu", vm.StatusMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerErrorIsShownVerbatim()
    {
        var api = new FakeApi { SaveError = "Peşinat toplam ücretten küçük olmalıdır." };
        var vm = Ready(api);
        vm.AmountText = "48.000,00";

        await ((AsyncCommand)vm.SaveCommand).ExecuteAsync(null);

        Assert.Equal("Peşinat toplam ücretten küçük olmalıdır.", vm.ErrorMessage);
    }

    [Fact]
    public async Task SelectingAPlanFillsTheFormAndInstallments()
    {
        var api = new FakeApi();
        var vm = Ready(api);
        await vm.LoadAsync();

        vm.SelectedPlan = vm.Plans[0];

        Assert.Equal("48.000,00", vm.AmountText);
        Assert.Equal("10", vm.InstallmentCountText);
        Assert.True(vm.HasInstallments);
        Assert.Equal("1. taksit", vm.Installments[0].Sequence);
        Assert.Contains("Kalan", vm.PlanSummaryText, StringComparison.Ordinal);
        Assert.Contains("Gecikmiş", vm.PlanSummaryText, StringComparison.Ordinal);
        Assert.True(vm.Installments[1].IsOverdue);
    }

    /// <summary>Silme iki adimlidir; ilk basista istek gitmez.</summary>
    [Fact]
    public async Task DeletingAsksForConfirmationFirst()
    {
        var api = new FakeApi();
        var vm = Ready(api);
        await vm.LoadAsync();
        vm.SelectedPlan = vm.Plans[0];

        await ((AsyncCommand)vm.DeleteCommand).ExecuteAsync(null);

        Assert.True(vm.DeleteArmed);
        Assert.Equal("Silmeyi Onayla", vm.DeleteButtonText);
        Assert.Equal(0, api.DeleteCalls);

        await ((AsyncCommand)vm.DeleteCommand).ExecuteAsync(null);

        Assert.Equal(1, api.DeleteCalls);
        Assert.Contains("pasife alınır", vm.StatusMessage!, StringComparison.Ordinal);
    }

    /// <summary>Baska plan secilince onay sifirlanir; yanlis plan silinmesin.</summary>
    [Fact]
    public async Task ChangingSelectionDisarmsTheDelete()
    {
        var api = new FakeApi { Plans = [Plan("2026-2027"), Plan("2025-2026")] };
        var vm = Ready(api);
        await vm.LoadAsync();
        vm.SelectedPlan = vm.Plans[0];
        await ((AsyncCommand)vm.DeleteCommand).ExecuteAsync(null);
        Assert.True(vm.DeleteArmed);

        vm.SelectedPlan = vm.Plans[1];

        Assert.False(vm.DeleteArmed);
    }

    [Fact]
    public void WithoutManagePermissionSavingIsBlocked()
    {
        var vm = Ready(new FakeApi(), manage: false);

        Assert.False(vm.CanManage);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.NewPlanCommand.CanExecute(null));
    }

    private static TuitionPlanDetails Plan(string period = "2026-2027") => new(
        Guid.NewGuid(), TuitionScopes.Class, PreschoolId, "Anasınıfı A", null, null, null,
        TuitionPlanKinds.Installment, "Toplam ücret + taksit", period, 48_000m, 0m, 10, 5,
        new DateOnly(2026, 10, 1), null, true, 48_000m, 4_800m, 43_200m, 4_800m,
        [
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, new DateOnly(2026, 10, 5), 4_800m, 4_800m, 0m,
                TuitionInstallmentStatuses.Paid, "Ödendi", false, null),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, new DateOnly(2026, 11, 5), 4_800m, 0m, 4_800m,
                TuitionInstallmentStatuses.Overdue, "Gecikmiş", false, null)
        ]);

    /// <summary>
    /// Ucret plani tutari NOKTA-ONDALIK yazimda 100 kat sismemelidir.
    ///
    /// <para>
    /// <c>decimal.TryParse(text, NumberStyles.Number, tr-TR, ...)</c> -- <c>AllowThousands</c>
    /// acik ve tr-TR'de grup ayiraci NOKTA. "6000.00" 600000 olarak okunuyordu; yanindaki
    /// <c>decimal.Round(value, 2) == value</c> kontrolu hicbir sey yakalamiyordu, cunku sonuc
    /// zaten tam sayi. <c>MaxAmount = 1.000.000</c> siniri da 600.000'i geciriyordu.
    /// </para>
    /// <para>
    /// Sonuc: sinif planina 6.000 TL yazan kullanici SINIFTAKI HER OGRENCIYE 600.000 TL borc
    /// yaziyordu. Ayni hata daha once Kasa'da bulunup duzeltilmisti
    /// (CashViewModelRegressionTests: "1250.50 tr-TR ile 125.050 okunuyordu: yuz kat fazla
    /// tahsilat kaydediliyordu") ama cozum yalnizca o ekrana uygulanmisti.
    /// </para>
    /// <para>
    /// QA'de kacmasinin sebebi: doldur-kaydet-ac-kaydet dongusu DOGRU calisiyor, cunku ekran
    /// tutari "N2" ile "48.000,00" yaziyor ve tr-TR bunu dogru okuyor. Yalnizca ELLE
    /// nokta-ondalik yazan kullanici patlatiyor.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("6000.00", 6_000)]      // Ingilizce klavye aliskanligi -- ONCE 600.000 oluyordu
    [InlineData("4800.50", 4_800.50)]   // sinirin altinda kaldigi icin SESSIZCE kabul ediliyordu
    [InlineData("1250.50", 1_250.50)]   // Kasa'daki regresyonun birebir esi
    [InlineData("250.75", 250.75)]
    [InlineData("48.000,00", 48_000)]   // tr-TR dogru yazim korunmali
    [InlineData("48000,50", 48_000.50)]
    [InlineData("48.000", 48_000)]      // tam ucluk grup: binlik ayiraci
    [InlineData("6000", 6_000)]
    public void NoktaOndalikTutarYuzKatSismez(string text, decimal expected)
    {
        var vm = Ready(new FakeApi());
        vm.SelectedClass = vm.Classes[0];
        vm.AmountText = text;
        vm.InstallmentCountText = "10";
        vm.DueDayText = "5";

        var request = vm.BuildRequest(out var error);

        Assert.Null(error);
        Assert.NotNull(request);
        Assert.Equal(expected, request!.Amount);
    }

    /// <summary>Pesinat ayni ayristiriciyi kullanir; orada da sismemeli.</summary>
    [Fact]
    public void PesinatNoktaOndalikYazimdaSismez()
    {
        var vm = Ready(new FakeApi());
        vm.SelectedClass = vm.Classes[0];
        vm.AmountText = "10000";
        vm.DownPaymentText = "1500.50";
        vm.InstallmentCountText = "10";
        vm.DueDayText = "5";

        var request = vm.BuildRequest(out var error);

        Assert.Null(error);
        Assert.Equal(1_500.50m, request!.DownPayment);
    }

    /// <summary>Gecersiz yazim REDDEDILMELI; sessizce baska bir sayiya donusmemeli.</summary>
    [Theory]
    [InlineData("6000.123")]   // ikiden fazla ondalik
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-500")]
    [InlineData("")]
    public void GecersizTutarReddedilir(string text)
    {
        var vm = Ready(new FakeApi());
        vm.SelectedClass = vm.Classes[0];
        vm.AmountText = text;
        vm.InstallmentCountText = "10";
        vm.DueDayText = "5";

        var request = vm.BuildRequest(out var error);

        Assert.NotNull(error);
        Assert.Null(request);
    }

    private sealed class FakeApi : ITuitionApiClient
    {
        public IReadOnlyList<TuitionPlanDetails> Plans { get; init; } = [Plan()];
        public string? SaveError { get; init; }
        public SaveTuitionPlanRequest? LastSaved { get; private set; }
        public int DeleteCalls { get; private set; }

        public Task<PagedResult<TuitionPlanDetails>> PlansAsync(TuitionPlanFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<TuitionPlanDetails>(Plans, 1, 200, Plans.Count));

        public Task<TuitionPlanDetails> SavePlanAsync(SaveTuitionPlanRequest request, CancellationToken cancellationToken = default)
        {
            if (SaveError is not null) throw new ApiRequestException(SaveError, System.Net.HttpStatusCode.BadRequest);
            LastSaved = request;
            return Task.FromResult(Plans[0]);
        }

        public Task DeletePlanAsync(Guid id, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }

        public Task<StudentTuitionSummary> ForStudentAsync(Guid studentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<TuitionInstallmentDetails> ApplyPaymentAsync(ApplyTuitionPaymentRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<StudentStatement> StatementAsync(Guid studentId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DownloadStatementPdfAsync(Guid studentId, DateOnly startDate, DateOnly endDate, string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
