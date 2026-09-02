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

    /// <summary>Onay metni islem turunu ve davranisi ham kodla ("CancelEntitlements", "Delete") degil Turkce yazar.</summary>
    [Fact]
    public async Task OnayMetniHamKodDegilTurkceYazar()
    {
        var api = new FakeApi(); var vm = new BulkOperationWizardViewModel(api, ["entitlements.bulk", "calendar.manage"]);
        await vm.InitializeAsync(); vm.OpenCommand.Execute(null);
        for (var i = 0; i < 4; i++) { var step = vm.Step; vm.NextCommand.Execute(null); await Until(() => vm.Step == step + 1); }
        Assert.Contains("Hak iptali", vm.ConfirmationText); Assert.Contains("Hakları iptal et", vm.ConfirmationText);
        Assert.DoesNotContain("CancelEntitlements", vm.ConfirmationText); Assert.DoesNotContain("Delete", vm.ConfirmationText);
    }

    /// <summary>
    /// Islem UYGULANDIKTAN sonra gecmis listesi yuklenemezse sonuc korunur (adim 7).
    /// Onceden adim 4'e donuluyor, kullanici islemi "basarisiz" sanip tekrar uyguluyordu.
    /// </summary>
    [Fact]
    public async Task GecmisYenilenemeseDeUygulamaSonucuKorunur()
    {
        var api = new FakeApi(); var vm = new BulkOperationWizardViewModel(api, ["entitlements.bulk", "calendar.manage"]);
        await vm.InitializeAsync(); vm.OpenCommand.Execute(null);
        for (var i = 0; i < 5; i++) { var step = vm.Step; vm.NextCommand.Execute(null); await Until(() => vm.Step == step + 1); }
        api.HistoryFails = true; var changed = 0; vm.Changed += (_, _) => changed++;
        vm.ApplyCommand.Execute(null); await Until(() => vm.Step == 7 && !vm.IsBusy);
        Assert.Equal(1, api.ApplyCalls); Assert.NotNull(vm.ResultMessage);
        Assert.Contains("geçmiş", vm.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, changed);
    }

    /// <summary>Uygulama ve geri alma, barindiran ekranin listeyi yenilemesi icin Changed olayini tetikler.</summary>
    [Fact]
    public async Task UygulamaVeGeriAlmaChangedOlayiniTetikler()
    {
        var api = new FakeApi { HistoryItems = [new(Guid.NewGuid(), "CancelEntitlements", "Completed", DateTimeOffset.Now, Guid.NewGuid(), 1, 1, 1, true, null)] };
        var vm = new BulkOperationWizardViewModel(api, ["entitlements.bulk", "calendar.manage"]);
        await vm.InitializeAsync(); var changed = 0; vm.Changed += (_, _) => changed++;
        vm.OpenCommand.Execute(null);
        for (var i = 0; i < 5; i++) { var step = vm.Step; vm.NextCommand.Execute(null); await Until(() => vm.Step == step + 1); }
        vm.ApplyCommand.Execute(null); await Until(() => vm.Step == 7 && !vm.IsBusy);
        Assert.Equal(1, changed);
        vm.UndoCommand.Execute(vm.History[0]); await Until(() => api.UndoCalls == 1 && !vm.IsBusy);
        Assert.Equal(2, changed); Assert.Equal("Geri alındı", vm.ResultMessage);
    }

    /// <summary>Manuel kapsamda okul numaralari StudentNos olarak, kimlikler StudentIds olarak sunucuya gider.</summary>
    [Fact]
    public async Task ManuelKapsamdaNumaralarStudentNosOlarakGider()
    {
        var api = new FakeApi(); var vm = new BulkOperationWizardViewModel(api, ["entitlements.bulk", "calendar.manage"]);
        await vm.InitializeAsync(); vm.OpenCommand.Execute(null); var id = Guid.NewGuid();
        vm.NextCommand.Execute(null); await Until(() => vm.Step == 2);
        vm.SelectedScope = vm.Scopes.Single(x => x.ScopeType == "Manual"); vm.ManualStudentIds = $"5012, 5013 {id:D}";
        vm.NextCommand.Execute(null); await Until(() => vm.Step == 3);
        vm.NextCommand.Execute(null); await Until(() => vm.Step == 4);
        vm.NextCommand.Execute(null); await Until(() => vm.Step == 5);
        Assert.Equal(["5012", "5013"], api.LastPreview!.Scope.StudentNos!);
        Assert.Equal([id], api.LastPreview.Scope.StudentIds!);
    }

    /// <summary>Ogun kutusu "Tümü" ile baslar; "Tümü" secimi sorguya null, ogun secimi kimlik olarak gider.</summary>
    [Fact]
    public async Task OgunKutusuTumuIleBaslar()
    {
        var api = new FakeApi(); var vm = new BulkOperationWizardViewModel(api, ["entitlements.bulk", "calendar.manage"]); await vm.InitializeAsync();
        Assert.Equal("Tümü", vm.MealFilters[0].Name); Assert.Same(vm.MealFilters[0], vm.SelectedMealFilter); Assert.Null(vm.SelectedMealType);
        Assert.Equal(2, vm.MealFilters.Count);
        vm.OpenCommand.Execute(null);
        for (var i = 0; i < 4; i++) { var step = vm.Step; vm.NextCommand.Execute(null); await Until(() => vm.Step == step + 1); }
        Assert.Null(api.LastPreview!.MealTypeId);
        vm.BackCommand.Execute(null); vm.SelectedMealFilter = vm.MealFilters[1];
        Assert.Equal(vm.MealTypes[0], vm.SelectedMealType);
        vm.NextCommand.Execute(null); await Until(() => vm.Step == 5);
        Assert.Equal(vm.MealTypes[0].Id, api.LastPreview!.MealTypeId);
    }

    [Fact]
    public async Task PresetTarihVeDavranisiAyarlar()
    {
        var vm = new BulkOperationWizardViewModel(new FakeApi(), ["entitlements.bulk", "calendar.manage"]); await vm.InitializeAsync();
        vm.Preset(new DateOnly(2026, 9, 16), transferBehavior: "NextBusinessDay");
        Assert.Equal(new DateTime(2026, 9, 16), vm.StartsOn); Assert.Equal(new DateTime(2026, 9, 16), vm.EndsOn);
        Assert.Equal("NextBusinessDay", vm.TransferBehavior);
        vm.Preset(transferBehavior: "Bilinmeyen"); Assert.Equal("NextBusinessDay", vm.TransferBehavior);
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
        public int ApplyCalls, UndoCalls;
        public bool HistoryFails;
        public BulkCalendarOperationRequest? LastPreview;
        public List<BulkOperationHistoryItem> HistoryItems = [];
        public Task<IReadOnlyCollection<CalendarScopeOption>> ScopesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyCollection<CalendarScopeOption>>([new("AllSchool", null, "Tüm okul")]);
        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MealTypeDetails>>([new(Guid.NewGuid(), "Öğle", null, null, true)]);
        public Task<BulkOperationPreview> PreviewAsync(BulkCalendarOperationRequest request, CancellationToken cancellationToken = default)
        { LastPreview = request; return Task.FromResult(new BulkOperationPreview(127, 127, 127, 127, 0, [], [], [], "token", DateTimeOffset.UtcNow.AddMinutes(5))); }
        public Task<BulkOperationResult> ApplyAsync(ApplyBulkOperationRequest request, CancellationToken cancellationToken = default) { ApplyCalls++; return Task.FromResult(new BulkOperationResult(Guid.NewGuid(), "Completed", 127, 127, 127, 127, 0, [])); }
        public Task<BulkOperationHistoryPage> HistoryAsync(CancellationToken cancellationToken = default)
        {
            if (HistoryFails) throw new ApiRequestException("İstek işlenirken beklenmeyen bir hata oluştu.", System.Net.HttpStatusCode.InternalServerError);
            return Task.FromResult(new BulkOperationHistoryPage(HistoryItems, 1, 30, HistoryItems.Count));
        }
        public Task<UndoBulkOperationResult> UndoAsync(Guid id, CancellationToken cancellationToken = default) { UndoCalls++; return Task.FromResult(new UndoBulkOperationResult(id, true, "Geri alındı")); }
    }
}
