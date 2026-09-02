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
            // Pasiflestir icin students.deactivate, Kart Degistir icin cards.manage gerekir.
            using var vm = MakeViewModel(api, ["students.read", "students.write", "students.deactivate", "cards.manage"]);
            vm.OpenFullDetailCommand.Execute(SampleItem("Ada", "Katırcı", "1001", "CARD-1"));

            var view = new StudentsView { DataContext = vm };
            UiThread.ApplyResources(view);
            var host = new Border { Width = 1440, Height = 900, Child = view };
            host.Measure(new Size(1440, 900));
            host.Arrange(new Rect(0, 0, 1440, 900));
            host.UpdateLayout();

            // Sekme seridi (sabit sirali ListBox) ve icerik alani ayri satirlardadir;
            // ikisi de olculebilir yukseklik almali. TabControl artik kullanilmiyor.
            var strip = (FrameworkElement)view.FindName("DetailTabStrip")!;
            var content = (FrameworkElement)view.FindName("DetailTabContent")!;
            Assert.True(strip.ActualHeight >= 24, $"Sekme seridi olculemedi: {strip.ActualHeight:F0}px.");
            Assert.True(content.ActualHeight >= 120,
                $"Detay sekme icerigi alani cok kucuk: {content.ActualHeight:F0}px. " +
                "Form icerigi sekme alanini sifira cokertiyor olabilir.");

            // Eylem dugmeleri kaymayan satirda: form ne kadar uzun olursa olsun gorunur olmali.
            foreach (var label in new[] { "Pasifleştir", "İzin Ver", "Kart Değiştir" })
            {
                var button = Descendants(view).OfType<Button>().FirstOrDefault(b => (b.Content as string) == label);
                Assert.NotNull(button);
                Assert.True(button!.ActualHeight > 0 && button.ActualWidth > 0, $"{label} dugmesi olculemedi.");
                var top = button.TransformToAncestor(host).Transform(new Point(0, 0)).Y;
                Assert.True(top + button.ActualHeight <= 900, $"{label} dugmesi pencerenin disinda ({top:F0}px).");
            }
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
            // 460px panelde kimlik satiri NO (96px) | Ad | Soyad uc sutundur (Tur 3: 900px
            // yukseklikte formu kaydirmadan sigdirmak icin bir satir kazanildi). En uzun
            // Turkce ad/soyad (HAŞLAMACI, SÖYLEMEZ) 13px yazi tipinde ~85px kaplar; ad/soyad
            // icin 140px, dort haneli numara icin 90px yeter. Adres/not gibi uzun metin
            // kutulari 180px altina inmemeli.
            const double usableWidth = 180;
            static double Floor(string path) => path switch
            {
                "FormStudentNo" => 90,
                "FormFirstName" or "FormLastName" => 140,
                _ => usableWidth,
            };
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
            // Salt okunur kisa degerler (Sinif "8B", Sube "B", Kart No, Veli Tel) iki sutunda
            // durur; kullanici onlara yazmaz, 220px kurali yalnizca yazilabilir kutular icindir.
            // Yeni kart numarasi kutusu da kasten kisadir (kart no 7-10 hane) ve dugmelerle
            // ayni satirda durur; 220px kurali ad/soyad/not gibi metin alanlari icindir.
            var narrow = Descendants(formPanel).OfType<TextBox>()
                .Where(box => box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path is { } path
                    && path.StartsWith("Form", StringComparison.Ordinal))
                .Where(box => box.ActualWidth > 0 && box.ActualWidth < Floor(box.GetBindingExpression(TextBox.TextProperty)!.ParentBinding.Path.Path))
                .Select(box => $"{box.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path ?? box.Name}: {box.ActualWidth:F0}px")
                .ToList();

            Assert.True(narrow.Count == 0,
                $"Ogrenci formunda {narrow.Count} kutu kullanilabilir genisligin altinda:{Environment.NewLine}" +
                string.Join(Environment.NewLine, narrow));
        });

    /// <summary>
    /// Gorev: liste sutunlari kesilmemeli. Once 12 sutunun sabit genislikleri toplami
    /// 911px idi ama liste sutunu 1440x900'de yalnizca ~745px; DataGrid hepsini orantili
    /// kucultunce "5001" -> "500'", "8350001" -> "835(", "Aktif" -> "Ak" oluyordu. Ayni
    /// ad-soyada sahip ogrencileri (iki ayri ALİ ÖZTÜRK) ayirt eden sutunlar tam da
    /// bunlardi.
    ///
    /// Test XAML metnini OKUMAZ: gercek gorsel agaci 1440x900'de olcup DataGrid'in
    /// ActualWidth toplamini mevcut alanla kiyaslar. Yalnizca "toplam sigiyor mu" demek
    /// yetmez -- WPF "*" sutunlari zaten kalan alani doldurur, o yuzden test asil SABIT
    /// genislikli sutunlarin ic metnini de olcer.
    /// </summary>
    [Fact]
    public void ListeSutunlariTasmadanSigar() =>
        UiThread.Run(() =>
        {
            var api = new FakeStudentApi();
            using var vm = MakeViewModel(api, ["students.read", "students.write"]);
            // Gercek veriden en uzun ornekler: 4 haneli no, 7 haneli kart, uzun Turkce isim.
            vm.Students.Add(SampleItem("HÜSEYİN", "HAŞLAMACI", "5001", "8350001"));
            vm.Students.Add(SampleItem("SÜMEYYE", "ÖZDEMİR", "5002", "8350002"));

            var view = new StudentsView { DataContext = vm };
            UiThread.ApplyResources(view);
            var host = new Border { Width = 1440, Height = 900, Child = view };
            host.Measure(new Size(1440, 900));
            host.Arrange(new Rect(0, 0, 1440, 900));
            host.UpdateLayout();

            var grid = (DataGrid)view.FindName("StudentsGrid")!;
            Assert.True(grid.ActualWidth > 0, "Ogrenci listesi olculemedi.");

            var totalColumns = grid.Columns.Sum(column => column.ActualWidth);
            Assert.True(totalColumns <= grid.ActualWidth + 1,
                $"Sutun genislikleri toplami {totalColumns:F0}px, kullanilabilir alan {grid.ActualWidth:F0}px: " +
                "liste yatay kayar ve sutunlar kesilir.");

            // Sabit genislikli her sutun, hem BASLIGINI hem de en uzun HUCRE metnini
            // hucre dolgusu (11+11px) ile birlikte kesilmeden tasiyabilmeli.
            const double cellPadding = 22;
            var tight = new List<string>();
            foreach (var (header, longest, fontSize) in new[]
            {
                ("NO", "5001", 13.0),
                ("SINIF", "6A", 13.0),
                ("ŞUBE", "A", 13.0),
                ("KART NO", "8350001", 13.0),
                ("DURUM", "Aktif", 11.0),
            })
            {
                var column = grid.Columns.Single(c => (string)c.Header == header);
                var needed = Math.Max(TextWidth(header, 11, FontWeights.SemiBold), TextWidth(longest, fontSize, FontWeights.Normal)) + cellPadding;
                if (column.ActualWidth < needed)
                    tight.Add($"{header}: {column.ActualWidth:F0}px < gereken {needed:F0}px (\"{longest}\")");
            }
            Assert.True(tight.Count == 0,
                $"{tight.Count} sutun icerigini kesiyor:{Environment.NewLine}{string.Join(Environment.NewLine, tight)}");

            // Kaldirilan sutunlar geri gelmemeli: geri donerlerse toplam yine tasar.
            foreach (var removed in new[] { "BÖLÜM", "VELİ TEL", "BUGÜNKÜ HAK", "BUGÜN GİRİŞ", "SON GİRİŞ" })
                Assert.DoesNotContain(grid.Columns, c => (string)c.Header == removed);
        });

    /// <summary>
    /// Gorev: listeden bir ogrenci SECILIR SECILMEZ sagdaki formun NO/Ad/Soyad kutulari
    /// o ogrencinin bilgileriyle dolmali. Once bu ucu yalnizca OpenEdit() dolduruyordu,
    /// yani "Duzenle" dugmesine basilana kadar form BOS kaliyordu.
    /// </summary>
    [Fact]
    public void OgrenciSecilinceFormDolar()
    {
        var api = new FakeStudentApi();
        using var vm = MakeViewModel(api, ["students.read", "students.write"]);
        var elif = SampleItem("ELİF", "ÇETİN", "5003", "8350003");

        vm.SelectedStudent = elif;

        Assert.Equal("5003", vm.FormStudentNo);
        Assert.Equal("ELİF", vm.FormFirstName);
        Assert.Equal("ÇETİN", vm.FormLastName);
        // Form salt okunur kalmali: dolmak duzenlemeye izin vermek DEGILDIR.
        Assert.False(vm.IsFormOpen);
    }

    /// <summary>
    /// Baska bir ogrenciye gecilince form ESKI ogrenciyi tasimamali; secim kalkinca
    /// (null) form temizlenmeli. Aksi halde panel yanlis ogrenciyi gosterir.
    /// </summary>
    [Fact]
    public void SecimDegisinceFormTakipEder()
    {
        var api = new FakeStudentApi();
        using var vm = MakeViewModel(api, ["students.read", "students.write"]);

        vm.SelectedStudent = SampleItem("ELİF", "ÇETİN", "5003", "8350003");
        vm.SelectedStudent = SampleItem("ALİ", "ÖZTÜRK", "5004", "8350004");

        Assert.Equal("5004", vm.FormStudentNo);
        Assert.Equal("ALİ", vm.FormFirstName);
        Assert.Equal("ÖZTÜRK", vm.FormLastName);

        vm.SelectedStudent = null;
        Assert.Equal("", vm.FormStudentNo);
        Assert.Equal("", vm.FormFirstName);
        Assert.Equal("", vm.FormLastName);
    }

    /// <summary>
    /// "Yeni Ogrenci" akisi BOZULMAMALI: bir ogrenci secili iken Yeni Ogrenci'ye
    /// basilinca form BOS baslamali (OpenCreate SelectedStudent'a dokunmaz, o yuzden
    /// secimden doldurma yeni kaydin uzerine yazmamali).
    /// </summary>
    [Fact]
    public void YeniOgrenciFormuBosBaslar()
    {
        var api = new FakeStudentApi();
        using var vm = MakeViewModel(api, ["students.read", "students.write"]);
        vm.SelectedStudent = SampleItem("ELİF", "ÇETİN", "5003", "8350003");

        vm.NewStudentCommand.Execute(null);

        Assert.Equal("", vm.FormStudentNo);
        Assert.Equal("", vm.FormFirstName);
        Assert.Equal("", vm.FormLastName);
        Assert.True(vm.IsFormOpen);
    }

    /// <summary>
    /// SameStudent korumasi bozulmamali: Details ile SelectedStudent farkli ogrencileri
    /// gosterirken salt okunur blok gizli kalmali. Form artik secimle doldugu icin bu
    /// korumanin hala calistigini ayrica dogrulanir.
    /// </summary>
    [Fact]
    public void FormDolarkenSameStudentKorumasiSurer() =>
        UiThread.Run(() =>
        {
            var api = new FakeStudentApi();
            using var vm = MakeViewModel(api, ["students.read", "students.write"]);
            var a = SampleItem("ELİF", "ÇETİN", "5003", "8350003");
            var b = SampleItem("ALİ", "ÖZTÜRK", "5004", "8350004");

            vm.OpenFullDetailCommand.Execute(a);
            vm.SelectedStudent = b; // Details hala A'yi tasir.
            Assert.Equal("5004", vm.FormStudentNo); // Form yeni secimi gosterir.

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
                "Form secimle dolduruldu ama SameStudent korumasi bozuldu.");
        });

    /// <summary>Bir metnin Segoe UI ile kaplayacagi gercek genislik (px).</summary>
    private static double TextWidth(string text, double fontSize, FontWeight weight)
    {
        var typeface = new System.Windows.Media.Typeface(
            new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
        return new System.Windows.Media.FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, fontSize, System.Windows.Media.Brushes.Black, 1.0)
            .WidthIncludingTrailingWhitespace;
    }

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
