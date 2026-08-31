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
        public int ApplyCount, CancelCount;
        public Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MealEntitlementPage([Row()], query.Page, query.PageSize, 75, new MealEntitlementSummary(8, 3, 5)));
        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MealTypeDetails>>([meal]);
        public Task<IReadOnlyList<ClassRecord>> ClassesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ClassRecord>>([new(Guid.NewGuid(), "5A", true)]);
        public Task<IReadOnlyList<GroupRecord>> GroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GroupRecord>>([new(Guid.NewGuid(), "Sporcular", "Manual", null, true, 1)]);
        public Task<EntitlementPreview> PreviewAsync(EntitlementGrantRequest request, CancellationToken cancellationToken = default)
        { LastPreview = request; return Task.FromResult(new EntitlementPreview(1, 1, 1, 1, 0, "TOKEN")); }
        public Task<BulkEntitlementResult> ApplyAsync(ApplyEntitlementGrantRequest request, CancellationToken cancellationToken = default)
        { ApplyCount++; return Task.FromResult(new BulkEntitlementResult(1, 1, 1, 0)); }
        public Task<CancelEntitlementsResult> CancelAsync(CancelEntitlementsRequest request, CancellationToken cancellationToken = default)
        { CancelCount++; return Task.FromResult(new CancelEntitlementsResult(request.ExpectedAffectedCount)); }
    }
}
