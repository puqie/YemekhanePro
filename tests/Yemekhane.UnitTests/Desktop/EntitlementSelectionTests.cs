using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Yemekhane.Application.BulkOperations;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Desktop;
using Yemekhane.Desktop.Controls;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ogun atamada ogrenci seciminin kullanilabilir oldugunu dogrular.
///
/// Once "Hizli Hakedis" cekmecesinde ogrenci kimlikleri her zaman ham,
/// virgulle ayrilmis bir metin kutusunda gosteriliyordu -- kullanici bunu
/// elle doldurmasi gerektigini saniyordu. Oysa ManualStudentIds/SetSelection
/// makinesi zaten calisiyor: OpenGrant() secili satirlari otomatik doldurur.
/// Asil sorun goruntudeydi, mekanizmada degildi. Bu yuzden ManualStudentIds
/// KALDIRILMAZ; yalnizca secim varken metin kutusu yerine duz dilde bir ozet
/// gosterilir.
///
/// Bu testler XAML METNINI degil, KURULMUS gorsel agaci olcer -- bir string
/// yorumda gecse bile bu testler bunu KANIT saymaz.
/// </summary>
[Collection("UI")]
public sealed class EntitlementSelectionTests
{
    private static MealEntitlementsViewModel CreateViewModel() =>
        new(new FakeApi(), ["entitlements.manage", "entitlements.bulk"]);

    private static MealEntitlementListItem Item(string studentNo, string name, string className, string card) =>
        new(Guid.NewGuid(), Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), studentNo, card, "Öğle",
            name, className, 1, 0, 1, "Active", "Manual", 1);

    /// <summary>Liste DataGrid olarak kalmali -- OnSelectionChanged bunu sartlar.</summary>
    [Fact]
    public void ListeDataGridOlarakKalir() =>
        UiThread.Run(() =>
        {
            var view = new MealEntitlementsView();

            Assert.NotNull(view.FindName("EntitlementsGrid"));
            Assert.IsType<DataGrid>(view.FindName("EntitlementsGrid"));
        });

    /// <summary>
    /// Satirin herhangi bir yerine GERCEK arayuzde tiklamak secimi kurar; ikinci satir
    /// tiklandiginda birinci secim dusmez. Gosterge WPF'nin kucuk varsayilan kutusu
    /// degil, tasarim sistemindeki 22px SelectionIndicator'dur.
    /// </summary>
    [Fact]
    public void TumSatirTikiSecimiKurarVeCokluSecimiKorur() =>
        UiThread.Run(() =>
        {
            var vm = CreateViewModel();
            vm.Items.Add(RowOf(vm, Item("100", "Ayşe Yılmaz", "5A", "1111")));
            vm.Items.Add(RowOf(vm, Item("101", "Mehmet Demir", "5B", "2222")));

            var view = new MealEntitlementsView { DataContext = vm };
            var host = UiThread.Host(view, 1600, 900);
            host.Measure(new Size(1600, 900));
            host.Arrange(new Rect(0, 0, 1600, 900));
            host.UpdateLayout();

            var grid = Assert.IsType<DataGrid>(view.FindName("EntitlementsGrid"));
            Assert.True(SelectionRowToggle.GetIsEnabled(grid));
            var indicators = Descendants(view).OfType<Border>()
                .Where(x => x.Name == "SelectionBox").ToList();
            Assert.Equal(2, indicators.Count);
            Assert.All(indicators, x =>
            {
                Assert.Equal(22d, x.ActualWidth, precision: 1);
                Assert.Equal(22d, x.ActualHeight, precision: 1);
            });

            ClickRow(grid, 0);
            host.UpdateLayout();
            Assert.True(vm.Items[0].IsSelected);
            Assert.Single(vm.SelectedItems);

            ClickRow(grid, 1);
            host.UpdateLayout();
            Assert.Equal(2, vm.SelectedItems.Count);
            Assert.True(vm.Items[0].IsSelected);
            Assert.True(vm.Items[1].IsSelected);
            Assert.True(vm.HasSelection);
        });

    private static void ClickRow(DataGrid grid, int index)
    {
        var row = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromIndex(index));
        row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
            Source = row
        });
    }

    /// <summary>Filtre/yeniden yukleme sonrasi ESKI secim kalmamali; toplu islem gorunmeyen satiri kapsardi.</summary>
    [Fact]
    public async Task YenidenYuklemeSecimiTemizler()
    {
        var vm = CreateViewModel();
        vm.Items.Add(RowOf(vm, Item("100", "Ayşe Yılmaz", "5A", "1111")));
        vm.Items[0].IsSelected = true;
        Assert.Single(vm.SelectedItems);

        await vm.LoadAsync(1);

        Assert.Empty(vm.SelectedItems);
        Assert.False(vm.HasSelection);
    }

    /// <summary>
    /// <summary>Elle numara/GUID kutusu arayüzden kaldırılmış olmalıdır.</summary>
    [Fact]
    public void ElleGirisKutusuArayuzdeYoktur() =>
        UiThread.Run(() =>
        {
            var vm = CreateViewModel();
            vm.Items.Add(RowOf(vm, Item("100", "Ayşe Yılmaz", "5A", "1111")));
            vm.OpenGrantCommand.Execute(null);

            var view = new MealEntitlementsView { DataContext = vm };
            Layout(view);

            Assert.Null(FindByName(view, "ManualStudentIdsBox"));
        });

    /// <summary>Seçili hakediş satırları ad/no ile picker'a taşınır; ham GUID gösterilmez.</summary>
    [Fact]
    public void SecimVarkenOgrencilerPickerdaSeciliGorunur() =>
        UiThread.Run(() =>
        {
            var vm = CreateViewModel();
            var a = Item("100", "Ayşe Yılmaz", "5A", "1111");
            var b = Item("101", "Mehmet Demir", "5B", "2222");
            vm.Items.Add(RowOf(vm, a)); vm.Items.Add(RowOf(vm, b));
            Tick(vm, a, b);
            vm.OpenGrantCommand.Execute(null);

            var view = new MealEntitlementsView { DataContext = vm };
            Layout(view);

            Assert.Null(FindByName(view, "ManualStudentIdsBox"));
            Assert.Equal(2, vm.StudentPicker.Count(x => x.IsSelected));
            Assert.DoesNotContain(a.StudentId.ToString(), vm.PickerSummary);
        });

    /// <summary>Onizleme ve Uygula korunmali: 200 ogrenciye yanlis atamayi engelliyor.</summary>
    [Fact]
    public void EtkileriOnizleKorundu() =>
        UiThread.Run(() =>
        {
            var vm = CreateViewModel();
            vm.OpenGrantCommand.Execute(null);
            var view = new MealEntitlementsView { DataContext = vm };
            Layout(view);

            Assert.NotNull(FindByType<Button>(view, b => Equals(b.Command, vm.PreviewCommand)));
            Assert.NotNull(FindByType<Button>(view, b => Equals(b.Command, vm.ApplyCommand)));
        });

    /// <summary>
    /// Son adim genel "Uygula" degil, kullanicinin ne yapildigini anlayacagi acik
    /// "Öğün İşle" adini tasir. Kullanici bu eylemin eksik oldugunu sahada bildirdi.
    /// </summary>
    [Fact]
    public void SonIslemDugmesiOgunIsleAdiniTasiyor() =>
        UiThread.Run(() =>
        {
            var vm = CreateViewModel();
            vm.OpenGrantCommand.Execute(null);
            var view = new MealEntitlementsView { DataContext = vm };
            Layout(view);

            var button = Assert.IsType<Button>(FindByName(view, "ProcessMealButton"));
            Assert.Equal("Öğün İşle", button.Content);
            Assert.Equal(vm.ApplyCommand, button.Command);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.True(button.ActualWidth > 0 && button.ActualHeight > 0,
                $"Öğün İşle düğmesi ölçülmedi: {button.ActualWidth:F0}x{button.ActualHeight:F0}");
            Assert.False(button.IsEnabled, "Önizleme alınmadan öğün işlenememeli.");
        });

    /// <summary>
    /// "Ücreti kasaya ekle" secenegi fiyatsiz/0 TL ogunde tamamen KAYBOLMAZ.
    /// Gorunur ama devre disidir; ucretli ogun secilince etkinlesir. Boylece kullanici
    /// ozelligin bulunmadigini sanmaz, eksik olanin ogun ucreti oldugunu anlar.
    /// </summary>
    [Fact]
    public void KasaSecenegiHerZamanGorunurUcretliOgundeEtkinlesir() =>
        UiThread.Run(() =>
        {
            var vm = CreateViewModel();
            vm.OpenGrantCommand.Execute(null);
            var view = new MealEntitlementsView { DataContext = vm };
            Layout(view);

            var charge = Assert.IsType<CheckBox>(FindByName(view, "ChargeToCashCheckBox"));
            Assert.Equal("Ücreti kasaya ekle", charge.Content);
            Assert.Equal(Visibility.Visible, charge.Visibility);
            Assert.False(charge.IsEnabled);

            vm.GrantMeal = new MealTypeDetails(Guid.NewGuid(), "Öğle", null, null, true, 250m);
            view.UpdateLayout();

            Assert.True(charge.IsEnabled);
        });

    /// <summary>Hızlı Hakediş çekmecesi dar kalmamalı ve yerel genişlik taşımalıdır.</summary>
    [Fact]
    public void HakedisCekmecesiDrawerKontroluKullanir() =>
        UiThread.Run(() =>
        {
            var vm = CreateViewModel();
            var view = new MealEntitlementsView { DataContext = vm };
            Layout(view);

            var drawer = FindByType<Drawer>(view, _ => true);
            Assert.NotNull(drawer);
            // Genişlik XAML'de yerel olarak atanır; varsayılan dar 400px kullanılmaz.
            Assert.NotEqual(DependencyProperty.UnsetValue,
                drawer!.ReadLocalValue(Drawer.DrawerWidthProperty));
            Assert.Equal(560d, drawer.DrawerWidth);
        });

    /// <summary>
    /// Iki katmanin ust uste binmemesi: Hakedis cekmecesi ACIKKEN, Iptal onayini
    /// acan "Seçileni İptal Et" dugmesi devre disi kalir; boylece kullanici
    /// ikinci bir katmani ustune yiginamaz. Bu, IsEnabled MIRASININ KARDES
    /// KONTEYNERLER ARASINDA calismadigi Kasa ekranindaki hatanin ayni
    /// sinifidir: guvenlik VM booleanina degil, DOGRUDAN dugme IsEnabled
    /// baglamasina konur.
    /// </summary>
    [Fact]
    public void HakedisCekmecesiAcikkenIptalDugmesiDevreDisi() =>
        UiThread.Run(() =>
        {
            var vm = CreateViewModel();
            var item = Item("100", "Ayşe Yılmaz", "5A", "1111");
            vm.Items.Add(RowOf(vm, item));
            Tick(vm, item);
            vm.OpenGrantCommand.Execute(null);

            var view = new MealEntitlementsView { DataContext = vm };
            Layout(view);

            var requestCancel = FindByType<Button>(view, b => Equals(b.Command, vm.RequestCancelCommand));

            Assert.NotNull(requestCancel);
            Assert.False(requestCancel!.IsEnabled, "Hakediş çekmecesi açıkken iptal onayı üstüne açılabilmemeli.");
        });

    /// <summary>Tersi yon: Iptal onayi acikken Hakedis cekmecesini acan dugme devre disi.</summary>
    [Fact]
    public void IptalOnayiAcikkenHakedisDugmesiDevreDisi() =>
        UiThread.Run(() =>
        {
            var vm = CreateViewModel();
            var item = Item("100", "Ayşe Yılmaz", "5A", "1111");
            vm.Items.Add(RowOf(vm, item));
            Tick(vm, item);
            vm.RequestCancelCommand.Execute(null);
            Assert.True(vm.IsCancelConfirmationOpen);

            var view = new MealEntitlementsView { DataContext = vm };
            Layout(view);

            var openGrant = FindByType<Button>(view, b => Equals(b.Command, vm.OpenGrantCommand));
            Assert.NotNull(openGrant);
            Assert.False(openGrant!.IsEnabled, "İptal onayı açıkken Hızlı Hakediş açılabilmemeli.");
        });

    /// <summary>
    /// Ucuncu katman: Toplu Islem sihirbazi (BulkWizard) da ayni ekranin ustune
    /// tam panel olarak acilir (Panel.ZIndex=20). Bu VM'e AIT ayri bir IsOpen
    /// tasidigindan, Hakedis cekmecesini/Iptal onayini acan dugmelerin de bu
    /// UCUNCU katmana karsi korunmasi gerekir -- yoksa iki tam ekran katman
    /// ust uste biner.
    /// </summary>
    [Fact]
    public void TopluIslemSihirbaziAcikkenDigerTetikleyicilerDevreDisi() =>
        UiThread.Run(() =>
        {
            var wizard = new BulkOperationWizardViewModel(new FakeBulkApi(), ["entitlements.bulk", "calendar.manage"]);
            var vm = new MealEntitlementsViewModel(new FakeApi(), ["entitlements.manage", "entitlements.bulk"], wizard);
            var item = Item("100", "Ayşe Yılmaz", "5A", "1111");
            vm.Items.Add(RowOf(vm, item));
            Tick(vm, item);
            wizard.OpenCommand.Execute(null);
            Assert.True(wizard.IsOpen);

            var view = new MealEntitlementsView { DataContext = vm };
            Layout(view);

            var openGrant = FindByType<Button>(view, b => Equals(b.Command, vm.OpenGrantCommand));
            var requestCancel = FindByType<Button>(view, b => Equals(b.Command, vm.RequestCancelCommand));

            Assert.NotNull(openGrant);
            Assert.NotNull(requestCancel);
            Assert.False(openGrant!.IsEnabled, "Toplu işlem sihirbazı açıkken Hızlı Hakediş açılabilmemeli.");
            Assert.False(requestCancel!.IsEnabled, "Toplu işlem sihirbazı açıkken iptal onayı açılabilmemeli.");
        });

    /// <summary>
    /// Duzeltme turu 1, Onemli 1: rota yolu (Ogrenciler ekranindan Ctrl+K ile
    /// "Hakediş Ver" secmek) OpenGrant()'i DOGRUDAN ViewModel uzerinde cagirir --
    /// hicbir dugmeye dokunmaz, dolayisiyla hicbir IsEnabled MultiBinding'i
    /// devreye giremez. Iptal onay modali (ScrimStrongBrush, yikici) acikken
    /// bu rota tetiklenirse, Hakedis cekmecesi onun USTUNE acilirdi ve Escape
    /// (MainWindow.xaml.cs:321) yalnizca alttaki modali kapatip cekmeceyi
    /// ekranda birakirdi. Duzeltme MainWindow.xaml.cs'de (code-behind, dondurulmus
    /// degil): rota isleyici HandleRoute'tan ONCE CloseCancelCommand'i calistirir.
    /// </summary>
    [Fact]
    public void RotaYoluAcikIptalOnayininUstuneCekmeceAcamaz() =>
        UiThread.Run(() =>
        {
            var vm = CreateViewModel();
            var item = Item("100", "Ayşe Yılmaz", "5A", "1111");
            vm.Items.Add(RowOf(vm, item));
            Tick(vm, item);
            vm.RequestCancelCommand.Execute(null);
            Assert.True(vm.IsCancelConfirmationOpen);

            var window = new MainWindow();
            UiThread.ApplyResources(window);
            window.MealEntitlementsDataContext = vm;
            window.ConfigureShortcuts(new HashSet<string> { "entitlements.manage", "entitlements.bulk" });

            window.Navigate($"{ShellRoutes.Entitlements}/{item.StudentId:D}");

            Assert.False(vm.IsGrantOpen && vm.IsCancelConfirmationOpen,
                "Hakediş çekmecesi ve iptal onayı aynı anda açık olamaz.");
            window.Close();
        });

    /// <summary>
    /// Duzeltme turu 1, Onemli 2: SelectedCountText'in gorunur METNI olcum
    /// yoluyla dogrulanir. Yol yanlis yazilsa (orn. SelectedItem.Count) WPF
    /// bunu sessizce yoksayip "Seçili: " metnini sonsuza kadar gosterirdi --
    /// yalnizca trace seviyesinde bir binding hatasi cikar, kullaniciya hicbir
    /// sey gorunmez.
    /// </summary>
    [Fact]
    public void SeciliSayisiMetniSecimeGoreGuncellenir() =>
        UiThread.Run(() =>
        {
            var vm = CreateViewModel();
            var a = Item("100", "Ayşe Yılmaz", "5A", "1111");
            var b = Item("101", "Mehmet Demir", "5B", "2222");
            vm.Items.Add(RowOf(vm, a)); vm.Items.Add(RowOf(vm, b));

            var view = new MealEntitlementsView { DataContext = vm };
            Layout(view);

            var countText = (TextBlock)FindByName(view, "SelectedCountText")!;
            Assert.Equal("Seçili: 0", countText.Text);

            Tick(vm, a, b);
            view.UpdateLayout();

            Assert.Equal("Seçili: 2", countText.Text);
        });

    private static Border Layout(FrameworkElement view)
    {
        var host = UiThread.Host(view, 1600, 900);
        host.Measure(new Size(1600, 900));
        host.Arrange(new Rect(0, 0, 1600, 900));
        host.UpdateLayout();
        return host;
    }

    private static object? FindByName(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe)
        {
            var found = fe.FindName(name);
            if (found is not null) return found;
        }
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var result = FindByName(child, name);
            if (result is not null) return result;
        }
        return null;
    }

    private static T? FindByType<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is T typed && predicate(typed)) return typed;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var result = FindByType(child, predicate);
            if (result is not null) return result;
        }
        return null;
    }


    /// <summary>Listeye satir sarmalayicisi koyar (secim satirin kendisindedir).</summary>
    private static MealEntitlementRowViewModel RowOf(MealEntitlementsViewModel vm, MealEntitlementListItem item) =>
        new(item, vm.RebuildSelectionForTests);

    /// <summary>Verilen kayitlarin satirlarini isaretler.</summary>
    private static void Tick(MealEntitlementsViewModel vm, params MealEntitlementListItem[] items)
    {
        foreach (var item in items)
        {
            var row = vm.Items.FirstOrDefault(x => x.Id == item.Id);
            if (row is null) { row = RowOf(vm, item); vm.Items.Add(row); }
            row.IsSelected = true;
        }
    }


    /// <summary>Gorsel agactaki tum alt ogeler (onay kutusunu bulmak icin).</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class FakeApi : IMealEntitlementApiClient
    {
        public Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken ct = default) =>
            Task.FromResult(new MealEntitlementPage([], 1, 50, 0, new MealEntitlementSummary(0, 0, 0)));
        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MealTypeDetails>>([]);
        public Task<IReadOnlyList<ClassRecord>> ClassesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ClassRecord>>([]);
        public Task<IReadOnlyList<GroupRecord>> GroupsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GroupRecord>>([]);
        public Task<EntitlementPreview> PreviewAsync(EntitlementGrantRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<BulkEntitlementResult> ApplyAsync(ApplyEntitlementGrantRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CancelEntitlementsResult> CancelAsync(CancelEntitlementsRequest request, CancellationToken ct = default) =>
            Task.FromResult(new CancelEntitlementsResult(request.EntitlementIds.Count));
    }

    private sealed class FakeBulkApi : IBulkOperationApiClient
    {
        public Task<IReadOnlyCollection<CalendarScopeOption>> ScopesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyCollection<CalendarScopeOption>>([new("AllSchool", null, "Tüm okul")]);
        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MealTypeDetails>>([]);
        public Task<BulkOperationPreview> PreviewAsync(BulkCalendarOperationRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<BulkOperationResult> ApplyAsync(ApplyBulkOperationRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<BulkOperationHistoryPage> HistoryAsync(CancellationToken ct = default) =>
            Task.FromResult(new BulkOperationHistoryPage([], 1, 30, 0));
        public Task<UndoBulkOperationResult> UndoAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(new UndoBulkOperationResult(id, true, "Geri alındı"));
    }
}
