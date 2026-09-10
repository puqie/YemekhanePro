using Yemekhane.Application.BulkOperations;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Kullanicinin yazdigi GUN SAYISI sunucuya oldugu gibi ulasmalidir.
///
/// <para>
/// Eskiden masaustu gun sayisini bitis tarihine cevirip yalnizca tarihi gonderiyordu ve
/// bu cevrimi TATILLERI BILMEDEN yapiyordu. Sunucu ayni araligi tatil takvimiyle yeniden
/// eledigi icin "20 gun" 15 gune dusuyor, 6.000 TL'lik hakedis 4.500 TL olarak
/// yaziliyordu. Kullanicinin niyeti sozlesmede hic tasinmadigi icin sunucu ne
/// istendigini asla ogrenemiyordu.
/// </para>
/// <para>
/// Sunucu tarafi duzeltmesi (MealEntitlementService.CollectDaysAsync) tek basina
/// yetmez: masaustu DayCount gondermezse eski yol calismaya devam eder. Bu test o
/// baglantiyi -- gonderilen ISTEGIN kendisini -- olcer.
/// </para>
/// </summary>
[Collection("UI")]
public sealed class EntitlementDayCountTests
{
    [Fact]
    public void GirilenGunSayisiIstegeOlduguGibiKonur() =>
        UiThread.Run(() =>
        {
            var api = new CapturingApi();
            var vm = CreateViewModel(api);
            vm.GrantMeal = Meal();
            vm.ManualStudentIds = "5012";
            vm.DayCountText = "20";

            Preview(vm);

            Assert.True(api.LastGrant is not null, vm.PreviewMessage ?? "(mesaj yok)");
            Assert.Equal(20, api.LastGrant!.DayCount);
        });

    /// <summary>
    /// Bitis tarihi artik masaustunde hesaplanmaz. Gonderilen EndsOn, sunucunun
    /// gun sayisindan yeniden hesaplayacagi bir yer tutucudur; masaustunun tatil
    /// bilmeden urettigi bir tarih SUNUCUYA DAYATILMAMALIDIR.
    /// </summary>
    [Fact]
    public void BitisTarihiMasaustundeHesaplanipDayatilmaz() =>
        UiThread.Run(() =>
        {
            var api = new CapturingApi();
            var vm = CreateViewModel(api);
            vm.GrantMeal = Meal();
            vm.ManualStudentIds = "5012";
            vm.GrantStartsOn = new DateTime(2026, 9, 14);
            vm.DayCountText = "20";

            Preview(vm);

            Assert.True(api.LastGrant is not null, vm.PreviewMessage ?? "(mesaj yok)");
            // Masaustu 20 is gununu 09.10.2026 gibi bir tarihe cevirirdi; artik cevirmiyor.
            Assert.Equal(new DateOnly(2026, 9, 14), api.LastGrant!.EndsOn);
        });

    /// <summary>
    /// Aralik metni kesin bir bitis tarihi VAAT ETMEMELIDIR: tatiller sunucuda
    /// hesaba katilinca gercek bitis daha ileri bir tarihtir. Kesin tarih yazan
    /// eski metin, sunucunun bulduguyla tutmuyordu.
    /// </summary>
    [Fact]
    public void AralikMetniKesinBitisTarihiVaatEtmez() =>
        UiThread.Run(() =>
        {
            var vm = CreateViewModel(new CapturingApi());
            vm.GrantStartsOn = new DateTime(2026, 9, 14);
            vm.DayCountText = "20";

            Assert.Contains("20 gün", vm.GrantRangeText);
            Assert.Contains("uzar", vm.GrantRangeText);
        });

    private static MealEntitlementsViewModel CreateViewModel(CapturingApi api) =>
        new(api, ["entitlements.manage", "entitlements.bulk"]);

    private static MealTypeDetails Meal() => new(Guid.NewGuid(), "Öğle", null, null, true, 25m);

    /// <summary>Komut async void calisir; test gonderilen istegi gormek icin bekler.</summary>
    private static void Preview(MealEntitlementsViewModel vm) =>
        ((AsyncCommand)vm.PreviewCommand).ExecuteAsync(null).GetAwaiter().GetResult();

    private sealed class CapturingApi : IMealEntitlementApiClient
    {
        public EntitlementGrantRequest? LastGrant { get; private set; }

        public Task<EntitlementPreview> PreviewAsync(EntitlementGrantRequest request, CancellationToken ct = default)
        {
            LastGrant = request;
            return Task.FromResult(new EntitlementPreview(1, 20, 20, 20, 0, "token", 0, 0));
        }

        public Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken ct = default) =>
            Task.FromResult(new MealEntitlementPage([], 1, 50, 0, new MealEntitlementSummary(0, 0, 0)));
        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MealTypeDetails>>([new(Guid.NewGuid(), "Öğle", null, null, true, 25m)]);
        public Task<IReadOnlyList<ClassRecord>> ClassesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ClassRecord>>([]);
        public Task<IReadOnlyList<GroupRecord>> GroupsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GroupRecord>>([]);
        public Task<BulkEntitlementResult> ApplyAsync(ApplyEntitlementGrantRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CancelEntitlementsResult> CancelAsync(CancelEntitlementsRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CancelEntitlementsResult(request.EntitlementIds.Count));
    }
}
