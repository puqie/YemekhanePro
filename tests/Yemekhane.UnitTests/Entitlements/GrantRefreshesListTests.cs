using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Entitlements;

/// <summary>
/// "Hak verdim ama listede gorunmedi, olmadi sandim ve tekrar verdim."
///
/// Uygulama sonrasi liste HER DURUMDA tazelenir ve ARAMA KUTUSU temizlenir. Onceden liste
/// yalnizca entitlements.manage yetkisi olanlarda yenileniyordu; ayrica kutuda kalan eski
/// arama metni yeni satiri suzuyordu. Ikisi de kullaniciya "islem gecmedi" hissi veriyordu.
/// </summary>
public sealed class GrantRefreshesListTests
{
    private static MealEntitlementsViewModel ViewModel(FakeApi api, params string[] permissions) =>
        new(api, permissions.Length > 0 ? permissions : ["entitlements.manage", "entitlements.bulk"]);

    private static async Task GrantAsync(MealEntitlementsViewModel vm, FakeApi api)
    {
        await vm.InitializeAsync();
        vm.TargetType = "Manual";
        vm.ManualStudentIds = "1001";
        vm.GrantMeal = (await api.MealTypesAsync())[0];
        await ((AsyncCommand)vm.PreviewCommand).ExecuteAsync(null);
        await ((AsyncCommand)vm.ApplyCommand).ExecuteAsync(null);
    }

    [Fact]
    public async Task TheListIsReloadedAfterGranting()
    {
        var api = new FakeApi();
        var vm = ViewModel(api);

        await GrantAsync(vm, api);

        // Ilk yukleme + uygulama sonrasi yeniden yukleme.
        Assert.True(api.SearchCount >= 2, $"Liste tazelenmedi (arama sayısı {api.SearchCount}).");
        Assert.Equal(1, api.ApplyCount);
    }

    /// <summary>
    /// Yonetim yetkisi OLMAYAN, yalnizca toplu hakedis yetkisi olan kullanicida da liste
    /// tazelenmeli; once bu durumda hic yenilenmiyor ve ekran bos kaliyordu.
    /// </summary>
    [Fact]
    public async Task TheListIsReloadedEvenWithoutTheManagePermission()
    {
        var api = new FakeApi();
        var vm = ViewModel(api, "entitlements.bulk");

        await GrantAsync(vm, api);

        Assert.Equal(1, api.ApplyCount);
        Assert.True(api.SearchCount >= 1, "Yönetim yetkisi olmayan kullanıcıda liste hiç yüklenmedi.");
    }

    /// <summary>Kutuda kalan eski arama metni yeni satiri suzmemeli.</summary>
    [Fact]
    public async Task TheSearchBoxIsClearedSoTheNewRowIsVisible()
    {
        var api = new FakeApi();
        var vm = ViewModel(api);
        await vm.InitializeAsync();
        vm.SearchText = "başka öğrenci";
        vm.TargetType = "Manual";
        vm.ManualStudentIds = "1001";
        vm.GrantMeal = (await api.MealTypesAsync())[0];
        await ((AsyncCommand)vm.PreviewCommand).ExecuteAsync(null);

        await ((AsyncCommand)vm.ApplyCommand).ExecuteAsync(null);

        Assert.Null(vm.SearchText);
        Assert.Null(api.LastQuery!.Search);
    }

    /// <summary>Sonuc mesaji tazelemeden SONRA yazilir; yoksa kullanici hicbir onay gormez.</summary>
    [Fact]
    public async Task TheResultMessageSurvivesTheReload()
    {
        var api = new FakeApi();
        var vm = ViewModel(api);

        await GrantAsync(vm, api);

        Assert.NotNull(vm.StatusMessage);
        Assert.Contains("hak oluşturuldu", vm.StatusMessage!, StringComparison.Ordinal);
    }

    /// <summary>Verilen aralik gorunur olsun diye filtre araligi GENISLETILIR.</summary>
    [Fact]
    public async Task TheDateRangeWidensToCoverTheGrant()
    {
        var api = new FakeApi();
        var vm = ViewModel(api);
        await vm.InitializeAsync();
        vm.StartsOn = DateTime.Today;
        vm.EndsOn = DateTime.Today;
        vm.TargetType = "Manual";
        vm.ManualStudentIds = "1001";
        vm.GrantMeal = (await api.MealTypesAsync())[0];
        vm.GrantStartsOn = DateTime.Today.AddDays(20);
        vm.DayCountText = "1";
        await ((AsyncCommand)vm.PreviewCommand).ExecuteAsync(null);

        await ((AsyncCommand)vm.ApplyCommand).ExecuteAsync(null);

        Assert.True(vm.EndsOn >= DateTime.Today.AddDays(20), "Verilen tarih filtre aralığının dışında kaldı.");
    }

    private sealed class FakeApi : IMealEntitlementApiClient
    {
        private readonly MealTypeDetails meal = new(Guid.NewGuid(), "Öğle", null, null, true);
        public MealEntitlementQuery? LastQuery;
        public int ApplyCount, SearchCount;

        public Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken cancellationToken = default)
        {
            SearchCount++; LastQuery = query;
            return Task.FromResult(new MealEntitlementPage([], query.Page, query.PageSize, 0, new MealEntitlementSummary(0, 0, 0)));
        }

        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MealTypeDetails>>([meal]);
        public Task<IReadOnlyList<ClassRecord>> ClassesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClassRecord>>([new(Guid.NewGuid(), "5A", true)]);
        public Task<IReadOnlyList<GroupRecord>> GroupsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GroupRecord>>([]);
        public Task<EntitlementPreview> PreviewAsync(EntitlementGrantRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EntitlementPreview(1, 1, 1, 1, 0, "TOKEN"));
        public Task<BulkEntitlementResult> ApplyAsync(ApplyEntitlementGrantRequest request, CancellationToken cancellationToken = default)
        {
            ApplyCount++;
            return Task.FromResult(new BulkEntitlementResult(1, 1, 1, 0));
        }
        public Task<CancelEntitlementsResult> CancelAsync(CancelEntitlementsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
