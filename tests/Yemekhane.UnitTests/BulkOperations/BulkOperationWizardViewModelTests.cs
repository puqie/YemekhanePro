using Yemekhane.Application.BulkOperations;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Meals;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.BulkOperations;

public sealed class BulkOperationWizardViewModelTests
{
    [Fact]
    public async Task SevenStepsValidateNavigatePreviewAndApply()
    {
        var api = new FakeApi(); var vm = new BulkOperationWizardViewModel(api, ["entitlements.bulk", "calendar.manage"]);
        await vm.InitializeAsync(); vm.OpenCommand.Execute(null);
        Assert.Equal(1, vm.Step);
        vm.NextCommand.Execute(null); await Until(() => vm.Step == 2);
        vm.NextCommand.Execute(null); await Until(() => vm.Step == 3);
        vm.NextCommand.Execute(null); await Until(() => vm.Step == 4);
        vm.NextCommand.Execute(null); await Until(() => vm.Step == 5);
        Assert.Contains("127", vm.ConfirmationText);
        vm.NextCommand.Execute(null); await Until(() => vm.Step == 6);
        vm.ApplyCommand.Execute(null); await Until(() => vm.Step == 7);
        Assert.Equal(1, api.ApplyCalls); Assert.NotNull(vm.ResultMessage);
    }

    [Fact]
    public async Task BackNavigationAndManualScopeValidationWork()
    {
        var vm = new BulkOperationWizardViewModel(new FakeApi(), ["entitlements.bulk", "calendar.manage"]); await vm.InitializeAsync(); vm.OpenCommand.Execute(null);
        vm.NextCommand.Execute(null); await Until(() => vm.Step == 2);
        vm.SelectedScope = vm.Scopes.Single(x => x.ScopeType == "Manual"); vm.NextCommand.Execute(null);
        await Until(() => vm.HasError); Assert.Equal(2, vm.Step);
        vm.ManualStudentIds = Guid.NewGuid().ToString(); vm.NextCommand.Execute(null); await Until(() => vm.Step == 3);
        vm.BackCommand.Execute(null); Assert.Equal(2, vm.Step);
    }

    [Fact]
    public void XamlContainsSevenStepModalProgressHistoryAndUndo()
    {
        var root = FindRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "Yemekhane.Desktop", "Views", "BulkOperationWizardView.xaml"));
        Assert.All(Enumerable.Range(1, 7), step => Assert.Contains($"{step}. ", xaml));
        Assert.Contains("Toplu İşlem Geçmişi", xaml); Assert.Contains("UndoCommand", xaml); Assert.Contains("IsBusy", xaml);
        Assert.Contains("OpenBulkCommand", File.ReadAllText(Path.Combine(root, "src", "Yemekhane.Desktop", "Views", "CalendarView.xaml")));
        Assert.Contains("OpenBulkCommand", File.ReadAllText(Path.Combine(root, "src", "Yemekhane.Desktop", "Views", "MealEntitlementsView.xaml")));
    }

    private static async Task Until(Func<bool> condition) { var end = DateTime.UtcNow.AddSeconds(3); while (!condition() && DateTime.UtcNow < end) await Task.Delay(10); Assert.True(condition()); }
    private static string FindRoot() { var path = AppContext.BaseDirectory; while (!File.Exists(Path.Combine(path, "Yemekhane.sln"))) path = Directory.GetParent(path)!.FullName; return path; }

    private sealed class FakeApi : IBulkOperationApiClient
    {
        public int ApplyCalls;
        public Task<IReadOnlyCollection<CalendarScopeOption>> ScopesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<CalendarScopeOption>>([new("AllSchool", null, "Tüm okul")]);
        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MealTypeDetails>>([]);
        public Task<BulkOperationPreview> PreviewAsync(BulkCalendarOperationRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new BulkOperationPreview(127, 127, 127, 127, 0, [], [], [], "token", DateTimeOffset.UtcNow.AddMinutes(5)));
        public Task<BulkOperationResult> ApplyAsync(ApplyBulkOperationRequest request, CancellationToken cancellationToken = default) { ApplyCalls++; return Task.FromResult(new BulkOperationResult(Guid.NewGuid(), "Completed", 127, 127, 127, 127, 0, [])); }
        public Task<BulkOperationHistoryPage> HistoryAsync(CancellationToken cancellationToken = default) => Task.FromResult(new BulkOperationHistoryPage([], 1, 30, 0));
        public Task<UndoBulkOperationResult> UndoAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(new UndoBulkOperationResult(id, true, "Geri alındı"));
    }
}
