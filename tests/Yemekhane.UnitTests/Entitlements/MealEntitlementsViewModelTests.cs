using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Entitlements;

public sealed class MealEntitlementsViewModelTests
{
    [Fact]
    public async Task LoadMapsServerPaginationSummaryAndEmptyState()
    {
        var api = new FakeApi(); var vm = Create(api);
        await vm.InitializeAsync();

        Assert.Equal(75, vm.TotalCount);
        Assert.Equal(8, vm.TotalQuantity);
        Assert.Equal(3, vm.ConsumedQuantity);
        Assert.Equal(5, vm.RemainingQuantity);
        Assert.False(vm.IsEmpty);
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task StudentRoutePreselectsTargetAndPreviewAppliesRealRequest()
    {
        var api = new FakeApi(); var vm = Create(api); await vm.InitializeAsync(); var studentId = Guid.NewGuid();
        vm.HandleRoute($"{ShellRoutes.Entitlements}/{studentId:D}");
        Assert.True(vm.IsGrantOpen); Assert.Contains(studentId.ToString("D"), vm.ManualStudentIds);

        vm.PreviewCommand.Execute(null); await Until(() => vm.HasPreview);
        Assert.Equal(studentId, api.LastPreview!.Target.StudentIds!.Single());
        vm.ApplyCommand.Execute(null); await Until(() => api.ApplyCount == 1);
        Assert.False(vm.IsGrantOpen);
    }

    [Fact]
    public async Task CancellationRequiresUnusedSelectionAndExplicitConfirmation()
    {
        var api = new FakeApi(); var vm = Create(api); await vm.InitializeAsync();
        var consumed = Row() with { ConsumedQuantity = 1, RemainingQuantity = 0 };
        vm.SetSelection([consumed]); vm.RequestCancelCommand.Execute(null);
        Assert.False(vm.IsCancelConfirmationOpen); Assert.NotNull(vm.ErrorMessage);

        vm.SetSelection([Row()]); vm.RequestCancelCommand.Execute(null);
        Assert.True(vm.IsCancelConfirmationOpen); Assert.Contains("1", vm.CancelConfirmationText);
        vm.ConfirmCancelCommand.Execute(null); await Until(() => api.CancelCount == 1);
        Assert.False(vm.IsCancelConfirmationOpen);
    }

    /// <summary>
    /// Harf girilen adet sunucuya gitmeden reddedilir. int baglamada "abc" WPF tarafinda
    /// sessizce yutulup ESKI adetle onizleme aliniyordu.
    /// </summary>
    [Fact]
    public async Task HarfliAdetSunucuyaGitmedenReddedilir()
    {
        var api = new FakeApi(); var vm = Create(api); await vm.InitializeAsync();
        vm.OpenGrantCommand.Execute(null); vm.ManualStudentIds = "5012";
        vm.QuantityText = "abc"; vm.PreviewCommand.Execute(null); await Until(() => vm.PreviewMessage is not null);
        Assert.Contains("1-10", vm.PreviewMessage); Assert.Null(api.LastPreview); Assert.False(vm.HasPreview);
        vm.QuantityText = "11"; vm.PreviewCommand.Execute(null); await Until(() => vm.PreviewMessage is not null);
        Assert.Contains("1-10", vm.PreviewMessage); Assert.Null(api.LastPreview);
        Assert.Equal(11, vm.Quantity);
    }

    /// <summary>Kullanici GUID bilmez: manuel hedefte okul numarasi StudentNos olarak sunucuya gider, kimlikler StudentIds olarak.</summary>
    [Fact]
    public async Task OgrenciNumarasiManuelHedefeTasinir()
    {
        var api = new FakeApi(); var vm = Create(api); await vm.InitializeAsync(); var id = Guid.NewGuid();
        vm.OpenGrantCommand.Execute(null);
        vm.ManualStudentIds = $"5012, 5013;{id:D}\n5012";
        vm.PreviewCommand.Execute(null); await Until(() => vm.HasPreview);
        Assert.Equal(["5012", "5013"], api.LastPreview!.Target.StudentNos!);
        Assert.Equal([id], api.LastPreview.Target.StudentIds!);

        vm.ManualStudentIds = "   "; vm.PreviewCommand.Execute(null); await Until(() => vm.PreviewMessage is not null);
        Assert.Contains("numara", vm.PreviewMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Uygulama sonrasi: cekmece kapanir ama sonuc metni listede kalir ve filtre araligi
    /// verilen gunleri kapsayacak sekilde genisler -- yoksa kullanici yeni satirlari goremez.
    /// </summary>
    [Fact]
    public async Task UygulamaSonrasiDurumMetniGorunurVeFiltreAraligiGenisler()
    {
        var api = new FakeApi(); var vm = Create(api); await vm.InitializeAsync();
        vm.OpenGrantCommand.Execute(null); vm.ManualStudentIds = "5012";
        vm.GrantStartsOn = DateTime.Today.AddDays(20); vm.GrantEndsOn = DateTime.Today.AddDays(24);
        vm.PreviewCommand.Execute(null); await Until(() => vm.HasPreview);
        vm.ApplyCommand.Execute(null); await Until(() => api.ApplyCount == 1 && vm.HasStatus);
        Assert.False(vm.IsGrantOpen);
        Assert.Contains("hak oluşturuldu", vm.StatusMessage);
        Assert.True(vm.EndsOn >= DateTime.Today.AddDays(24));
        Assert.True(vm.StartsOn <= DateTime.Today.AddDays(-7));
    }

    /// <summary>Sunucunun Turkce dogrulama basligi (ApiRequestException) oldugu gibi kullaniciya ulasir; "cevrimdisi" sayilmaz.</summary>
    [Fact]
    public async Task SunucuDogrulamaMesajiKullaniciyaUlasir()
    {
        var api = new FakeApi { PreviewError = new ApiRequestException("Aktif öğrenci bulunamadı: 5999", System.Net.HttpStatusCode.BadRequest) };
        var vm = Create(api); await vm.InitializeAsync();
        vm.OpenGrantCommand.Execute(null); vm.ManualStudentIds = "5999";
        vm.PreviewCommand.Execute(null); await Until(() => vm.PreviewMessage is not null);
        Assert.Equal("Aktif öğrenci bulunamadı: 5999", vm.PreviewMessage); Assert.False(vm.IsOffline);
    }

    /// <summary>Grup ve ogun filtre kutulari "Tümü" ile baslar; secim sorguya kimlik olarak gider, "Tümü" geri secilince kalkar.</summary>
    [Fact]
    public async Task OgunVeGrupFiltreleriTumuIleBaslar()
    {
        var api = new FakeApi(); var vm = Create(api); await vm.InitializeAsync();
        Assert.Equal("Tümü", vm.MealFilters[0].Name); Assert.Same(vm.MealFilters[0], vm.SelectedMealFilter); Assert.Null(vm.SelectedMeal);
        Assert.Equal("Tümü", vm.GroupFilters[0].Name); Assert.Same(vm.GroupFilters[0], vm.SelectedGroupFilter);
        Assert.Equal(2, vm.MealFilters.Count); Assert.Equal(2, vm.GroupFilters.Count);
        Assert.Null(api.LastQuery!.MealTypeId);

        vm.SelectedMealFilter = vm.MealFilters[1]; vm.SelectedGroupFilter = vm.GroupFilters[1];
        Assert.Equal(vm.MealTypes[0], vm.SelectedMeal); Assert.Equal(vm.Groups[0], vm.SelectedGroup);
        await vm.LoadAsync(1);
        Assert.Equal(vm.MealTypes[0].Id, api.LastQuery!.MealTypeId); Assert.Equal(vm.Groups[0].Id, api.LastQuery.GroupId);

        vm.SelectedMeal = null; vm.SelectedGroup = null;
        Assert.Same(vm.MealFilters[0], vm.SelectedMealFilter); Assert.Same(vm.GroupFilters[0], vm.SelectedGroupFilter);
        await vm.LoadAsync(1);
        Assert.Null(api.LastQuery!.MealTypeId); Assert.Null(api.LastQuery.GroupId);
    }

    [Fact]
    public void RbacControlsReadAndBulkActions()
    {
        var read = new MealEntitlementsViewModel(new FakeApi(), ["entitlements.manage"]);
        Assert.True(read.CanManage); Assert.False(read.CanBulk);
        var bulk = new MealEntitlementsViewModel(new FakeApi(), ["entitlements.bulk"]);
        Assert.False(bulk.CanManage); Assert.True(bulk.CanBulk);
    }

    private static MealEntitlementsViewModel Create(FakeApi api) => new(api, ["entitlements.manage", "entitlements.bulk"]);
    private static MealEntitlementListItem Row() => new(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 9, 1),
        "42", "CARD42", "Öğle", "Ada Yılmaz", "5A", 1, 0, 1, "Active", "Manual", 0);
    private static async Task Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < timeout) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeApi : IMealEntitlementApiClient
    {
        private readonly MealTypeDetails meal = new(Guid.NewGuid(), "Öğle", null, null, true);
        public EntitlementGrantRequest? LastPreview;
        public MealEntitlementQuery? LastQuery;
        public Exception? PreviewError;
        public int ApplyCount, CancelCount, SearchCount;
        public Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken cancellationToken = default)
        {
            SearchCount++; LastQuery = query;
            return Task.FromResult(new MealEntitlementPage([Row()], query.Page, query.PageSize, 75, new MealEntitlementSummary(8, 3, 5)));
        }
        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MealTypeDetails>>([meal]);
        public Task<IReadOnlyList<ClassRecord>> ClassesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ClassRecord>>([new(Guid.NewGuid(), "5A", true)]);
        public Task<IReadOnlyList<GroupRecord>> GroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GroupRecord>>([new(Guid.NewGuid(), "Sporcular", "Manual", null, true, 1)]);
        public Task<EntitlementPreview> PreviewAsync(EntitlementGrantRequest request, CancellationToken cancellationToken = default)
        {
            if (PreviewError is not null) throw PreviewError;
            LastPreview = request; return Task.FromResult(new EntitlementPreview(1, 1, 1, 1, 0, "TOKEN"));
        }
        public Task<BulkEntitlementResult> ApplyAsync(ApplyEntitlementGrantRequest request, CancellationToken cancellationToken = default)
        { ApplyCount++; return Task.FromResult(new BulkEntitlementResult(1, 1, 1, 0)); }
        public Task<CancelEntitlementsResult> CancelAsync(CancelEntitlementsRequest request, CancellationToken cancellationToken = default)
        { CancelCount++; return Task.FromResult(new CancelEntitlementsResult(request.ExpectedAffectedCount)); }
    }
}
