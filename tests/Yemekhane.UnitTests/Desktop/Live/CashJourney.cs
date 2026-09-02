using System.Net.Http;
using System.Net.Http.Json;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Desktop.ViewModels;
using Xunit;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Kasa ekraninin CANLI API uzerinden uctan uca yolculugu: ozet kartlari, filtreler,
/// gelir ekleme, iptal, gunluk kasa, gelir turleri ve rapor gecisi. Her adimda
/// ViewModel durumu API'nin kendi yanitiyla karsilastirilir; ekran goruntuleri
/// YP_SHOT_DIR'a yazilir. YP_LIVE_API yoksa sessizce gecer.
/// </summary>
[Collection("UI")]
public class CashJourney
{
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public void OzetKartlariVeFiltreler() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("cash");
        var vm = ui.Cash;
        Assert.False(vm.HasError, vm.ErrorMessage);

        // 1) Ust kartlar API ile birebir.
        var daily = Get<CashSummary>(ui, "api/cash/summary?period=Daily");
        var weekly = Get<CashSummary>(ui, "api/cash/summary?period=IsoWeek");
        var monthly = Get<CashSummary>(ui, "api/cash/summary?period=Monthly");
        Assert.Equal(daily.TotalAmount, vm.DailyTotal);
        Assert.Equal(daily.TransactionCount, vm.Daily!.TransactionCount);
        Assert.Equal(weekly.TotalAmount, vm.WeeklyTotal);
        Assert.Equal(monthly.TotalAmount, vm.MonthlyTotal);
        ui.Note($"Kartlar: bugun {vm.DailyTotal} ({vm.Daily.TransactionCount}), hafta {vm.WeeklyTotal}, ay {vm.MonthlyTotal}");
        ui.Shot("cash-01-genel");

        // Para bicimi: ekranda TL simgesi ve Turkce ayirici olmali.
        var moneyTexts = ui.FindAll<TextBlock>().Select(t => t.Text).Where(t => t.Contains('₺')).ToList();
        Assert.NotEmpty(moneyTexts);
        Assert.Contains(moneyTexts, t => t.Contains(",00"));
        ui.Note("Para metinleri: " + string.Join(" | ", moneyTexts.Take(6)));

        // Kesik sutun kontrolu: her hucrenin metni hucre genisligine sigmali.
        var grid = ui.FindAll<DataGrid>().First(g => g.Name == "TransactionsGrid");
        AssertNoClippedCells(ui, grid);

        // 2) Iptal durumu filtresi.
        var voidedApi = Get<PagedResult<IncomeTransactionDetails>>(ui, "api/income/transactions?isVoided=true&pageSize=1");
        vm.FilterIsVoided = true; vm.FilterFrom = new DateTime(2026, 1, 1); vm.FilterTo = new DateTime(2026, 12, 31);
        Execute(ui, vm.ApplyFiltersCommand);
        Assert.Equal(voidedApi.TotalCount, vm.TotalCount);
        Assert.All(vm.Transactions, t => Assert.True(t.IsVoided));
        Assert.All(vm.Transactions, t => Assert.False(string.IsNullOrWhiteSpace(t.VoidReason)));
        ui.Shot("cash-02-filtre-iptal");

        // Aktif filtresi.
        vm.FilterIsVoided = false; Execute(ui, vm.ApplyFiltersCommand);
        Assert.All(vm.Transactions, t => Assert.False(t.IsVoided));

        // Gelir turu filtresi.
        vm.FilterIsVoided = null;
        var type = vm.IncomeTypes[0];
        Assert.Equal("Tümü", vm.SelectedFilterType?.Name);
        vm.SelectedFilterType = vm.FilterTypeOptions.Single(o => o.Id == type.Id); Execute(ui, vm.ApplyFiltersCommand);
        var typeApi = Get<PagedResult<IncomeTransactionDetails>>(ui, $"api/income/transactions?incomeTypeId={type.Id}&pageSize=1");
        Assert.Equal(typeApi.TotalCount, vm.TotalCount);
        Assert.All(vm.Transactions, t => Assert.Equal(type.Name, t.IncomeTypeName));
        vm.SelectedFilterType = IncomeTypeOption.All;

        // Kart no filtresi.
        var anyCard = ui.Cash.Transactions.First(t => t.CardNumber is not null).CardNumber!;
        vm.FilterCardNumber = anyCard; Execute(ui, vm.ApplyFiltersCommand);
        Assert.True(vm.TotalCount >= 1);
        Assert.All(vm.Transactions, t => Assert.Equal(anyCard, t.CardNumber));
        vm.FilterCardNumber = null;

        // Ogrenci no filtresi (Ogrenciyi Bul -> Filtrele).
        vm.FilterStudentNumber = "5001"; Execute(ui, vm.LookupFilterStudentCommand);
        Assert.NotNull(vm.FilterStudent);
        Assert.Contains("5001", vm.FilterStudentText);
        Execute(ui, vm.ApplyFiltersCommand);
        Assert.All(vm.Transactions, t => Assert.Equal(vm.FilterStudent!.Id, t.StudentId));
        ui.Note($"Ogrenci filtresi 5001: {vm.TotalCount} kayit; metin: {vm.FilterStudentText}");
        ui.Shot("cash-03-filtre-ogrenci");

        // Olmayan ogrenci no -> Turkce hata.
        vm.FilterStudentNumber = "999999"; Execute(ui, vm.LookupFilterStudentCommand);
        Assert.Null(vm.FilterStudent);
        Assert.True(vm.HasError, "olmayan ogrenci icin hata yok");
        ui.Note("Olmayan ogrenci hatasi: " + vm.ErrorMessage);

        // Sayfalama.
        vm.FilterStudentNumber = null; vm.FilterIsVoided = null; vm.FilterCardNumber = null;
        Execute(ui, vm.ApplyFiltersCommand);
        Assert.False(vm.HasError, vm.ErrorMessage);
        var total = vm.TotalCount; var firstPageFirst = vm.Transactions[0].Id;
        Assert.True(total > vm.PageSize, $"sayfalama icin yeterli kayit yok: {total}");
        Execute(ui, vm.NextPageCommand);
        Assert.Equal(2, vm.Page);
        Assert.NotEqual(firstPageFirst, vm.Transactions[0].Id);
        Assert.Equal(total, vm.TotalCount);
        Execute(ui, vm.PreviousPageCommand);
        Assert.Equal(1, vm.Page);
        Assert.Equal(firstPageFirst, vm.Transactions[0].Id);
        ui.Note("Sayfa metni: " + vm.PageText);

        // Tarih filtresi: yalnizca bugun -> bugunun aktif + iptal sayisi.
        var today = DateTime.Today;
        vm.FilterFrom = today; vm.FilterTo = today; Execute(ui, vm.ApplyFiltersCommand);
        Assert.Equal(daily.TransactionCount + daily.VoidedCount, vm.TotalCount);
        ui.Shot("cash-04-filtre-bugun");
    });

    [Fact]
    public void GelirEkleVeIptal() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("cash");
        var vm = ui.Cash;
        var before = Get<CashSummary>(ui, "api/cash/summary?period=Daily");
        var countBefore = vm.TotalCount;

        // 3) Cekmece ilk acilista hata gostermemeli.
        vm.OpenAddCommand.Execute(null); ui.Pump();
        Assert.True(vm.IsAddOpen);
        Assert.Null(vm.AddError);
        ui.Shot("cash-10-ekle-bos");
        var drawerTexts = DrawerTexts(ui, "AddPanel");
        Assert.DoesNotContain(drawerTexts, t => t.Contains("doğrulanmadı", StringComparison.OrdinalIgnoreCase));

        // Ayni adli ogrenciler: 5016 ve 5252 ikisi de ADA AKGUN. Dogrulama sonucu no + sinif/sube + kart gostermeli.
        vm.StudentNumber = "5252"; Execute(ui, vm.LookupStudentCommand);
        Assert.NotNull(vm.LookupStudent);
        Assert.Equal("5252", vm.LookupStudent!.StudentNo);
        Assert.Contains("5252", vm.LookupStudentText);
        // Sinif/sube sabit yazilmaz: tohum deterministik olsa da kimlik metninin API'deki
        // gercek sinifi tasimasi dogrulanan sey. (Once "6C" sabitti ve baska bir
        // veritabaninda 7B cikinca test rastgele dusuyordu.)
        Assert.False(string.IsNullOrWhiteSpace(vm.LookupStudent.ClassName), "sinif bos");
        Assert.Contains(vm.LookupStudent.ClassName!, vm.LookupStudentText);
        ui.Note("Dogrulama metni (kartsiz): " + vm.LookupStudentText);
        vm.StudentNumber = "5016"; Execute(ui, vm.LookupStudentCommand);
        Assert.Equal("5016", vm.LookupStudent!.StudentNo);
        Assert.Contains("8350016", vm.LookupStudentText);
        Assert.Contains(vm.LookupStudent.ClassName!, vm.LookupStudentText);
        ui.Note("Dogrulama metni: " + vm.LookupStudentText);
        ui.Shot("cash-11-ekle-dogrulandi");

        // Kart no ile dogrulama. Kart numarasi SABIT YAZILMAZ: ayni veritabaninda kosan
        // otomatik SMS yolculugu kart yenileme akisini surdugu icin 5001'in karti degisebilir;
        // sabit "8350001" o durumda pasif kart olur ve dogrulama bos doner (kosu sirasina
        // bagli hata, urun hatasi degil). Ogrencinin O ANKI aktif karti okunur.
        using (var cardDb = LiveDb.Open())
        {
            var activeCard = cardDb.Text(
                "SELECT c.card_number FROM student_cards c JOIN students s ON s.Id = c.StudentId " +
                "WHERE s.student_no = '5001' AND c.IsActive = 1");
            Assert.False(string.IsNullOrWhiteSpace(activeCard), "5001 icin aktif kart yok");
            vm.StudentNumber = null; vm.LookupCardNumber = activeCard; Execute(ui, vm.LookupStudentCommand);
        }
        Assert.NotNull(vm.LookupStudent); Assert.Equal("5001", vm.LookupStudent!.StudentNo);

        // Olmayan no -> Turkce hata.
        vm.LookupCardNumber = null; vm.StudentNumber = "999999"; Execute(ui, vm.LookupStudentCommand);
        Assert.Null(vm.LookupStudent); Assert.NotNull(vm.AddError);
        ui.Note("Olmayan no hatasi: " + vm.AddError);
        // Pasif ogrenci (5000) -> bulunamamali.
        vm.StudentNumber = "5000"; Execute(ui, vm.LookupStudentCommand);
        Assert.Null(vm.LookupStudent);

        // Tutar bicimleri.
        vm.StudentNumber = "5016"; Execute(ui, vm.LookupStudentCommand); Assert.NotNull(vm.LookupStudent);
        vm.AddConfirmed = true;
        foreach (var bad in new[] { "abc", "-5", "0", "", "12,345" })
        { vm.AmountText = bad; Assert.NotNull(vm.ValidateAdd()); }
        vm.AmountText = "1.250,50"; Assert.Null(vm.ValidateAdd());
        vm.AmountText = "1250,50"; Assert.Null(vm.ValidateAdd());
        vm.AmountText = "1250.50"; Assert.Null(vm.ValidateAdd());

        // Kaydet: 1.250,50 bugun. Aciklama her kosuda benzersiz: veritabani kosular arasinda kalici.
        var marker = "Canlı yolculuk testi " + DateTime.Now.ToString("HHmmss");
        vm.AmountText = "1.250,50"; vm.Description = marker;
        vm.AddCommand.Execute(null); vm.AddCommand.Execute(null); // cift tik
        ui.Delay(2500); ui.Pump();
        Assert.Null(vm.AddError);
        Assert.False(vm.IsAddOpen, "cekmece kapanmadi");
        // Tohum verisinde bugun 13:00'e kadar (gelecek saatli) islemler var; liste islem saatine gore
        // sirali oldugundan "simdi" kaydedilen satir ilk sirada olmayabilir. Kayit sayfada olmali ve
        // sayfa islem saatine gore azalan sirada kalmali.
        var first = vm.Transactions.Single(t => t.Description == marker);
        Assert.True(vm.Transactions.Zip(vm.Transactions.Skip(1)).All(p => p.First.TransactionAt >= p.Second.TransactionAt), "liste islem saatine gore sirali degil");
        Assert.Equal("8350016", first.CardNumber);
        Assert.Equal(countBefore + 1, vm.TotalCount);
        Assert.Equal(before.TotalAmount + 1250.50m, vm.DailyTotal);
        Assert.Equal(before.TransactionCount + 1, vm.Daily!.TransactionCount);
        var afterAdd = Get<CashSummary>(ui, "api/cash/summary?period=Daily");
        Assert.Equal(afterAdd.TotalAmount, vm.DailyTotal);
        ui.Shot("cash-12-ekle-sonrasi");

        // Form temiz mi? Yeniden acinca eski deger kalmamali.
        vm.OpenAddCommand.Execute(null); ui.Pump();
        Assert.Equal("", vm.AmountText); Assert.Null(vm.Description); Assert.Null(vm.LookupStudent); Assert.False(vm.AddConfirmed);
        vm.CloseAddCommand.Execute(null);

        // 23:30 sinir testi: dun 23:30 -> dune sayilmali, bugune degil (UTC'de 20:30, gun sinirinin bu yaninda).
        var yesterday = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)).ToString("yyyy-MM-dd");
        var yesterdayBefore = Get<CashSummary>(ui, $"api/cash/summary?period=Daily&date={yesterday}");
        vm.OpenAddCommand.Execute(null); ui.Pump();
        vm.StudentNumber = "5001"; Execute(ui, vm.LookupStudentCommand); Assert.NotNull(vm.LookupStudent);
        vm.AddDate = DateTime.Today.AddDays(-1); vm.TransactionTime = "23:30"; vm.AmountText = "10,00"; vm.AddConfirmed = true;
        Execute(ui, vm.AddCommand);
        Assert.Null(vm.AddError); Assert.False(vm.IsAddOpen);
        var yesterdaySummary = Get<CashSummary>(ui, $"api/cash/summary?period=Daily&date={yesterday}");
        Assert.Equal(afterAdd.TotalAmount, vm.DailyTotal); // bugun degismedi
        Assert.Equal(yesterdayBefore.TotalAmount + 10m, yesterdaySummary.TotalAmount);
        // Varsayilan tarih filtresi bugun oldugu icin dunku kayit listede gorunmez; tarih filtresini genisletince gorunmeli.
        vm.FilterFrom = DateTime.Today.AddDays(-1); Execute(ui, vm.ApplyFiltersCommand);
        var late = vm.Transactions.First(t => t.Amount == 10m && t.TransactionAt.Hour == 23 && t.TransactionAt.Minute == 30);
        Assert.Equal(DateTime.Today.AddDays(-1), late.TransactionAt.Date);
        ui.Note($"Dun 23:30 kaydi: {late.TransactionAt:dd.MM.yyyy HH:mm zzz}; dun toplam {yesterdayBefore.TotalAmount} -> {yesterdaySummary.TotalAmount}, bugun {vm.DailyTotal}");
        vm.FilterFrom = DateTime.Today; Execute(ui, vm.ApplyFiltersCommand);

        // 4) Iptal.
        vm.SelectedTransaction = vm.Transactions.Single(t => t.Description == marker);
        Assert.True(vm.OpenVoidCommand.CanExecute(null));
        vm.OpenVoidCommand.Execute(null); ui.Pump();
        Assert.True(vm.IsVoidOpen);
        var voidTexts = DrawerTexts(ui, "VoidPanel");
        Assert.Contains(voidTexts, t => t.Contains("1.250,50"));
        Assert.Contains(voidTexts, t => t.Contains("8350016"));
        Assert.Contains(voidTexts, t => t.Contains("No 5016"));
        Assert.Equal("5016", vm.SelectedTransaction!.StudentNo);
        Assert.Contains(voidTexts, t => t.Contains("AKGÜN", StringComparison.OrdinalIgnoreCase));
        ui.Note("Iptal ozeti: " + string.Join(" | ", voidTexts));
        ui.Shot("cash-13-iptal-cekmece");
        Assert.False(vm.VoidCommand.CanExecute(null));
        vm.VoidReason = "Canlı test iptali"; vm.VoidConfirmed = true;
        Assert.True(vm.VoidCommand.CanExecute(null));
        Execute(ui, vm.VoidCommand);
        Assert.False(vm.IsVoidOpen);
        Assert.False(vm.HasError, vm.ErrorMessage);
        var voided = vm.Transactions.Single(t => t.Description == marker);
        Assert.True(voided.IsVoided); Assert.Equal("Canlı test iptali", voided.VoidReason);
        Assert.Equal(before.TotalAmount, vm.DailyTotal);
        ui.Shot("cash-14-iptal-sonrasi");

        // Iptal edilmis islemi tekrar iptal -> engellenmeli.
        vm.SelectedTransaction = voided;
        Assert.False(vm.OpenVoidCommand.CanExecute(null));

        // Iki cekmece ayni anda acilamamali.
        vm.SelectedTransaction = vm.Transactions.First(t => !t.IsVoided);
        vm.OpenAddCommand.Execute(null); vm.OpenVoidCommand.Execute(null); ui.Pump();
        Assert.False(vm.IsAddOpen && vm.IsVoidOpen, "iki cekmece ayni anda acik");
        vm.CloseAddCommand.Execute(null); vm.CloseVoidCommand.Execute(null);
    });

    [Fact]
    public void GunlukKasaTurlerVeRapor() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("cash");
        var vm = ui.Cash;
        var todayCard = vm.DailyTotal;

        // 5) Gunluk kasa: dun.
        var yesterday = DateTime.Today.AddDays(-1);
        vm.DailyDate = yesterday; Execute(ui, vm.LoadDailyCommand);
        var api = Get<CashSummary>(ui, $"api/cash/summary?period=Daily&date={yesterday:yyyy-MM-dd}");
        var tab = ui.TabControlWith("Günlük Kasa")!; tab.SelectedIndex = 1; ui.Pump();
        ui.Shot("cash-20-gunluk");
        var texts = ui.FindAll<TextBlock>(tab).Select(t => t.Text).ToList();
        Assert.Contains(texts, t => t.Contains(api.TotalAmount.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))));
        Assert.Equal(todayCard, vm.DailyTotal); // BUGUN karti dunun rakamiyla ezilmemeli
        ui.Note($"Gunluk kasa {yesterday:dd.MM}: net {api.TotalAmount}, iptal {api.VoidedAmount}, tur {api.ByIncomeType.Count}");

        // Ozel aralik.
        vm.CustomFrom = new DateTime(2026, 9, 1); vm.CustomTo = DateTime.Today; Execute(ui, vm.LoadCustomCommand);
        var custom = Get<CashSummary>(ui, $"api/cash/summary?period=Custom&from=2026-09-01&to={DateTime.Today:yyyy-MM-dd}");
        Assert.NotNull(vm.Custom);
        Assert.Equal(custom.TotalAmount, vm.Custom!.TotalAmount);
        Assert.Equal(custom.TransactionCount, vm.Custom.TransactionCount);
        // Ters aralik -> Turkce hata.
        vm.CustomFrom = DateTime.Today; vm.CustomTo = new DateTime(2026, 9, 1); Execute(ui, vm.LoadCustomCommand);
        Assert.True(vm.HasError);
        ui.Shot("cash-21-ozel-aralik");

        // 6) Gelir turleri.
        tab.SelectedIndex = 2; ui.Pump();
        var name = "Bağış " + DateTime.Now.ToString("HHmmss");
        vm.NewTypeCommand.Execute(null); vm.TypeName = name; Execute(ui, vm.SaveTypeCommand);
        Assert.False(vm.HasError, vm.ErrorMessage);
        var created = vm.ManagedTypes.Single(t => t.Name == name);
        Assert.Contains(vm.IncomeTypes, t => t.Id == created.Id);
        ui.Shot("cash-22-tur-yeni");

        // Ayni adla ikinci -> hata.
        vm.NewTypeCommand.Execute(null); vm.TypeName = name; Execute(ui, vm.SaveTypeCommand);
        Assert.Equal("Gelir türü adı zaten kayıtlı.", vm.ErrorMessage); // sunucunun ProblemDetails basligi, genel yedek metin degil
        ui.Note("Ayni ad hatasi: " + vm.ErrorMessage);
        Assert.Single(vm.ManagedTypes, t => t.Name == name);

        // Duzenle.
        vm.SelectedManagedType = created; vm.EditTypeCommand.Execute(null);
        Assert.Equal(name, vm.TypeName);
        vm.TypeName = name + " v2"; Execute(ui, vm.SaveTypeCommand);
        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.Contains(vm.ManagedTypes, t => t.Id == created.Id && t.Name == name + " v2");

        // Bu turle bir islem kaydet, sonra pasiflestir: listede tur adi kalmali, ekle listesinden dusmeli.
        vm.OpenAddCommand.Execute(null); ui.Pump();
        vm.StudentNumber = "5002"; Execute(ui, vm.LookupStudentCommand); Assert.NotNull(vm.LookupStudent);
        vm.SelectedAddType = vm.IncomeTypes.Single(t => t.Id == created.Id);
        vm.AmountText = "5,00"; vm.AddConfirmed = true; Execute(ui, vm.AddCommand);
        Assert.Null(vm.AddError);
        Assert.Contains(vm.Transactions, t => t.Amount == 5m && t.IncomeTypeName == name + " v2");
        vm.SelectedManagedType = vm.ManagedTypes.Single(t => t.Id == created.Id);
        Execute(ui, vm.DeactivateTypeCommand);
        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.DoesNotContain(vm.IncomeTypes, t => t.Id == created.Id);
        Assert.Contains(vm.ManagedTypes, t => t.Id == created.Id && !t.IsActive);
        Execute(ui, vm.RefreshCommand);
        Assert.Contains(vm.Transactions, t => t.IncomeTypeName == name + " v2");
        vm.OpenAddCommand.Execute(null); ui.Pump();
        Assert.DoesNotContain(vm.IncomeTypes, t => t.Id == created.Id);
        Assert.NotNull(vm.SelectedAddType); Assert.True(vm.SelectedAddType!.IsActive);
        vm.CloseAddCommand.Execute(null);
        tab.SelectedIndex = 2; ui.Pump();
        ui.Shot("cash-23-tur-pasif");

        // 7) Rapor merkezine aktar.
        Assert.True(vm.IsExportAvailable);
        vm.OpenReportsCommand.Execute(null); ui.Pump();
        var reportsHost = (ContentControl)ui.Window.FindName("ReportsHost")!;
        Assert.Equal(System.Windows.Visibility.Visible, reportsHost.Visibility);
        ui.Note("Rapor: secili rapor = " + ui.Reports.SelectedReport?.Name);
        ui.Shot("cash-24-rapor");
    });

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

    private static void Execute(LiveUiHarness ui, System.Windows.Input.ICommand command)
    {
        Assert.True(command.CanExecute(null), "komut calistirilamaz durumda");
        if (command is AsyncCommand async) Assert.True(LiveUiHarness.Wait(async.ExecuteAsync(null), TimeSpan.FromSeconds(30)), "komut zaman asimi");
        else command.Execute(null);
        ui.Pump();
    }

    private static List<string> DrawerTexts(LiveUiHarness ui, string name)
    {
        var panel = ui.FindAll<Yemekhane.Desktop.Controls.Drawer>().FirstOrDefault(d => d.Name == name)
            ?? throw new InvalidOperationException("Cekmece bulunamadi: " + name);
        return ui.FindAll<TextBlock>(panel).Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
    }

    /// <summary>Gorunen her metin hucresinin icerigi hucreye sigmali; sigmayanlar Note'a yazilir ve test duser.</summary>
    private static void AssertNoClippedCells(LiveUiHarness ui, DataGrid grid)
    {
        var clipped = new List<string>();
        foreach (var cell in ui.FindAll<DataGridCell>(grid))
        {
            if (cell.Content is not TextBlock text || string.IsNullOrEmpty(text.Text)) continue;
            text.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var needed = text.DesiredSize.Width + cell.Padding.Left + cell.Padding.Right;
            if (needed > cell.ActualWidth + 0.5)
                clipped.Add($"{cell.Column.Header}: '{text.Text}' {needed:F0}px > {cell.ActualWidth:F0}px");
        }
        foreach (var header in ui.FindAll<DataGridColumnHeader>(grid))
        {
            if (header.Content is not string title) continue;
            var tb = ui.FindAll<TextBlock>(header).FirstOrDefault();
            if (tb is null) continue;
            tb.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            if (tb.DesiredSize.Width + header.Padding.Left + header.Padding.Right > header.ActualWidth + 0.5)
                clipped.Add($"BASLIK {title}: {tb.DesiredSize.Width:F0}px > {header.ActualWidth:F0}px");
        }
        foreach (var c in clipped.Distinct()) ui.Note("KESIK: " + c);
        Assert.Empty(clipped.Distinct());
    }
}
