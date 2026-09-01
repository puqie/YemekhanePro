using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Common;
using Yemekhane.Application.Leaves;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ogrenci ekraninda liste ve formun AYNI ANDA gorunur oldugunu dogrular.
///
/// Once form cekmecede aciliyordu; cekmece acilinca liste kapaniyordu.
/// Eski uygulama bu isi daha hizli yapiyordu cunku ikisi yan yanaydi.
///
/// Duzeltme turu notu (Gorev 7, tur 1): bu dosyadaki bazi testler onceki
/// halinde yalnizca "xaml metninde bu kelime geciyor mu" diye bakiyordu --
/// bu, kelime bir YORUM icinde bile olsa gecerdi. Asagidaki testler artik
/// GERCEK bir StudentsViewModel'e baglanip olculen gorsel agaci kontrol
/// ediyor.
/// </summary>
[Collection("UI")]
public sealed class StudentsLayoutTests
{
    [Fact]
    public void ListeVeFormAyniAndaGorunur() =>
        UiThread.Run(() =>
        {
            var view = new StudentsView();
            UiThread.ApplyResources(view);
            var host = new Border { Width = 1440, Height = 900, Child = view };
            host.Measure(new Size(1440, 900));
            host.Arrange(new Rect(0, 0, 1440, 900));
            host.UpdateLayout();

            var grid = (FrameworkElement)view.FindName("StudentsGrid")!;
            var form = (FrameworkElement)view.FindName("StudentFormPanel")!;

            Assert.True(grid.ActualWidth > 0, "Ogrenci listesi gorunur degil.");
            Assert.True(form.ActualWidth > 0, "Ogrenci formu gorunur degil.");
        });

    /// <summary>Kaldirilan alanlar formda bulunmamali.</summary>
    [Theory]
    [InlineData("FormNationalId")]
    [InlineData("FormAddress")]
    public void KaldirilanAlanFormdaYok(string bindingPath)
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Yemekhane.Desktop", "Views", "StudentsView.xaml"));

        Assert.DoesNotContain(bindingPath, xaml);
    }

    /// <summary>Kart okuma modali bilerek korunur.</summary>
    [Fact]
    public void KartOkumaModaliKorunur()
    {
        var xaml = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Yemekhane.Desktop", "Views", "StudentsView.xaml"));

        Assert.Contains("IsCardWorkflowOpen", xaml);
        Assert.Contains("CardWorkflowHost", xaml);
    }

    /// <summary>
    /// Kritik 1 duzeltmesi: DataGrid'in TEK tiklama (LeftClick) MouseBinding'i XAML'de
    /// dogrudan OpenFullDetailCommand'a baglanmali -- OpenQuickDetailCommand'a degil.
    /// Once tek tiklama yalnizca SelectedStudent'i dolduruyordu, Details bos kaliyor,
    /// No/Ad/Soyad/Not blank kaliyor ve Duzenle/Pasiflestir/Kart Degistir surekli pasif
    /// goruyordu. Bu test dogrudan ViewModel metodunu cagirmiyor -- GERCEK XAML kablolamasini
    /// (InputBindings) okuyor, cunku hata VM'de degil View'in kablolanmasindaydi.
    /// </summary>
    [Fact]
    public void TekTiklamaXamlKablolamasiTamDetayaBagli() =>
        UiThread.Run(() =>
        {
            var api = new FakeStudentApi();
            using var vm = MakeViewModel(api, ["students.read", "students.write"]);
            var view = new StudentsView { DataContext = vm };
            UiThread.ApplyResources(view);
            var host = new Border { Width = 1440, Height = 900, Child = view };
            host.Measure(new Size(1440, 900));
            host.Arrange(new Rect(0, 0, 1440, 900));
            host.UpdateLayout();

            var grid = (DataGrid)view.FindName("StudentsGrid")!;
            var leftClick = grid.InputBindings.OfType<MouseBinding>()
                .FirstOrDefault(b => b.MouseAction == MouseAction.LeftClick);

            Assert.NotNull(leftClick);
            Assert.Same(vm.OpenFullDetailCommand, leftClick!.Command);
        });

    /// <summary>
    /// Kritik 2 duzeltmesi, "Yeni Ogrenci" senaryosu: bir ogrenci secili iken
    /// "Yeni Ogrenci" tiklanirsa, Details null olur ama SelectedStudent DOKUNULMADAN
    /// kalir (OpenCreate ViewModel'de boyle calisiyor). Salt okunur blok (Sinif, Kart No,
    /// Veli Tel, Durum) bu durumda GIZLI olmali; aksi halde onceki ogrencinin kart
    /// numarasi ekranda kalirdi ve kullanici yeni ogrenciyi o karta yaziyormus gibi
    /// gorunurdu.
    /// </summary>
    [Fact]
    public void YeniOgrenciSaltOkunurBloguGizler() =>
        UiThread.Run(() =>
        {
            var api = new FakeStudentApi();
            using var vm = MakeViewModel(api, ["students.read", "students.write"]);
            var item = SampleItem("Ada", "Katırcı", "1001", "CARD-1");
            vm.OpenFullDetailCommand.Execute(item);
            Assert.NotNull(vm.Details);

            vm.NewStudentCommand.Execute(null);
            Assert.Null(vm.Details);
            Assert.NotNull(vm.SelectedStudent); // OpenCreate SelectedStudent'a dokunmaz.

            var view = new StudentsView { DataContext = vm };
            UiThread.ApplyResources(view);
            var host = new Border { Width = 1440, Height = 900, Child = view };
            host.Measure(new Size(1440, 900));
            host.Arrange(new Rect(0, 0, 1440, 900));
            host.UpdateLayout();

            var cardNoBox = Descendants(view).OfType<TextBox>()
                .FirstOrDefault(b => b.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "SelectedStudent.CardNumber");
            Assert.NotNull(cardNoBox);
            Assert.False(IsEffectivelyVisible(cardNoBox!),
                "Yeni Ogrenci acildiginda onceki ogrencinin Kart No alani hala gorunur.");
        });

    /// <summary>
    /// Kritik 2 duzeltmesi, yaris durumu: satir A'ya tiklanip API yaniti donmeden
    /// satir B secilirse (bu testte dogrudan SelectedStudent degistirilerek simule
    /// edilir), Details hala A'yi tasirken salt okunur blok GIZLI kalmalidir --
    /// aksi halde iki farkli ogrenci ayni panelde bir arada gorunur.
    /// </summary>
    [Fact]
    public void FarkliOgrenciSecilinceSaltOkunurBlokGizlenir() =>
        UiThread.Run(() =>
        {
            var api = new FakeStudentApi();
            using var vm = MakeViewModel(api, ["students.read", "students.write"]);
            var a = SampleItem("Ada", "Katırcı", "1001", "CARD-1");
            var b = SampleItem("Ada", "Haşlamacı", "1002", "CARD-2");

            vm.OpenFullDetailCommand.Execute(a);
            Assert.Equal(a.Id, vm.Details!.Id);

            // API yaniti donmeden SelectedStudent B'ye kayar (yaris penceresini simule eder):
            // Details hala A'yi tasir.
            vm.SelectedStudent = b;

            var view = new StudentsView { DataContext = vm };
            UiThread.ApplyResources(view);
            var host = new Border { Width = 1440, Height = 900, Child = view };
            host.Measure(new Size(1440, 900));
            host.Arrange(new Rect(0, 0, 1440, 900));
            host.UpdateLayout();

            var cardNoBox = Descendants(view).OfType<TextBox>()
                .FirstOrDefault(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "SelectedStudent.CardNumber");
            Assert.NotNull(cardNoBox);
            Assert.False(IsEffectivelyVisible(cardNoBox!),
                "Details ve SelectedStudent farkli ogrencileri gosterirken salt okunur blok hala gorunur.");
        });

    /// <summary>
    /// Onemli 4 duzeltmesi: salt okunur bir kutu, duzenlenebilir bir kutudan GORSEL
    /// olarak ayirt edilebilmeli. Ikisi pixel-ayni olursa kullanici salt okunur
    /// kutuya yazip hicbir sey olmadigini gorur.
    /// </summary>
    [Fact]
    public void SaltOkunurAlanGorseleFarkliGorunur() =>
        UiThread.Run(() =>
        {
            var api = new FakeStudentApi();
            using var vm = MakeViewModel(api, ["students.read", "students.write"]);
            var item = SampleItem("Ada", "Katırcı", "1001", "CARD-1");
            vm.OpenFullDetailCommand.Execute(item);
            // Duzenle: FormStudentNo GERCEKTEN yazilabilir olsun (IsFormOpen=true) --
            // aksi halde form yeni acildiginda o da salt okunurdur ve karsilastirma
            // hicbir sey kanitlamaz.
            vm.EditStudentCommand.Execute(null);

            var view = new StudentsView { DataContext = vm };
            UiThread.ApplyResources(view);
            var host = new Border { Width = 1440, Height = 900, Child = view };
            host.Measure(new Size(1440, 900));
            host.Arrange(new Rect(0, 0, 1440, 900));
            host.UpdateLayout();

            var cardNoBox = Descendants(view).OfType<TextBox>()
                .First(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "SelectedStudent.CardNumber");
            var noBox = Descendants(view).OfType<TextBox>()
                .First(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == "FormStudentNo");

            Assert.True(cardNoBox.IsReadOnly, "Kart No alani salt okunur olmali.");
            Assert.False(noBox.IsReadOnly, "Duzenle sonrasi Ogrenci NO yazilabilir olmali.");
            host.UpdateLayout();
            var readOnlyBackground = GetTemplateBackground(cardNoBox);
            var editableBackground = GetTemplateBackground(noBox);
            Assert.NotEqual(readOnlyBackground, editableBackground);
        });

    /// <summary>
    /// Kucuk 6 duzeltmesi: form icerigi (~620px) 1440x900'de kalan alani (~568px)
    /// asiyordu; eskiden ikisi de Auto/Auto/* satirlarda oldugu icin "*" satiri
    /// SIFIRA cokup dokuz detay sekmesi hic gorunmuyordu. Artik form ScrollViewer
    /// icinde kayar, sekmeler MinHeight'li sabit bir satirda -- bu test sekme
    /// alaninin GERCEKTEN olculebilir bir yuksekligi oldugunu dogrular.
    /// </summary>
    [Fact]
    public void DetaySekmeleriGorunurYukseklikAlir() =>
        UiThread.Run(() =>
        {
            var api = new FakeStudentApi();
            using var vm = MakeViewModel(api, ["students.read", "students.write"]);
            vm.OpenFullDetailCommand.Execute(SampleItem("Ada", "Katırcı", "1001", "CARD-1"));

            var view = new StudentsView { DataContext = vm };
            UiThread.ApplyResources(view);
            var host = new Border { Width = 1440, Height = 900, Child = view };
            host.Measure(new Size(1440, 900));
            host.Arrange(new Rect(0, 0, 1440, 900));
            host.UpdateLayout();

            var tabControl = Descendants(view).OfType<TabControl>().FirstOrDefault();
            Assert.NotNull(tabControl);
            Assert.True(tabControl!.ActualHeight >= 150,
                $"Detay sekmeleri alani cok kucuk: {tabControl.ActualHeight:F0}px. " +
                "Form icerigi tab alanini sifira cokertiyor olabilir.");
        });

    /// <summary>
    /// FieldWidthTests deseni: StudentsView formundaki metin kutulari, adres/not
    /// gibi uzun bir metni yazmaya yetecek genislikte olmali (>= 220px). Mevcut
    /// FieldWidthTests suiti yalnizca SettingsView'i olcuyordu; bu ekran icin
    /// hicbir genislik dogrulamasi yoktu.
    /// </summary>
    [Fact]
    public void FormAlanlariKullanilabilirGenislikte() =>
        UiThread.Run(() =>
        {
            const double usableWidth = 220;
            var api = new FakeStudentApi();
            using var vm = MakeViewModel(api, ["students.read", "students.write"]);
            vm.OpenFullDetailCommand.Execute(SampleItem("Ada", "Katırcı", "1001", "CARD-1"));

            var view = new StudentsView { DataContext = vm };
            UiThread.ApplyResources(view);
            var host = new Border { Width = 1440, Height = 900, Child = view };
            host.Measure(new Size(1440, 900));
            host.Arrange(new Rect(0, 0, 1440, 900));
            host.UpdateLayout();

            var formPanel = (FrameworkElement)view.FindName("StudentFormPanel")!;
            var narrow = Descendants(formPanel).OfType<TextBox>()
                .Where(box => box.ActualWidth > 0 && box.ActualWidth < usableWidth)
                .Select(box => $"{box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path ?? box.Name}: {box.ActualWidth:F0}px")
                .ToList();

            Assert.True(narrow.Count == 0,
                $"Ogrenci formunda {narrow.Count} kutu {usableWidth:F0}px'den dar:{Environment.NewLine}" +
                string.Join(Environment.NewLine, narrow));
        });

    private static StudentsViewModel MakeViewModel(FakeStudentApi api, IEnumerable<string> permissions) =>
        new(api, new ShellNavigationService([ShellRoutes.Students]), permissions);

    private static StudentListItem SampleItem(string first, string last, string no, string card) =>
        new(Guid.NewGuid(), no, card, first, last, "6A", "A", "Sayısal", "555-0000", true, 1, false, null);

    private static System.Windows.Media.Brush? GetTemplateBackground(TextBox box)
    {
        var border = Descendants(box).OfType<Border>().FirstOrDefault(b => b.Name == "bd");
        return border?.Background;
    }

    private static bool IsEffectivelyVisible(FrameworkElement element)
    {
        var current = (DependencyObject?)element;
        while (current is not null)
        {
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return element.ActualWidth > 0 && element.ActualHeight > 0;
    }

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

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Depo koku bulunamadi.");
    }

    private sealed class FakeStudentApi : IStudentApiClient
    {
        public Task<PagedResult<StudentListItem>> SearchAsync(StudentQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<StudentListItem>([], 1, 50, 0));

        private static StudentDetails Base(Guid id) => new(
            id, "1001", null, "Ada", "Katırcı", null, null, null, null, null,
            null, null, null, null, null, true, DateOnly.FromDateTime(DateTime.Today));

        public Task<StudentDetails> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Base(id));

        public Task<StudentDetails> SaveAsync(Guid? id, SaveStudentRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Base(id ?? Guid.NewGuid()) with
            {
                StudentNo = request.StudentNo, FirstName = request.FirstName, LastName = request.LastName,
                Notes = request.Notes, IsActive = request.IsActive
            });

        public Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<object>> LoadTabAsync(string tab, Guid studentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<object>>([]);

        public Task GiveLeaveAsync(CreateLeaveRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ReplaceCardAsync(Guid studentId, ReplaceCardRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
