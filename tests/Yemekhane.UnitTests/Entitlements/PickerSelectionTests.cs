using Yemekhane.Application.Common;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Entitlements;

/// <summary>
/// "Ela'ya hak verirken Ceylin'in tiki hala duruyordu, Ceylin'e ikinci kez yukleme yapildi."
///
/// Yeni arama secimi SIFIRLAR. Onceden secim sessizce tasiniyordu: kullanici aradigi isme
/// bakip hak veriyor, gormedigi ogrenciye de hak yaziliyordu. Yanlis ogrenciye yukleme
/// yapmak ikinci bir arama yapmaktan cok daha pahalidir.
///
/// Cogul secim gerekiyorsa (once 5/A sonra 5/B) "Seçimi koru" isaretlenir; o zaman tasinan
/// satirlar EKRANDA ADIYLA uyari serifinde gorunur.
/// </summary>
public sealed class PickerSelectionTests
{
    private static MealEntitlementsViewModel ViewModel(FakeApi api) =>
        new(api, ["entitlements.manage", "entitlements.bulk"]);

    private static async Task<MealEntitlementsViewModel> SearchAndSelectAsync(FakeApi api, string term, string pick)
    {
        var vm = ViewModel(api);
        await vm.InitializeAsync();
        await SearchAsync(vm, term);
        vm.StudentPicker.Single(x => x.Name.Contains(pick, StringComparison.Ordinal)).IsSelected = true;
        return vm;
    }

    private static async Task SearchAsync(MealEntitlementsViewModel vm, string term)
    {
        vm.StudentPickerSearch = term;
        await ((AsyncCommand)vm.SearchStudentsCommand).ExecuteAsync(null);
    }

    /// <summary>Kok senaryo: Ceylin secilip Ela arandiginda Ceylin ARTIK SECILI DEGILDIR.</summary>
    [Fact]
    public async Task ANewSearchClearsThePreviousSelection()
    {
        var api = new FakeApi();
        var vm = await SearchAndSelectAsync(api, "ceylin", "Ceylin");

        await SearchAsync(vm, "ela");

        Assert.DoesNotContain(vm.StudentPicker, x => x.Name.Contains("Ceylin", StringComparison.Ordinal));
        Assert.DoesNotContain(vm.StudentPicker, x => x.IsSelected);
    }

    /// <summary>Yeni aramadan sonra hak YALNIZCA yeni secilen ogrenciye gider.</summary>
    [Fact]
    public async Task OnlyTheNewlySelectedStudentIsGranted()
    {
        var api = new FakeApi();
        var vm = await SearchAndSelectAsync(api, "ceylin", "Ceylin");
        await SearchAsync(vm, "ela");
        vm.StudentPicker.Single(x => x.Name.Contains("Ela", StringComparison.Ordinal)).IsSelected = true;

        vm.TargetType = "Manual";
        vm.GrantMeal = (await api.MealTypesAsync())[0];
        await ((AsyncCommand)vm.PreviewCommand).ExecuteAsync(null);

        var target = api.LastGrant!.Target;
        var ids = (target.StudentIds ?? []).ToArray();
        var nos = (target.StudentNos ?? []).ToArray();
        Assert.DoesNotContain(FakeApi.CeylinNo, nos);
        Assert.DoesNotContain(FakeApi.CeylinId, ids);
        Assert.True(nos.Contains(FakeApi.ElaNo) || ids.Contains(FakeApi.ElaId), "Ela hedefte yok.");
    }

    /// <summary>"Seçimi koru" isaretliyse eski davranis gecerlidir; cogul sinif secimi bozulmaz.</summary>
    [Fact]
    public async Task KeepingTheSelectionPreservesItAcrossSearches()
    {
        var api = new FakeApi();
        var vm = await SearchAndSelectAsync(api, "ceylin", "Ceylin");
        vm.KeepSelectionAcrossSearches = true;

        await SearchAsync(vm, "ela");

        Assert.Contains(vm.StudentPicker, x => x.Name.Contains("Ceylin", StringComparison.Ordinal) && x.IsSelected);
    }

    /// <summary>Tasinan secim GORUNUR bir uyari uretir; sessiz kalmasi bu hataya yol acmisti.</summary>
    [Fact]
    public async Task ACarriedOverSelectionRaisesAVisibleWarningNamingTheStudent()
    {
        var api = new FakeApi();
        var vm = await SearchAndSelectAsync(api, "ceylin", "Ceylin");
        vm.KeepSelectionAcrossSearches = true;

        await SearchAsync(vm, "ela");

        Assert.True(vm.HasHiddenSelection);
        Assert.Equal(1, vm.HiddenSelectedCount);
        Assert.Contains("Ceylin", vm.HiddenSelectionWarning, StringComparison.Ordinal);
    }

    /// <summary>Secim ozeti ADI yazar: "2 öğrenci" demek kimin secili oldugunu gizliyordu.</summary>
    [Fact]
    public async Task TheSummaryNamesTheSelectedStudents()
    {
        var api = new FakeApi();
        var vm = await SearchAndSelectAsync(api, "ceylin", "Ceylin");

        Assert.Contains("Ceylin", vm.PickerSummary, StringComparison.Ordinal);
    }

    /// <summary>Ayni aramada secilen satirlar uyari uretmez; uyari yalnizca GORUNMEYEN secim icindir.</summary>
    [Fact]
    public async Task SelectionsFromTheCurrentSearchDoNotWarn()
    {
        var api = new FakeApi();
        var vm = await SearchAndSelectAsync(api, "ceylin", "Ceylin");

        Assert.False(vm.HasHiddenSelection);
        Assert.Equal("", vm.HiddenSelectionWarning);
    }

    private sealed class FakeApi : IMealEntitlementApiClient
    {
        public static readonly Guid CeylinId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid ElaId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public const string CeylinNo = "5001";
        public const string ElaNo = "5002";

        private readonly MealTypeDetails meal = new(Guid.NewGuid(), "Öğle", null, null, true);
        public EntitlementGrantRequest? LastGrant;

        private static StudentListItem Student(Guid id, string no, string first, string last) =>
            new(id, no, null, first, last, "5A", "A", null, null, true, 0, false, null);

        public Task<PagedResult<StudentListItem>> SearchStudentsAsync(string term, CancellationToken cancellationToken = default)
        {
            var all = new[]
            {
                Student(CeylinId, CeylinNo, "Ceylin Mihra", "Yılmaz"),
                Student(ElaId, ElaNo, "Ela", "Demir"),
            };
            var hits = all.Where(x => x.FirstName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
            return Task.FromResult(new PagedResult<StudentListItem>(hits, 1, 50, hits.Count));
        }

        public Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MealEntitlementPage([], query.Page, query.PageSize, 0, new MealEntitlementSummary(0, 0, 0)));
        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MealTypeDetails>>([meal]);
        public Task<IReadOnlyList<ClassRecord>> ClassesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClassRecord>>([new(Guid.NewGuid(), "5A", true)]);
        public Task<IReadOnlyList<GroupRecord>> GroupsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GroupRecord>>([]);
        public Task<EntitlementPreview> PreviewAsync(EntitlementGrantRequest request, CancellationToken cancellationToken = default)
        {
            LastGrant = request;
            return Task.FromResult(new EntitlementPreview(1, 1, 1, 1, 0, "TOKEN"));
        }
        public Task<BulkEntitlementResult> ApplyAsync(ApplyEntitlementGrantRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BulkEntitlementResult(1, 1, 1, 0));
        public Task<CancelEntitlementsResult> CancelAsync(CancelEntitlementsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
