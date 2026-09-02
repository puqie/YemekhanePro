using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Xunit;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Desktop;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Tanimlar ekraninin CANLI API uzerinden uctan uca yolculugu: ogun ekle/duzenle/hatali
/// saat/pasiflestir, dort tanim sekmesinde ekle -> listede gor -> yeniden adlandir -> sil,
/// kullanilan sinifi silmeye calisinca 409 mesaji, ogun ucreti -> Hakedis cekmecesinde bedel,
/// Ayarlar'daki "Yemek Türleri" dugmesi. Her adimda ekran cekilir. YP_LIVE_API yoksa sessizce gecer.
/// </summary>
[Collection("UI")]
public class DefinitionsJourney
{
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void TanimlarAkisi() => Run(ui =>
    {
        // Yolculuk tekrar kosulabilsin: Ogle Yemegi ucreti ekranlar yuklenmeden ONCE sifirlanir;
        // boylece Hakedis ekrani eski (0) bedelle acilir ve tazeleme gercekten sinanir.
        var lunch = Get<List<MealTypeDetails>>(ui, "api/meal-types?includeInactive=true").Single(m => m.Name == "Öğle Yemeği");
        Put(ui, $"api/meal-types/{lunch.Id:D}", new SaveMealTypeRequest(lunch.Name, lunch.StartsAt, lunch.EndsAt, lunch.IsActive, 0));

        ui.LoadAll(); ui.Navigate("definitions");
        var vm = ui.Definitions;
        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.True(vm.CanManageMeals && vm.CanManageLookups, "admin yetkileri okunamadi");
        Assert.True(vm.Meals.Count >= 3, "tohum ogunleri gelmedi");
        Assert.Equal(Visibility.Visible, Host(ui, "DefinitionsHost").Visibility);
        var nav = ((Panel)ui.Window.FindName("NavigationButtons")!).Children.OfType<Button>().Single(b => (string)b.Tag == "definitions");
        Assert.Equal(Visibility.Visible, nav.Visibility);
        Assert.True(NavigationSelection.GetIsSelected(nav), "menude Tanimlar secili degil");
        ui.Shot("tanimlar-01-ogunler");
        AssertNoClippedCells(ui, Grid(ui, "MealsGrid"));
        AssertNoRawEnglish(ui);

        var suffix = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- ogunler
        var mealName = "Akşam " + suffix;
        vm.OpenNewMealCommand.Execute(null); ui.Pump();
        Assert.True(vm.IsMealOpen);
        vm.MealName = mealName; vm.MealStartsAt = "17:30"; vm.MealEndsAt = "abc"; vm.MealPriceText = "180,50";
        ui.Shot("tanimlar-02-ogun-cekmece");
        Execute(ui, vm.SaveMealCommand);
        Assert.True(vm.IsMealOpen, "hatali saatle cekmece kapanmamali");
        Assert.True(vm.HasMealError, "hatali saat mesaji yok");
        Assert.Contains("SS:dd", vm.MealError);
        ui.Note("hatali saat mesaji: " + vm.MealError);
        ui.Shot("tanimlar-03-ogun-hatali-saat");

        vm.MealEndsAt = "19:00";
        Execute(ui, vm.SaveMealCommand);
        Assert.False(vm.IsMealOpen, "kayit sonrasi cekmece kapanmadi: " + vm.MealError);
        var created = vm.Meals.SingleOrDefault(m => m.Name == mealName);
        Assert.NotNull(created);
        Assert.Equal(180.50m, created!.Price);
        Assert.Equal("17:30", created.StartsText);
        Assert.Equal("Aktif", created.StatusText);
        Assert.Equal(created.Id, vm.SelectedMeal?.Id);
        var apiMeal = Get<List<MealTypeDetails>>(ui, "api/meal-types?includeInactive=true").Single(m => m.Name == mealName);
        Assert.Equal(180.50m, apiMeal.Price);
        Assert.Equal(new TimeOnly(19, 0), apiMeal.EndsAt);
        ui.Shot("tanimlar-04-ogun-listede");
        AssertNoClippedCells(ui, Grid(ui, "MealsGrid"));

        // Ucreti 250.50 (noktali) yazip 250,50 okunmali.
        vm.OpenEditMealCommand.Execute(null); ui.Pump();
        Assert.Equal("Öğünü Düzenle", vm.MealFormTitle);
        Assert.Equal("180,50", vm.MealPriceText);
        vm.MealPriceText = "250.50";
        Execute(ui, vm.SaveMealCommand);
        Assert.False(vm.IsMealOpen, vm.MealError);
        Assert.Equal(250.50m, vm.Meals.Single(m => m.Id == created.Id).Price);

        // Pasiflestir: satir listede kalir, durum Pasif, listenin sonuna gider.
        Execute(ui, vm.DeactivateMealCommand);
        Assert.False(vm.HasError, vm.ErrorMessage);
        var passive = vm.Meals.Single(m => m.Id == created.Id);
        Assert.Equal("Pasif", passive.StatusText);
        Assert.Equal(vm.Meals.Count - 1, vm.Meals.IndexOf(passive));
        Assert.False(vm.DeactivateMealCommand.CanExecute(null));
        Assert.DoesNotContain(Get<List<MealTypeDetails>>(ui, "api/meal-types?includeInactive=false"), m => m.Id == created.Id);
        ui.Shot("tanimlar-05-ogun-pasif");

        // Ogle Yemegi'ne ucret: Hakedis cekmecesinde bedel bu deger uzerinden gorunur.
        vm.SelectedMeal = vm.Meals.Single(m => m.Name == "Öğle Yemeği");
        vm.OpenEditMealCommand.Execute(null); ui.Pump();
        vm.MealPriceText = "250";
        Execute(ui, vm.SaveMealCommand);
        Assert.False(vm.IsMealOpen, vm.MealError);
        Assert.Equal(250m, vm.Meals.Single(m => m.Name == "Öğle Yemeği").Price);

        // ---------------------------------------------------------------- tanim sekmeleri
        var tabs = new (LookupTabViewModel Tab, int Index)[] { (vm.Classes, 1), (vm.Sections, 2), (vm.Departments, 3), (vm.Jobs, 4) };
        foreach (var (tab, index) in tabs)
        {
            vm.SelectedTabIndex = index; ui.Pump();
            var name = $"{tab.Singular} {suffix}";
            var before = tab.Items.Count;
            tab.NewName = name;
            Execute(ui, tab.AddCommand);
            Assert.False(tab.HasError, tab.ErrorMessage);
            Assert.Equal(before + 1, tab.Items.Count);
            Assert.Equal(name, tab.SelectedItem?.Name);
            Assert.Equal("", tab.NewName);
            Assert.Contains(Get<List<LookupRecord>>(ui, $"api/organization/{tab.Kind}/lookups"), x => x.Name == name);
            ui.Shot($"tanimlar-1{index}-{tab.Kind}-eklendi");
            AssertNoClippedCells(ui, VisibleLookupGrid(ui));

            tab.OpenRenameCommand.Execute(null); ui.Pump();
            Assert.True(tab.IsRenameOpen);
            Assert.Equal(name, tab.RenameName);
            tab.RenameName = name + " Y";
            ui.Shot($"tanimlar-2{index}-{tab.Kind}-yeniden-adlandir");
            Execute(ui, tab.SaveRenameCommand);
            Assert.False(tab.HasError, tab.ErrorMessage);
            Assert.False(tab.IsRenameOpen);
            Assert.Equal(name + " Y", tab.SelectedItem?.Name);
            Assert.Contains(Get<List<LookupRecord>>(ui, $"api/organization/{tab.Kind}/lookups"), x => x.Name == name + " Y");

            Execute(ui, tab.DeleteCommand);
            Assert.True(tab.IsDeleteArmed);
            Assert.Equal("Silmeyi Onayla", tab.DeleteButtonText);
            Assert.Contains(FindButtons(ui), b => (b.Content as string) == "Silmeyi Onayla" && b.IsVisible);
            Assert.Contains(FindButtons(ui), b => (b.Content as string) == "Vazgeç" && b.IsVisible);
            ui.Shot($"tanimlar-3{index}-{tab.Kind}-sil-onay");
            Execute(ui, tab.DeleteCommand);
            Assert.False(tab.HasError, tab.ErrorMessage);
            Assert.Equal(before, tab.Items.Count);
            Assert.DoesNotContain(Get<List<LookupRecord>>(ui, $"api/organization/{tab.Kind}/lookups"), x => x.Name == name + " Y");
            ui.Note($"{tab.Title}: ekle/yeniden adlandir/sil tamam ({before} kayit)");
        }
        // Bolumler ve Gorevler tohumda bos: silme sonrasi "Henüz kayıt yok" gorunmeli.
        vm.SelectedTabIndex = 3; ui.Pump();
        if (vm.Departments.Items.Count == 0)
        {
            Assert.True(vm.Departments.IsEmpty);
            Assert.Contains(ui.FindAll<TextBlock>().Where(t => t.IsVisible), t => t.Text == "Henüz kayıt yok");
            ui.Shot("tanimlar-40-bolumler-bos");
        }

        // ---------------------------------------------------------------- 409: kullanilan sinif
        vm.SelectedTabIndex = 1; ui.Pump();
        var used = vm.Classes.Items.First(x => x.StudentCount > 0);
        vm.Classes.SelectedItem = used;
        Execute(ui, vm.Classes.DeleteCommand);
        Execute(ui, vm.Classes.DeleteCommand);
        Assert.True(vm.Classes.HasError, "409 mesaji gelmedi");
        Assert.Contains($"{used.StudentCount} öğrencide kullanılıyor", vm.Classes.ErrorMessage);
        Assert.Contains("başka bir tanıma taşıyın", vm.Classes.ErrorMessage);
        Assert.False(vm.Classes.IsDeleteArmed);
        Assert.Contains(vm.Classes.Items, x => x.Id == used.Id);
        Assert.Contains(ui.FindAll<TextBlock>().Where(t => t.IsVisible), t => t.Text == vm.Classes.ErrorMessage);
        ui.Note("409 mesaji: " + vm.Classes.ErrorMessage);
        ui.Shot("tanimlar-41-sinif-409");

        // ---------------------------------------------------------------- Hakedis cekmecesinde bedel
        // Hakedis ekrani acilista yuklendi (ucret o zaman 0'di); cekmece acilinca liste tazelenir.
        ui.Navigate("entitlements");
        var ent = ui.Entitlements;
        Assert.Equal(0m, ent.MealTypes.Single(x => x.Name == "Öğle Yemeği").Price);
        ent.OpenGrantCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.True(ent.IsGrantOpen);
        ent.GrantMeal = ent.MealTypes.Single(x => x.Name == "Öğle Yemeği");
        Assert.True(ent.HasGrantMealPrice);
        Assert.Equal("Öğün bedeli: ₺250,00", ent.GrantMealPriceText);
        Assert.Contains(ui.FindAll<TextBlock>().Where(t => t.IsVisible), t => t.Text == "Öğün bedeli: ₺250,00");
        ent.TargetType = "Class"; ent.GrantClass = ent.Classes.First(); ent.QuantityText = "1";
        ent.GrantStartsOn = new DateTime(2026, 9, 7); ent.GrantEndsOn = new DateTime(2026, 9, 11);
        Execute(ui, ent.PreviewCommand); ui.Delay(1500); ui.Pump();
        Assert.True(ent.HasPreview, "onizleme yok: " + ent.PreviewMessage);
        Assert.True(ent.HasPreviewTotal);
        Assert.Equal(250m * ent.Preview!.RightsCount, ent.PreviewTotal);
        Assert.Equal("Toplam bedel: " + ent.PreviewTotal.ToString("C2", CultureInfo.GetCultureInfo("tr-TR")), ent.PreviewTotalText);
        Assert.Contains(ui.FindAll<TextBlock>().Where(t => t.IsVisible), t => t.Text == ent.PreviewTotalText);
        ui.Note("hakedis bedeli: " + ent.GrantMealPriceText + " / " + ent.PreviewTotalText);
        ui.Shot("tanimlar-50-hakedis-bedel");
        ent.CloseGrantCommand.Execute(null); ui.Pump();

        // Ucretsiz ogunde bedel satiri gizli.
        ent.OpenGrantCommand.Execute(null); ui.Pump();
        ent.GrantMeal = ent.MealTypes.Single(x => x.Name == "Kahvaltı");
        Assert.False(ent.HasGrantMealPrice);
        Assert.DoesNotContain(ui.FindAll<TextBlock>().Where(t => t.IsVisible), t => t.Text.StartsWith("Öğün bedeli", StringComparison.Ordinal));
        ent.CloseGrantCommand.Execute(null); ui.Pump();

        // ---------------------------------------------------------------- Ayarlar -> Yemek Turleri
        ui.Navigate("settings");
        Assert.True(ui.Settings.NavigateMealsCommand.CanExecute(null));
        ui.Settings.NavigateMealsCommand.Execute(null); ui.Pump();
        Assert.Equal(Visibility.Visible, Host(ui, "DefinitionsHost").Visibility);
        Assert.Equal(Visibility.Collapsed, Host(ui, "SettingsHost").Visibility);
        ui.Shot("tanimlar-60-ayarlardan-gelis");
    });

    // ---------------------------------------------------------------- yardimcilar

    /// <summary>Komut govdesinden kacan hatalar (AsyncCommand.UnhandledError) testi dusurur; sessiz yutulmaz.</summary>
    private static void Run(Action<LiveUiHarness> journey) => LiveUiHarness.Run(ui =>
    {
        var errors = new List<string>();
        EventHandler<Exception> handler = (_, ex) => errors.Add(ex.GetType().Name + ": " + ex.Message);
        AsyncCommand.UnhandledError += handler;
        try { journey(ui); }
        finally
        {
            AsyncCommand.UnhandledError -= handler;
            foreach (var error in errors) ui.Note("KOMUT HATASI: " + error);
        }
        Assert.True(errors.Count == 0, "komut govdesinden kacan hata: " + string.Join(" | ", errors));
    });

    private static FrameworkElement Host(LiveUiHarness ui, string name) => (FrameworkElement)ui.Window.FindName(name)!;

    private static DataGrid Grid(LiveUiHarness ui, string name) =>
        ui.FindAll<DataGrid>(Host(ui, "DefinitionsHost")).Single(g => g.Name == name);

    /// <summary>Secili tanim sekmesindeki (yerlestirilmis) DataGrid.</summary>
    private static DataGrid VisibleLookupGrid(LiveUiHarness ui) =>
        ui.FindAll<DataGrid>(Host(ui, "DefinitionsHost")).Single(g => g.Name != "MealsGrid" && g.IsVisible && g.ActualWidth > 0);

    private static IEnumerable<Button> FindButtons(LiveUiHarness ui) => ui.FindAll<Button>(Host(ui, "DefinitionsHost"));

    private static T Get<T>(LiveUiHarness ui, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ui.Session.AccessToken);
        var task = ui.Http.SendAsync(request);
        Assert.True(LiveUiHarness.Wait(task, ApiTimeout), "API zaman asimi: " + url);
        Assert.True(task.Result.IsSuccessStatusCode, $"{url} -> {(int)task.Result.StatusCode}");
        var body = task.Result.Content.ReadFromJsonAsync<T>();
        Assert.True(LiveUiHarness.Wait(body, ApiTimeout));
        return body.Result!;
    }

    private static void Put<T>(LiveUiHarness ui, string url, T body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ui.Session.AccessToken);
        var task = ui.Http.SendAsync(request);
        Assert.True(LiveUiHarness.Wait(task, ApiTimeout), "API zaman asimi: " + url);
        Assert.True(task.Result.IsSuccessStatusCode, $"{url} -> {(int)task.Result.StatusCode}");
    }

    private static void Execute(LiveUiHarness ui, System.Windows.Input.ICommand command)
    {
        Assert.True(command.CanExecute(null), "komut calistirilamaz durumda");
        if (command is AsyncCommand async) Assert.True(LiveUiHarness.Wait(async.ExecuteAsync(null), TimeSpan.FromSeconds(30)), "komut zaman asimi");
        else command.Execute(null);
        ui.Pump();
    }

    /// <summary>Ekranda ham API kodu (Active/Inactive/true/false) kalmamali; durum Turkce yazilir.</summary>
    private static void AssertNoRawEnglish(LiveUiHarness ui)
    {
        var raw = ui.FindAll<TextBlock>(Host(ui, "DefinitionsHost")).Where(t => t.IsVisible)
            .Select(t => t.Text).Where(t => t is "Active" or "Inactive" or "True" or "False" or "true" or "false").ToList();
        Assert.Empty(raw);
    }

    /// <summary>Gorunen her metin hucresinin icerigi hucreye sigmali; sigmayanlar Note'a yazilir ve test duser.</summary>
    private static void AssertNoClippedCells(LiveUiHarness ui, DataGrid grid)
    {
        var clipped = new List<string>();
        foreach (var cell in ui.FindAll<DataGridCell>(grid))
        {
            if (cell.Content is not TextBlock text || string.IsNullOrEmpty(text.Text)) continue;
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var needed = text.DesiredSize.Width + cell.Padding.Left + cell.Padding.Right;
            if (needed > cell.ActualWidth + 0.5)
                clipped.Add($"{cell.Column.Header}: '{text.Text}' {needed:F0}px > {cell.ActualWidth:F0}px");
        }
        foreach (var header in ui.FindAll<DataGridColumnHeader>(grid))
        {
            if (header.Content is not string title) continue;
            var tb = ui.FindAll<TextBlock>(header).FirstOrDefault();
            if (tb is null) continue;
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (tb.DesiredSize.Width + header.Padding.Left + header.Padding.Right > header.ActualWidth + 0.5)
                clipped.Add($"BASLIK {title}: {tb.DesiredSize.Width:F0}px > {header.ActualWidth:F0}px");
        }
        foreach (var c in clipped.Distinct()) ui.Note("KESIK: " + c);
        Assert.Empty(clipped.Distinct());
    }
}
