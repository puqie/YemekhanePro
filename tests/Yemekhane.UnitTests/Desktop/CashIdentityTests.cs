using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Controls;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Kasa ekraninda yikici eylemin dogru yerde, dogru stilde oldugunu ve iptal
/// onayinin AYNI ISIMLI ogrencileri ayirt ettigini dogrular.
///
/// Guvenlik acigi: VoidConfirmationText yalnizca "TUTAR - AD SOYAD" gosteriyordu.
/// Veritabaninda ayni isimden birden fazla ogrenci var (Ada Katirci, Ada
/// Haslamaci, Ada Soylemez gibi) -- operator kimin parasini iptal ettigini
/// ayirt edemiyordu. VoidConfirmationText DONDURULMUS bir hesaplanmis ozellik
/// oldugu icin degistirilemez; bunun yerine onay paneli SelectedTransaction'in
/// kendi alanlarina (tutar, ogrenci adi, KART NUMARASI, islem tarihi/saati)
/// dogrudan baglanir.
///
/// Once "Secili Islemi Iptal Et" dugmesi sayfanin ORTASINDA, sayfalama
/// dugmelerinin yaninda ve notr (Action) stildeydi. Yikici bir eylem
/// beklenmedik bir konumda ve navigasyon kontrolleri arasinda duruyordu.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class CashIdentityTests
{
    /// <summary>
    /// Duzeltme turu son, Onemli 2: OpenVoidCommand XAML'de IKI kez gecer --
    /// arac cubugundaki dugme (satir 52) VE izgaranin Enter/Delete
    /// KeyBinding'leri (satir 55). Eski test IndexOf/LastIndexOf ile ham
    /// XAML metnini dilimliyordu; XAML sirasi degisirse veya KeyBinding
    /// dugmeden ONCE gelirse LastIndexOf("&lt;Button", ...) ALAKASIZ bir
    /// dugmeye geri yururdu ve Destructive iddiasi SESSIZCE yanlis elemani
    /// test ederdi. Bunun yerine gorsel agacta GERCEK dugme,
    /// ReferenceEquals(b.Command, vm.OpenVoidCommand) ile bulunur --
    /// EklemeAcikkenIptalYollariDevreDisi... testinin kullandigi ayni desen.
    /// </summary>
    [Fact]
    public void IptalDugmesiYikiciStilTasir() =>
        UiThread.Run(() =>
        {
            var api = new FakeCashApi();
            var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
            vm.InitializeAsync().GetAwaiter().GetResult();

            var view = new CashView { DataContext = vm };
            Host(view);

            var toolbarVoidButton = Descendants(view).OfType<Button>()
                .First(b => ReferenceEquals(b.Command, vm.OpenVoidCommand));

            var destructiveStyle = (Style)view.TryFindResource("Destructive")!;
            Assert.Same(destructiveStyle, toolbarVoidButton.Style);
        });

    [Fact]
    public void IptalDugmesiSayfaOrtasindaDegil() =>
        UiThread.Run(() =>
        {
            var api = new FakeCashApi();
            var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
            vm.InitializeAsync().GetAwaiter().GetResult();

            var view = new CashView { DataContext = vm };
            Host(view);

            var toolbarVoidButton = Descendants(view).OfType<Button>()
                .First(b => ReferenceEquals(b.Command, vm.OpenVoidCommand));

            Assert.NotEqual(HorizontalAlignment.Center, toolbarVoidButton.HorizontalAlignment);
        });

    /// <summary>
    /// Asil guvenlik dogrulamasi: iptal onay panelinin KENDISINDE (izgara
    /// hucrelerinde degil -- KART sutunu zaten kart numarasini gosterir ve
    /// yanlislikla o hucreyi yakalayip testi anlamsizlastirabilir) kart
    /// numarasi gercekten gorseldir. Panel x:Name="VoidPanel" ile aranir ve
    /// yalnizca ONUN soyu taranir.
    /// </summary>
    [Fact]
    public void IptalPaneliKartNumarasiniGosterir() =>
        UiThread.Run(() =>
        {
            var api = new FakeCashApi();
            var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
            vm.InitializeAsync().GetAwaiter().GetResult();
            var transaction = vm.Transactions[0];
            Assert.Equal("CARD-7788", transaction.CardNumber);
            vm.SelectedTransaction = transaction;
            vm.OpenVoidCommand.Execute(null);
            Assert.True(vm.IsVoidOpen);

            var view = new CashView { DataContext = vm };
            var host = Host(view);

            var panel = (FrameworkElement)view.FindName("VoidPanel")!;
            var texts = Descendants(panel).OfType<TextBlock>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            Assert.Contains(texts, t => t!.Contains("CARD-7788", StringComparison.Ordinal));
            // Ayni isimli baska bir ogrenciyle karistirilmamasi icin isim de
            // ayni blokta gorunmeli.
            Assert.Contains(texts, t => t!.Contains("Ada Katırcı", StringComparison.Ordinal));
        });

    /// <summary>
    /// Ogrencisiz islem (StudentId/StudentName null) durumunda cokme veya
    /// bos metin degil, aciklayici bir metin gosterilmeli -- hem ogrenci
    /// adi hem kart numarasi icin (CardNumber de bu durumda null; XAML'deki
    /// TargetNullValue=Kart: - yanlislikla bozulursa bunu yakalayacak tek
    /// test budur). Kardesi olan IptalPaneliKartNumarasiniGosterir gibi
    /// panel x:Name="VoidPanel" ile izole edilir -- Descendants(view)
    /// kullanmak, izgara veya baska bir yerde ayni metin gecerse sessizce
    /// yanlis-yesil verebilir.
    /// </summary>
    [Fact]
    public void IptalPaneliOgrencisizIslemiGosterir() =>
        UiThread.Run(() =>
        {
            var api = new FakeCashApi { StudentlessTransaction = true };
            var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
            vm.InitializeAsync().GetAwaiter().GetResult();
            var transaction = vm.Transactions[0];
            Assert.Null(transaction.StudentName);
            Assert.Null(transaction.CardNumber);
            vm.SelectedTransaction = transaction;
            vm.OpenVoidCommand.Execute(null);

            var view = new CashView { DataContext = vm };
            Host(view);

            var panel = (FrameworkElement)view.FindName("VoidPanel")!;
            var texts = Descendants(panel).OfType<TextBlock>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            Assert.Contains(texts, t => t!.Contains("Öğrencisiz işlem", StringComparison.Ordinal));
            Assert.Contains(texts, t => t!.Contains("Kart: -", StringComparison.Ordinal));
        });

    /// <summary>
    /// Divergence analizi: LookupStudent (dogrulanmis) ile StudentNumber/
    /// LookupCardNumber (yazilan metin) FARKLI ogrenciyi gosteremez, cunku
    /// CashViewModel'de StudentNumber/LookupCardNumber setter'lari
    /// LookupStudent'i null yapar (satir 80-81). Bu test bunu KOD SEVIYESINDE
    /// degil GERCEK VIEWMODEL DAVRANISI uzerinden kanitlar: dogrulanmis bir
    /// ogrenci varken kutuya yazi yazilirsa LookupStudent hemen null olur --
    /// yani ekranda iki farkli ogrencinin AYNI ANDA gorunmesi mumkun degildir.
    /// </summary>
    [Fact]
    public async Task DogrulanmisOgrenciYaziyaBasinincaTemizlenir()
    {
        var api = new FakeCashApi();
        var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
        await vm.InitializeAsync();
        vm.OpenAddCommand.Execute(null);
        vm.StudentNumber = "1001";
        vm.LookupStudentCommand.Execute(null);
        await UntilAsync(() => vm.LookupStudent is not null);

        vm.StudentNumber = "1002";
        Assert.Null(vm.LookupStudent);

        vm.StudentNumber = null;
        vm.LookupCardNumber = "1001";
        vm.LookupStudentCommand.Execute(null);
        await UntilAsync(() => vm.LookupStudent is not null);

        vm.LookupCardNumber = "9999";
        Assert.Null(vm.LookupStudent);
    }

    /// <summary>
    /// ViewModel gercegi: OpenAdd (satir 188-193) ve OpenVoid (satir 252-256)
    /// birbirinin bayragina DOKUNMAZ -- IsAddOpen ve IsVoidOpen'in setter'lari
    /// private ve View bunlari VIEWMODEL DEGISTIRMEDEN birbirine baglayamaz.
    /// Bu test, ikisinin GERCEKTEN ayni anda true olabildigini (View'in bunu
    /// engellemesi gerektigini) belgeler -- ViewModel bunu KENDI BASINA
    /// engellemiyor.
    /// </summary>
    [Fact]
    public async Task ViewModelIkiCekmeceyiKendiBasinaAyniAndaAcikBirakabilir()
    {
        var api = new FakeCashApi();
        var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
        await vm.InitializeAsync();

        vm.OpenAddCommand.Execute(null);
        vm.SelectedTransaction = vm.Transactions[0];
        vm.OpenVoidCommand.Execute(null);

        Assert.True(vm.IsAddOpen);
        Assert.True(vm.IsVoidOpen);
    }

    /// <summary>
    /// Bu yuzden erisilebilirlik View'de kesilir. OpenVoidCommand'a giden IKI
    /// ayri kontrol var -- izgaranin Enter/Delete KeyBinding'leri VE arac
    /// cubugundaki "Secili Islemi Iptal Et" dugmesi (ikincisi izgaranin
    /// KARDESI, alt agaci degil; izgarayi devre disi birakmak onu ETKILEMEZ).
    /// Uc kontrolun ucu de ayri ayri kapatilmali:
    /// - izgara (ve Enter/Delete): ekleme cekmecesi acikken devre disi
    /// - arac cubugundaki iptal dugmesi: ekleme cekmecesi acikken devre disi
    /// - "Gelir Ekle" dugmesi: iptal cekmecesi acikken devre disi
    /// Boylece VIEWMODEL DEGISTIRILMEDEN iki cekmecenin AYNI ANDA EKRANDA
    /// GORUNMESI engellenir -- IsAddOpen/IsVoidOpen teorik olarak ikisi de
    /// true olsa bile kullanici ikinciyi tetikleyecek hicbir yola erisemez.
    /// </summary>
    [Fact]
    public void EklemeAcikkenIptalYollariDevreDisiIptalAcikkenEklemeDugmesiDevreDisi() =>
        UiThread.Run(() =>
        {
            var api = new FakeCashApi();
            var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
            vm.InitializeAsync().GetAwaiter().GetResult();

            var view = new CashView { DataContext = vm };
            Host(view);

            var grid = (DataGrid)view.FindName("TransactionsGrid")!;
            var addButton = Descendants(view).OfType<Button>()
                .First(b => ReferenceEquals(b.Command, vm.OpenAddCommand));
            var toolbarVoidButton = Descendants(view).OfType<Button>()
                .First(b => ReferenceEquals(b.Command, vm.OpenVoidCommand));

            vm.SelectedTransaction = vm.Transactions[0];
            vm.OpenAddCommand.Execute(null);
            Assert.False(grid.IsEnabled, "Ekleme cekmecesi acikken izgara hala etkin -- Enter/Delete ile ikinci cekmece acilabilir.");
            Assert.False(toolbarVoidButton.IsEnabled, "Ekleme cekmecesi acikken arac cubugundaki Iptal Et dugmesi hala etkin -- izgaranin KARDESI oldugu icin izgarayi kapatmak onu etkilemez.");

            vm.CloseAddCommand.Execute(null);
            vm.OpenVoidCommand.Execute(null);
            Assert.False(addButton.IsEnabled, "Iptal cekmecesi acikken Gelir Ekle dugmesi hala etkin.");
        });

    [Fact]
    public void EklemeVeIptalCekmeceleriDrawerKontroluKullanir()
    {
        var xaml = CashXaml();
        Assert.Contains("controls:Drawer", xaml);
        Assert.Contains("IsOpen=\"{Binding IsAddOpen}\"", xaml);
        Assert.Contains("IsOpen=\"{Binding IsVoidOpen}\"", xaml);
        Assert.Contains("DrawerWidth=\"400\"", xaml);
    }

    [Fact]
    public void DuzenlemeNotuFooterdeCaptionStiliyle()
    {
        var xaml = CashXaml();
        var index = xaml.IndexOf("Düzenleme ve silme desteklenmez.", StringComparison.Ordinal);
        Assert.True(index >= 0, "Not metni bulunamadi.");

        var start = xaml.LastIndexOf("<TextBlock", index, StringComparison.Ordinal);
        var end = xaml.IndexOf("/>", index, StringComparison.Ordinal);
        var block = xaml[start..end];

        Assert.Contains("Caption", block);
    }

    /// <summary>Onay kutulari uyari renginde bir zeminle vurgulanmali.</summary>
    [Fact]
    public void OnayKutulariUyariZemininde() =>
        UiThread.Run(() =>
        {
            var api = new FakeCashApi();
            var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.OpenAddCommand.Execute(null);
            vm.SelectedTransaction = vm.Transactions[0];

            var view = new CashView { DataContext = vm };
            Host(view);

            var addConfirm = Descendants(view).OfType<CheckBox>()
                .FirstOrDefault(c => c.GetBindingExpression(ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == "AddConfirmed");
            Assert.NotNull(addConfirm);

            // Uyari zeminini tasiyan bir ata Border bulunmali (dogrudan CheckBox'in
            // kendisi degil -- WarningSoftBrush arka plan olarak bir kapsayicida).
            var hasWarningAncestor = Ancestors(addConfirm!).OfType<Border>()
                .Any(b => b.Background is System.Windows.Media.SolidColorBrush brush && IsWarningSoft(brush));
            Assert.True(hasWarningAncestor, "Ekleme onay kutusu uyari renginde bir zeminde degil.");
        });

    /// <summary>Onay kutulari isaretlenmeden kaydet/iptal dugmeleri GERCEKTEN pasif okunmali.</summary>
    [Fact]
    public void OnaylanmamisIslemdeDugmelerPasifGorunur() =>
        UiThread.Run(() =>
        {
            var api = new FakeCashApi();
            var vm = new CashViewModel(api, ["cash.read", "cash.write"]);
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.OpenAddCommand.Execute(null);
            vm.SelectedTransaction = vm.Transactions[0];
            vm.OpenVoidCommand.Execute(null);

            var view = new CashView { DataContext = vm };
            Host(view);

            var addButton = Descendants(view).OfType<Button>()
                .FirstOrDefault(b => ReferenceEquals(b.Command, vm.AddCommand));
            var voidButton = Descendants(view).OfType<Button>()
                .FirstOrDefault(b => ReferenceEquals(b.Command, vm.VoidCommand));

            Assert.NotNull(addButton);
            Assert.NotNull(voidButton);
            Assert.False(addButton!.IsEnabled, "AddConfirmed isaretlenmeden Kaydet etkin gorunuyor.");
            Assert.False(voidButton!.IsEnabled, "VoidConfirmed/VoidReason bossa Iptal Et etkin gorunuyor.");
        });

    private static bool IsWarningSoft(System.Windows.Media.SolidColorBrush brush) =>
        brush.Color == System.Windows.Media.Color.FromRgb(0xFD, 0xF3, 0xE3);

    private static Border Host(UserControl view)
    {
        UiThread.ApplyResources(view);
        var host = new Border { Width = 1440, Height = 900, Child = view };
        host.Measure(new Size(1440, 900));
        host.Arrange(new Rect(0, 0, 1440, 900));
        host.UpdateLayout();
        return host;
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 100 && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate());
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

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject node)
    {
        var current = System.Windows.Media.VisualTreeHelper.GetParent(node);
        while (current is not null)
        {
            yield return current;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
    }

    private static string CashXaml() => File.ReadAllText(Path.Combine(
        RepositoryRoot(), "src", "Yemekhane.Desktop", "Views", "CashView.xaml"));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Depo koku bulunamadi.");
    }

    private sealed class FakeCashApi : ICashApiClient
    {
        private readonly Guid typeId = Guid.NewGuid();
        private readonly Guid studentId = Guid.NewGuid();
        public bool StudentlessTransaction { get; init; }

        public Task<CashSummary> SummaryAsync(CashSummaryPeriod period, DateOnly? anchorDate = null, DateOnly? startDate = null, DateOnly? endDate = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CashSummary(period, startDate ?? anchorDate ?? new DateOnly(2026, 8, 31), endDate ?? anchorDate ?? new DateOnly(2026, 8, 31), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 10m, 1, 0, 0, [new(typeId, "Nakit", 10m, 1)]));

        public Task<PagedResult<IncomeTransactionDetails>> TransactionsAsync(IncomeTransactionFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<IncomeTransactionDetails>([Transaction()], filter.Page, filter.PageSize, 1));

        public Task<IReadOnlyList<IncomeTypeDetails>> TypesAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<IncomeTypeDetails>>([new(typeId, "Nakit", true)]);

        public Task<IncomeTransactionDetails> AddAsync(CreateIncomeTransactionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(Transaction());

        public Task<IncomeTransactionDetails> VoidAsync(Guid id, string reason, CancellationToken cancellationToken = default) =>
            Task.FromResult(Transaction() with { IsVoided = true, VoidReason = reason });

        public Task<IncomeTypeDetails> SaveTypeAsync(Guid? id, SaveIncomeTypeRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IncomeTypeDetails(id ?? Guid.NewGuid(), request.Name, request.IsActive));

        public Task DeactivateTypeAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PagedResult<StudentListItem>> FindStudentAsync(string? studentNumber, string? cardNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<StudentListItem>([new(studentId, "1001", "CARD-7788", "Ada", "Katırcı", null, null, null, null, true, 0, false, null)], 1, 2, 1));

        private IncomeTransactionDetails Transaction() => StudentlessTransaction
            ? new(Guid.NewGuid(), Guid.NewGuid(), null, null, null, DateTimeOffset.UtcNow, typeId, "Nakit", 10m, null, Guid.NewGuid(), false, null, null, null)
            : new(Guid.NewGuid(), Guid.NewGuid(), studentId, "Ada Katırcı", "CARD-7788", DateTimeOffset.UtcNow, typeId, "Nakit", 10m, null, Guid.NewGuid(), false, null, null, null);
    }
}
