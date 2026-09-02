using System.Globalization;
using System.Net.Http.Json;
using System.Windows.Controls;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Cash;
using Yemekhane.Application.Meals;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Xunit;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// On odemeli TL bakiyesinin CANLI uctan uca yolculugu (eski programdaki "Tl Bakiye Yukleme"):
/// Kasa'dan yukleme -> islem listesinde ve ogrencinin Bakiye sekmesinde gorunur ->
/// hakki OLMAYAN ogrenci gercek turnike cagrisiyla geciyor ve ucret bakiyeden dusuyor ->
/// ayni olay tekrar gelince IKINCI dusum olmuyor -> gelir islemi iptal edilince bakiye duser.
/// YP_LIVE_API yoksa sessizce gecer.
/// </summary>
[Collection("UI")]
public class BalanceJourney
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    [Fact]
    public void BakiyeYuklemeGecisteDusumVeIptal() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        using var db = LiveDb.Open();

        // 0) Ogun ucreti: tohumda fiyat yok; bakiye kurali ancak ucret > 0 iken devreye girer.
        // Ucret KAHVALTI'ya verilir: tohumda hakedisler yalnizca Ogle Yemegi icin uretilmis, yani
        // "hakki olmayan ogun" gercekten kahvaltidir. Ogle uzerinden gidilseydi tum ogrencilerin
        // hakki oldugu icin bakiye yolu hic denenmezdi.
        var meals = LiveApi.Get<IReadOnlyList<MealTypeDetails>>(ui, "api/meal-types");
        var meal = meals.First(x => x.Name.StartsWith("Kahvalt", StringComparison.Ordinal));
        LiveApi.Put<MealTypeDetails>(ui, $"api/meal-types/{meal.Id:D}",
            new SaveMealTypeRequest(meal.Name, meal.StartsAt, meal.EndsAt, meal.IsActive, 75m));
        Assert.Equal(75m, LiveApi.Get<IReadOnlyList<MealTypeDetails>>(ui, "api/meal-types").Single(x => x.Id == meal.Id).Price);
        ui.Note($"Ogun ucreti: {meal.Name} = 75,00 ₺");

        // Bu ogun icin hakki OLMAYAN, aktif kartli ve defteri HENUZ BOS bir ogrenci sec.
        // Defteri bos olmasi sarti yolculugun tekrar tekrar kosulabilmesi icindir: onceki kosunun
        // biraktigi bakiye/dusum satirlari beklenen rakamlari kaydirir.
        var studentNo = db.Text(
            "SELECT s.student_no FROM students s JOIN student_cards c ON c.StudentId = s.Id " +
            "WHERE s.IsActive = 1 AND s.IsDeleted = 0 AND c.IsActive = 1 " +
            "  AND NOT EXISTS (SELECT 1 FROM student_balance_entries b WHERE b.StudentId = s.Id) " +
            "  AND NOT EXISTS (" +
            "  SELECT 1 FROM meal_entitlements e WHERE e.StudentId = s.Id AND e.EntitlementDate = '" + Journey.Today + "'" +
            "    AND e.MealTypeId = '" + meal.Id.ToString("D").ToUpperInvariant() + "') " +
            "ORDER BY s.student_no DESC LIMIT 1")!;
        var cardNumber = db.Text($"SELECT c.card_number FROM student_cards c JOIN students s ON s.Id = c.StudentId WHERE s.student_no = '{studentNo}' AND c.IsActive = 1")!;
        var studentId = Guid.Parse(db.Text($"SELECT Id FROM students WHERE student_no = '{studentNo}'")!);
        ui.Note($"Hakkisiz ogrenci: no {studentNo}, kart {cardNumber}");

        // ---------------------------------------------------------------- 1) Kasa > Bakiye Yukle
        ui.Navigate("cash");
        var vm = ui.Cash;
        Assert.False(vm.HasError, vm.ErrorMessage);
        var dailyBefore = LiveApi.Get<CashSummary>(ui, "api/cash/summary?period=Daily").TotalAmount;

        Assert.True(vm.OpenTopUpCommand.CanExecute(null));
        vm.OpenTopUpCommand.Execute(null); ui.Pump(4);
        Assert.True(vm.IsTopUpOpen);
        Assert.False(vm.IsAddOpen, "Bakiye Yukle acilinca Gelir Ekle kapanmali");
        // Cekmece gercekten cizilmis mi (bos panel degil): baslik ve yonlendirme metni.
        var drawerTexts = DrawerTexts(ui, "TopUpPanel");
        Assert.Contains(drawerTexts, t => t.Contains("Bakiye Yükle", StringComparison.Ordinal));
        Assert.Contains(drawerTexts, t => t.Contains("tüm öğünler", StringComparison.OrdinalIgnoreCase));
        ui.Shot("bakiye-01-cekmece-bos");

        // Dogrulama hatalari kullaniciya ulasiyor mu.
        Assert.Equal("Öğrenci veya kart doğrulaması zorunludur.", vm.ValidateTopUp());
        vm.StudentNumber = studentNo;
        Execute(ui, vm.LookupStudentCommand);
        Assert.NotNull(vm.LookupStudent);
        Assert.Contains("No " + studentNo, vm.LookupStudentText, StringComparison.Ordinal);
        vm.TopUpAmountText = "abc";
        Assert.StartsWith("Tutar sıfırdan büyük", vm.ValidateTopUp(), StringComparison.Ordinal);
        vm.TopUpAmountText = "500";
        Assert.Equal("Yükleme bilgilerini onaylayın.", vm.ValidateTopUp());
        vm.TopUpNote = "Eylül bakiyesi";
        vm.TopUpConfirmed = true;
        Assert.Null(vm.ValidateTopUp());
        ui.Shot("bakiye-02-cekmece-dolu");

        Execute(ui, vm.TopUpCommand);
        Assert.Null(vm.TopUpError);
        Assert.False(vm.IsTopUpOpen);
        Assert.NotNull(vm.StatusMessage);
        Assert.Contains("500", vm.StatusMessage!, StringComparison.Ordinal);
        ui.Note("Yukleme sonucu: " + vm.StatusMessage);

        // 2) Kasa listesi ve ozet: 500 ₺ "Bakiye Yükleme" turuyle gorunur.
        var topUpRow = vm.Transactions.FirstOrDefault(x => x.StudentNo == studentNo && x.Amount == 500m);
        Assert.NotNull(topUpRow);
        Assert.Equal(StudentBalanceIncomeType.Name, topUpRow!.IncomeTypeName);
        Assert.Equal(dailyBefore + 500m, LiveApi.Get<CashSummary>(ui, "api/cash/summary?period=Daily").TotalAmount);
        Assert.Equal(dailyBefore + 500m, vm.DailyTotal);
        ui.Shot("bakiye-03-kasa-listesi");

        // 3) API: bakiye ve defter.
        var balance = LiveApi.Get<StudentBalanceSummary>(ui, $"api/students/{studentId:D}/balance");
        Assert.Equal(500m, balance.Balance);
        Assert.Equal(500m, balance.Available);
        Assert.Equal(studentNo, balance.StudentNo);
        var entry = Assert.Single(balance.Entries.Items);
        Assert.Equal(StudentBalanceEntryKinds.TopUp, entry.Kind);
        Assert.Equal(500m, entry.Amount);
        Assert.Equal(topUpRow.Id, entry.ReferenceId);

        // ---------------------------------------------------------------- 4) Ogrenciler > Bakiye sekmesi
        ui.Navigate("students");
        var students = ui.Students;
        students.Search = studentNo;
        Execute(ui, students.SearchCommand);
        Journey.Until(ui, () => students.Students.Any(x => x.StudentNo == studentNo), "arama " + studentNo);
        var listed = students.Students.First(x => x.StudentNo == studentNo);
        students.OpenFullDetailCommand.Execute(listed);
        Journey.Until(ui, () => students.Details?.Id == listed.Id, "detay " + studentNo);
        var balanceTab = students.Tabs.First(t => t.Key == "Balance");
        Assert.Equal("Bakiye", balanceTab.Title);
        students.SelectedTab = balanceTab;
        Journey.Until(ui, () => balanceTab.IsLoaded || balanceTab.Error is not null, "Bakiye sekmesi");
        Assert.Null(balanceTab.Error);
        var headline = Assert.IsType<StudentBalanceHeadline>(balanceTab.Items[0]);
        Assert.Equal(500m, headline.Balance);
        Assert.Equal(500m.ToString("C2", Turkish), headline.BalanceText);
        var movement = Assert.IsType<StudentDetailRow>(balanceTab.Items[1]);
        Assert.Contains("Yükleme", movement.Summary, StringComparison.Ordinal);
        Assert.Contains("Eylül bakiyesi", movement.Summary, StringComparison.Ordinal);
        // Buyuk bakiye gercekten ekranda cizilmis mi.
        Assert.Contains(ui.FindAll<TextBlock>().Select(x => x.Text), t => t == headline.BalanceText);
        ui.Shot("bakiye-04-ogrenci-sekmesi");

        // ---------------------------------------------------------------- 5) Gercek gecis: hak yok, bakiyeden dus
        var deviceId = Guid.Parse(db.Text("SELECT Id FROM devices WHERE Name LIKE 'Yemekhane Giri%'")!);
        var operationId = Guid.NewGuid();
        var decision = Access(ui, cardNumber, deviceId, meal.Id, operationId);
        Assert.Equal("ALLOW", decision.Decision);
        Assert.Equal(BalanceAccessReasons.BalanceUsed, decision.Reason);
        var afterAccess = LiveApi.Get<StudentBalanceSummary>(ui, $"api/students/{studentId:D}/balance");
        Assert.Equal(425m, afterAccess.Balance);
        Assert.Contains(afterAccess.Entries.Items, x => x.Kind == StudentBalanceEntryKinds.Deduction && x.Amount == -75m);
        ui.Note($"Gecis: {decision.Decision}/{decision.Reason}, bakiye 500 -> {afterAccess.Balance}");

        // 6) AYNI olay tekrar: ikinci dusum OLMAZ.
        var replay = Access(ui, cardNumber, deviceId, meal.Id, operationId);
        Assert.Equal("ALLOW", replay.Decision);
        Assert.Equal(decision.OperationId, replay.OperationId);
        var afterReplay = LiveApi.Get<StudentBalanceSummary>(ui, $"api/students/{studentId:D}/balance");
        Assert.Equal(425m, afterReplay.Balance);
        Assert.Equal(2, afterReplay.Entries.TotalCount);
        ui.Note("Tekrar gonderim: ikinci dusum yok, bakiye " + afterReplay.Balance);

        // 7) Bakiye bitince red: 425 ₺ / 75 ₺ = 5 gecis daha, sonrasi InsufficientBalance.
        for (var i = 0; i < 5; i++) Assert.Equal("ALLOW", Access(ui, cardNumber, deviceId, meal.Id, Guid.NewGuid()).Decision);
        var denied = Access(ui, cardNumber, deviceId, meal.Id, Guid.NewGuid());
        Assert.Equal("DENY", denied.Decision);
        Assert.Equal(BalanceAccessReasons.InsufficientBalance, denied.Reason);
        Assert.Equal(50m, LiveApi.Get<StudentBalanceSummary>(ui, $"api/students/{studentId:D}/balance").Balance);

        // 8) Gunluk Takip: nedenler Turkce gorunuyor mu.
        ui.Navigate("daily-tracking");
        var tracking = ui.Tracking;
        Execute(ui, tracking.RefreshCommand);
        Journey.Until(ui, () => tracking.Rows.Any(x => x.OperationId == decision.OperationId), "gecis satiri");
        Assert.Equal("Bakiyeden düşüldü", Yemekhane.Desktop.Converters.EnumTextConverter.Translate(
            tracking.Rows.First(x => x.OperationId == decision.OperationId).Reason, "Reason"));
        Assert.Equal("Yemek hakkı yok; bakiye yetersiz", Yemekhane.Desktop.Converters.EnumTextConverter.Translate(
            BalanceAccessReasons.InsufficientBalance, "Reason"));
        Journey.Until(ui, () => ui.FindAll<TextBlock>().Any(t => t.Text == "Bakiyeden düşüldü"), "ekranda Turkce neden");
        ui.Shot("bakiye-05-gunluk-takip");

        // ---------------------------------------------------------------- 9) Gelir islemini iptal et -> bakiye duser
        ui.Navigate("cash");
        Execute(ui, vm.RefreshCommand);
        vm.SelectedTransaction = vm.Transactions.First(x => x.Id == topUpRow.Id);
        vm.OpenVoidCommand.Execute(null); ui.Pump(3);
        vm.VoidReason = "Yanlış öğrenciye yüklendi";
        vm.VoidConfirmed = true;
        ui.Shot("bakiye-06-iptal-cekmece");
        Execute(ui, vm.VoidCommand);
        Assert.False(vm.HasError, vm.ErrorMessage);
        // Para harcanmisti: sunucu bakiyenin eksiye dustugunu bildirmeli, sessiz kalmamali.
        Assert.NotNull(vm.StatusMessage);
        Assert.Contains("EKSİYE", vm.StatusMessage!, StringComparison.Ordinal);
        var afterVoid = LiveApi.Get<StudentBalanceSummary>(ui, $"api/students/{studentId:D}/balance");
        Assert.Equal(-450m, afterVoid.Balance);
        Assert.Contains(afterVoid.Entries.Items, x => x.Kind == StudentBalanceEntryKinds.Refund && x.Amount == -500m);
        ui.Note("Iptal sonrasi bakiye: " + afterVoid.Balance + " | uyari: " + vm.StatusMessage);
        ui.Shot("bakiye-07-iptal-sonrasi");

        // Bakiye eksideyken yeni gecis reddedilir.
        var afterVoidDecision = Access(ui, cardNumber, deviceId, meal.Id, Guid.NewGuid());
        Assert.Equal("DENY", afterVoidDecision.Decision);
        Assert.Equal(BalanceAccessReasons.InsufficientBalance, afterVoidDecision.Reason);

        // 10) Ogrenci sekmesi eksi bakiyeyi ve iade satirini gosteriyor.
        ui.Navigate("students");
        var refreshed = students.Tabs.First(t => t.Key == "Balance");
        Journey.Run(ui, refreshed.ReloadAsync(), "Bakiye sekmesi yeniden");
        students.SelectedTab = refreshed;
        ui.Pump(4);
        var negative = Assert.IsType<StudentBalanceHeadline>(refreshed.Items[0]);
        Assert.Equal(-450m, negative.Balance);
        Assert.Contains("ekside", negative.DetailText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(refreshed.Items.OfType<StudentDetailRow>(), r => r.Summary.Contains("İade", StringComparison.Ordinal));
        ui.Shot("bakiye-08-ogrenci-eksi");
    });

    private static AccessReply Access(LiveUiHarness ui, string cardNumber, Guid deviceId, Guid mealTypeId, Guid operationId)
    {
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "api/access/check")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                cardNumber, deviceId, mealTypeId, timestamp = DateTimeOffset.Now,
                direction = "Entry", readerSource = "Device", operationId
            })
        };
        request.Headers.Add("X-Device-Key", Environment.GetEnvironmentVariable("YP_DEVICE_KEY") ?? "test-cihaz-anahtari-1234567890");
        var send = ui.Http.SendAsync(request);
        Assert.True(LiveUiHarness.Wait(send, TimeSpan.FromSeconds(30)), "access/check zaman asimi");
        Assert.True(send.Result.IsSuccessStatusCode, "access/check " + send.Result.StatusCode);
        var read = send.Result.Content.ReadFromJsonAsync<AccessReply>();
        Assert.True(LiveUiHarness.Wait(read, TimeSpan.FromSeconds(20)), "access/check govdesi");
        ui.Pump(2);
        return read.Result!;
    }

    private sealed record AccessReply(Guid OperationId, string Decision, string Reason);

    private static void Execute(LiveUiHarness ui, System.Windows.Input.ICommand command)
    {
        Assert.True(command.CanExecute(null), "komut calistirilamaz durumda");
        if (command is AsyncCommand async) Assert.True(LiveUiHarness.Wait(async.ExecuteAsync(null), TimeSpan.FromSeconds(30)), "komut zaman asimi");
        else command.Execute(null);
        ui.Delay(400); ui.Pump(3);
    }

    /// <summary>Cekmece View'in kendi ad kapsamindadir; Window.FindName oraya ULASMAZ, gorsel agactan bulunur.</summary>
    private static List<string> DrawerTexts(LiveUiHarness ui, string name)
    {
        var panel = ui.FindAll<Yemekhane.Desktop.Controls.Drawer>().FirstOrDefault(d => d.Name == name)
            ?? throw new InvalidOperationException("Cekmece bulunamadi: " + name);
        return ui.FindAll<TextBlock>(panel).Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
    }
}
