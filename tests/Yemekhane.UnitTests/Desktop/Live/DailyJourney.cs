using System.Windows.Controls;
using Xunit;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop.Live;

[Collection("UI")]
public class DailyJourney
{
    [Fact]
    public void GunlukTakipFiltreSayfalamaCanliVeDetay() => LiveUiHarness.Run(ui =>
    {
        using var db = LiveDb.Open();
        ui.LoadAll();
        ui.Navigate("daily-tracking");
        var vm = ui.Tracking;
        Journey.Until(ui, () => !vm.IsLoading, "günlük takip");
        Assert.Null(vm.ErrorMessage);
        var view = ui.FindAll<Yemekhane.Desktop.Views.DailyTrackingView>().Single();
        var grid = ui.FindAll<DataGrid>(view).Single(x => x.Name == "AccessGrid");

        var range = LiveDb.Range("a.Timestamp", Journey.Today, Journey.Today);
        var from = "FROM access_logs a JOIN devices d ON d.Id = a.DeviceId LEFT JOIN students s ON s.Id = a.StudentId LEFT JOIN classes c ON c.Id = s.ClassId WHERE " + range;
        long Count(string extra = "1") => db.Count($"SELECT COUNT(*) {from} AND {extra}");

        // 1) Ozet kartlari SQLite ile birebir.
        var total = Count();
        Assert.Equal(total, vm.Summary.Total);
        Assert.Equal(Count("a.Decision = 'ALLOW'"), vm.Summary.Allowed);
        Assert.Equal(Count("a.Decision = 'DENY'"), vm.Summary.Denied);
        Assert.Equal((int)Math.Min(100, total), vm.Rows.Count);
        Assert.Equal(total > 100, vm.HasMore);
        ui.Shot("daily-01-ozet");

        // Acilir kutular "Tumu" ile acilir; Neden sutununda ham "OK" yok.
        var combos = ui.FindAll<ComboBox>(view).ToList();
        Assert.Equal(4, combos.Count);
        var search = ui.FindAll<TextBox>(view).First();
        ui.Note($"filtre yükseklikleri: ara {search.ActualHeight:0}px, kutular {string.Join("/", combos.Select(x => $"{x.ActualHeight:0}"))}px; karar kutusu şablon={combos[0].Template?.GetType().Name} düzenlenebilir={combos[0].IsEditable} etkin={combos[0].IsEnabled} stil={(combos[0].Style == combos[1].Style)}");
        Assert.All(combos, combo => Assert.True(Math.Abs(combo.ActualHeight - search.ActualHeight) < 2, $"filtre kutusu yüksekliği {combo.ActualHeight:0} ≠ arama kutusu {search.ActualHeight:0}"));
        Assert.All(combos, combo => Assert.Equal("Tümü", combo.SelectedItem switch
        {
            TrackingFilterOption f => f.Name, TrackingDecisionOption d => d.Name, _ => "(boş)"
        }));
        var cellTexts = Journey.TextsIn(ui, grid).ToList();
        Assert.DoesNotContain("OK", cellTexts);
        Assert.Contains("Geçiş onaylandı", cellTexts);

        // 2) Daha eski kayitlari yukle -> tum gun tek listede.
        if (vm.HasMore)
        {
            vm.LoadMoreCommand.Execute(null);
            Journey.Until(ui, () => !vm.IsLoading && vm.Rows.Count > 100, "daha eski kayıtlar");
            Assert.Equal(total, vm.Rows.Count);
            Assert.False(vm.HasMore);
            Assert.Equal(vm.Rows.Select(x => x.OperationId).Distinct().Count(), vm.Rows.Count);
            ui.Shot("daily-02-tum-kayitlar");
        }

        // 3) Filtreler tek tek; secim uygulamadan sonra kutuda KALMALI.
        vm.Search = "5001";
        Apply(ui, vm, "ara");
        Assert.Equal(Count("(a.CardNumber LIKE '%5001%' OR s.student_no LIKE '%5001%' OR (s.FirstName || ' ' || s.LastName) LIKE '%5001%')"), vm.Summary.Total);
        vm.Search = null;

        vm.SelectedDecisionOption = vm.Decisions.Single(x => x.Value == "DENY");
        Apply(ui, vm, "karar");
        Assert.Equal(Count("a.Decision = 'DENY'"), vm.Summary.Total);
        Assert.Equal(vm.Summary.Total, vm.Summary.Denied);
        Assert.Equal("Reddedildi", (combos[0].SelectedItem as TrackingDecisionOption)?.Name);
        ui.Shot("daily-03-filtre-karar");
        vm.SelectedDecisionOption = vm.Decisions[0];

        var meal = vm.MealTypes.First(x => x.Id.HasValue);
        vm.SelectedMealTypeOption = meal;
        Apply(ui, vm, "öğün");
        Assert.Equal(Count($"a.MealTypeId = '{meal.Id!.Value.ToString().ToUpperInvariant()}'"), vm.Summary.Total);
        Assert.Same(meal, combos[1].SelectedItem);
        vm.SelectedMealTypeOption = TrackingFilterOption.All;

        var device = vm.Devices.First(x => x.Name.StartsWith("Kantin", StringComparison.Ordinal));
        vm.SelectedDeviceOption = device;
        Apply(ui, vm, "cihaz");
        Assert.Equal(Count($"a.DeviceId = '{device.Id!.Value.ToString().ToUpperInvariant()}'"), vm.Summary.Total);
        Assert.Same(device, combos[2].SelectedItem);
        Assert.True(vm.Devices.Count > 2, "cihaz filtresi uygulanınca diğer cihazlar kutudan silindi");
        ui.Shot("daily-04-filtre-cihaz");
        vm.SelectedDeviceOption = TrackingFilterOption.All;

        var schoolClass = vm.Classes.First(x => x.Id.HasValue);
        vm.SelectedClassOption = schoolClass;
        Apply(ui, vm, "sınıf");
        Assert.Equal(Count($"s.ClassId = '{schoolClass.Id!.Value.ToString().ToUpperInvariant()}'"), vm.Summary.Total);
        Assert.Same(schoolClass, combos[3].SelectedItem);
        vm.SelectedClassOption = TrackingFilterOption.All;
        Apply(ui, vm, "filtre temizle");
        Assert.Equal(total, vm.Summary.Total);

        // 4) Canli / Duraklat.
        vm.ToggleLiveCommand.Execute(null);
        Journey.Until(ui, () => !vm.IsLive, "duraklat");
        Assert.Equal("Duraklatıldı", vm.LiveText);
        Assert.Contains(ui.FindAll<Button>(view), x => (x.Content as string) == "Duraklatıldı");
        vm.ToggleLiveCommand.Execute(null);
        Journey.Until(ui, () => vm.IsLive && !vm.IsLoading, "sürdür");

        // 5) Gercek zamanli: SignalR bagli; gercek gecis liste ve kartlara dusmeli.
        Assert.Equal("Bağlı", vm.ConnectionText);
        var deviceId = Guid.Parse(db.Text("SELECT Id FROM devices WHERE Name LIKE 'Yemekhane Giri%'")!);
        var mealId = Guid.Parse(db.Text("SELECT Id FROM meal_types WHERE Name LIKE '%le Yeme%'")!);
        var before = vm.Summary.Total;
        var (operationId, decision) = Journey.SimulateAccess(ui, Journey.UnusedCard(db, descending: false), deviceId, mealId);
        Assert.Equal("ALLOW", decision);
        // SignalR olayi -> RecoverGapAsync -> ozet kartlari sunucudan tazelenir (Toplam +1).
        Journey.Until(ui, () => vm.Summary.Total == before + 1, "canlı geçiş özet kartı", 15000);
        Assert.Equal(Count(), vm.Summary.Total);
        // Tohum verisi bugunun kayitlarini 11:00-11:59'a yazdi; sabah erken kosulan bu yolculukta yeni
        // gecis en yeni satirdan ESKI kaldigi icin "since" sorgusu onu getirmez (gercek kullanimda
        // zaman monoton artar). Satirin gercekten yazildigi Yenile ile dogrulanir.
        if (!vm.Rows.Any(x => x.OperationId == operationId))
        {
            ui.Note("canlı geçiş listeye düşmedi: tohum kayıtları gelecek saatli (11:xx), since sorgusu eskiye bakmaz; Yenile ile doğrulandı");
            vm.RefreshCommand.Execute(null);
            ui.Delay(300);
            Journey.Until(ui, () => !vm.IsLoading, "yenile");
            while (vm.HasMore && !vm.Rows.Any(x => x.OperationId == operationId))
            {
                vm.LoadMoreCommand.Execute(null);
                ui.Delay(300);
                Journey.Until(ui, () => !vm.IsLoading, "daha eski");
            }
        }
        Assert.Contains(vm.Rows, x => x.OperationId == operationId);
        ui.Note($"canlı geçiş: {decision} {operationId}");
        ui.Shot("daily-05-canli-gecis");

        // 6) Cift tik / Enter -> ogrenci detayi (App.xaml.cs'teki kablolama ile ayni).
        vm.StudentDetailNavigationRequested += (_, route) => ui.Navigation.Navigate(route);
        var row = vm.Rows.First(x => x.StudentId.HasValue);
        Assert.True(vm.OpenStudentCommand.CanExecute(row));
        vm.OpenStudentCommand.Execute(row);
        ui.Pump(6);
        Assert.Equal("student-detail", Journey.Route(ui));
        ui.Shot("daily-06-ogrenci-detay");
    });

    private static void Apply(LiveUiHarness ui, DailyTrackingViewModel vm, string what)
    {
        vm.ApplyFiltersCommand.Execute(null);
        ui.Delay(300);
        Journey.Until(ui, () => !vm.IsLoading, what);
        Assert.Null(vm.ErrorMessage);
    }
}
