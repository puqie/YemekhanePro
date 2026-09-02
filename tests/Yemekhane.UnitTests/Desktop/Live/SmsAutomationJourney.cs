using System.Globalization;
using System.Text.Json;
using System.Windows.Controls;
using Xunit;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Income;
using Yemekhane.Application.Sms;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Otomatik SMS: Ayarlar → SMS → "Otomatik SMS" karti gercek API ile surulur (yerel ve sunucu
/// dogrulamasi, kaydet, yeniden yukle), sonra API'den gelir girilir ve kart degistirilir;
/// SMS Merkezi → Gecmis'te yetkili/veli mesajlari KAYNAK filtresiyle dogrulanir; "Şimdi gönder"
/// ile hak uyarisi kosulur ve veritabanindan sayilir.
/// </summary>
[Collection("UI")]
public class SmsAutomationJourney
{
    [Fact]
    public void KurallarGelirKartVeHakUyarisiAkisi() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        ui.Navigate("settings");
        var vm = ui.Settings;
        var tabs = ui.TabControlWith("Okul");
        Assert.NotNull(tabs);
        tabs!.SelectedIndex = 2; ui.Pump();
        Assert.False(vm.IsDirty);
        Assert.StartsWith("Sunucu saati: ", vm.ServerTimeText);
        Assert.Matches(@"\d\d:\d\d$", vm.ServerTimeText);
        ui.Shot("otosms-01-ayarlar");

        // ---- Yerel dogrulama: saat, gun esigi, telefonsuz gelir kurali. Sunucuya gitmez, cevrimdisi olmaz.
        vm.AutoEntitlementSendAt = "25:99";
        Assert.True(vm.IsDirty);
        Assert.False(vm.RunEntitlementWarningCommand.CanExecute(null));
        vm.SaveCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.Contains("SS:dd", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.False(vm.IsOffline);
        ui.Shot("otosms-02-saat-hatasi");
        vm.AutoEntitlementSendAt = "13:10"; vm.AutoEntitlementDaysText = "45";
        vm.SaveCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.Contains("1 ile 30", vm.ErrorMessage, StringComparison.Ordinal);
        vm.AutoEntitlementDaysText = "2"; vm.AutoIncomeEnabled = true; vm.AutoIncomePhone = "";
        vm.SaveCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.Contains("GSM", vm.ErrorMessage, StringComparison.Ordinal);

        // ---- Sunucu dogrulamasi: sabit hat yerel 10 haneyi gecer, sunucu "mobil" der; mesaj ekrana ulasir.
        vm.AutoIncomePhone = "0212 555 44 33";
        vm.SaveCommand.Execute(null); ui.Delay(2500); ui.Pump();
        Assert.Contains("mobil", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.False(vm.IsOffline);
        Assert.True(vm.IsDirty);
        ui.Note("sunucu telefon reddi: " + vm.ErrorMessage);
        ui.Shot("otosms-03-sunucu-telefon-reddi");

        // ---- Gecerli kayit: uc kural acik. Kart kurali veliye + yetkiliye.
        const string adminPhone = "0532 111 22 33";
        vm.AutoIncomePhone = adminPhone; vm.AutoEntitlementEnabled = true; vm.AutoCardEnabled = true; vm.AutoCardPhone = "";
        vm.SaveCommand.Execute(null); ui.Delay(2500); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
        Assert.False(vm.IsDirty);
        Assert.True(vm.RunEntitlementWarningCommand.CanExecute(null));
        var status = LiveApi.Get<SmsAutomationStatus>(ui, "api/settings/sms-automation");
        Assert.True(status.Settings.IncomeNotice.Enabled); Assert.Equal(adminPhone, status.Settings.IncomeNotice.AdminPhone);
        Assert.True(status.Settings.EntitlementWarning.Enabled); Assert.Equal(2, status.Settings.EntitlementWarning.DaysThreshold);
        Assert.Equal(new TimeOnly(13, 10), status.Settings.EntitlementWarning.SendAt);
        Assert.True(status.Settings.CardReplacement.Enabled);
        vm.RefreshCommand.Execute(null); ui.Delay(2500); ui.Pump();
        Assert.True(vm.AutoIncomeEnabled); Assert.Equal(adminPhone, vm.AutoIncomePhone); Assert.Equal("2", vm.AutoEntitlementDaysText);
        ui.Shot("otosms-04-kaydedildi");

        // Ogrenci SABIT SECILMEZ: tohumda her ogrencinin velisi/karti yok (420 ogrenci, 280 veli,
        // 373 kart) ve onceki kosular kart numaralarini degistirmis olabilir. Veli + aktif kart
        // tasiyan ilk ogrenci veritabanindan secilir.
        using var db = LiveDb.Open();
        var studentNo = db.Text("""
            SELECT s.student_no FROM students s
            JOIN parents p ON p.StudentId = s.Id AND p.IsActive = 1
            JOIN student_cards k ON k.StudentId = s.Id AND k.IsActive = 1
            WHERE s.IsActive = 1 AND s.IsDeleted = 0
            ORDER BY s.student_no LIMIT 1
            """);
        Assert.False(string.IsNullOrEmpty(studentNo), "veli ve kartı olan aktif öğrenci bulunamadı");
        var student = ApiStudent(ui, studentNo!);
        Assert.NotNull(student.Id);
        var parentPhone = db.Text($"SELECT NormalizedPhone FROM parents WHERE StudentId = '{Sql(student.Id!.Value)}' AND IsActive = 1 ORDER BY IsPrimary DESC LIMIT 1");
        Assert.False(string.IsNullOrEmpty(parentPhone), $"{studentNo} velisi bulunamadı");
        ui.Note($"seçilen öğrenci: {studentNo} {student.FirstName}, kart {student.CardNumber}");

        // ---- Gelir: API'den kayit, yetkiliye SMS; gecmiste kaynak filtresiyle bulunur.
        var types = LiveApi.Get<List<IncomeTypeDetails>>(ui, "api/income/types");
        var marker = "Oto SMS " + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        var income = LiveApi.Post<IncomeTransactionDetails>(ui, "api/income/transactions", new
        {
            operationId = Guid.NewGuid(), studentId = student.Id, transactionAt = DateTimeOffset.Now,
            incomeTypeId = types[0].Id, amount = 1250.75m, description = marker
        });
        ui.Navigate("sms");
        var sms = ui.Sms;
        var smsTabs = ui.TabControlWith("Gönder");
        Assert.NotNull(smsTabs);
        smsTabs!.SelectedIndex = 2; ui.Pump();
        sms.HistoryStudent = ""; sms.HistoryPhone = ""; sms.HistoryProvider = ""; sms.HistoryStatus = ""; sms.HistorySource = SmsSources.AutoIncome;
        sms.RefreshHistoryCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.Equal("", sms.HistoryError);
        var incomeSms = sms.History.FirstOrDefault(h => h.Message.Contains(marker, StringComparison.Ordinal));
        Assert.NotNull(incomeSms);
        Assert.Equal("+905321112233", incomeSms!.Phone);
        Assert.Equal(SmsSources.AutoIncome, incomeSms.Source);
        Assert.Contains("1.250,75", incomeSms.Message, StringComparison.Ordinal);
        Assert.Contains(student.FirstName!, incomeSms.Message, StringComparison.Ordinal);
        Assert.Contains(types[0].Name, incomeSms.Message, StringComparison.Ordinal);
        Assert.All(sms.History, h => Assert.Equal(SmsSources.AutoIncome, h.Source));
        ui.Note("gelir SMS: " + incomeSms.Message);
        var texts = ui.FindAll<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Otomatik: gelir bildirimi", texts);
        Assert.DoesNotContain("AutoIncome", texts);
        ui.Shot("otosms-05-gecmis-gelir");

        // ---- Kart degistir: veliye SMS, eski kart no mesajda.
        var oldCard = student.CardNumber;
        Assert.NotNull(oldCard);
        var newCard = "OT" + DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);
        var card = LiveApi.Post<CardDetails>(ui, $"api/students/{student.Id:D}/cards/replace", new { cardNumber = newCard, reason = "Otomatik SMS yolculuğu" });
        Assert.Equal(newCard, card.CardNumber);
        sms.HistorySource = SmsSources.AutoCard;
        sms.RefreshHistoryCommand.Execute(null); ui.Delay(1500); ui.Pump();
        var cardSms = sms.History.FirstOrDefault(h => h.Message.Contains(newCard, StringComparison.Ordinal));
        Assert.NotNull(cardSms);
        Assert.Equal(Yemekhane.Application.Common.TurkishMobilePhone.Normalize(parentPhone), cardSms!.Phone);
        Assert.Contains(student.FirstName!, cardSms.Message, StringComparison.Ordinal);
        Assert.Equal(1, db.Count($"SELECT COUNT(*) FROM sms_logs WHERE IdempotencyKey LIKE 'oto:kart:{card.Id:D}%'"));
        ui.Note("kart SMS: " + cardSms.Message);
        ui.Shot("otosms-06-gecmis-kart");
        // Eski kart numarasi GERI VERILMEZ: pasiflesen kart satiri numarayi uzerinde tutar
        // (kart no benzersiz) ve ayni numarayi yeniden atamak 409 doner. Her kosu benzersiz
        // "OTssdddd" numarasi kullanir; tohumun kart numaralarina dokunulmaz.
        ui.Note($"kart {oldCard} -> {newCard} olarak degisti (geri alinmaz, numara benzersizdir)");

        // ---- Hak uyarisi: secilen ogrencinin bugunden sonraki haklarini iptal et -> kalan gun <= 2 -> "Şimdi gönder".
        var today = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var remainingBefore = db.Count($"SELECT COUNT(DISTINCT EntitlementDate) FROM meal_entitlements WHERE StudentId = '{Sql(student.Id!.Value)}' AND Status = 'Active' AND Quantity > ConsumedQuantity AND EntitlementDate >= '{today}'");
        ui.Note($"{studentNo} kalan hak günü (önce): {remainingBefore}");
        var cancelled = db.Execute($"UPDATE meal_entitlements SET Status = 'Cancelled' WHERE StudentId = '{Sql(student.Id!.Value)}' AND Status = 'Active' AND EntitlementDate > '{today}'");
        try
        {
            var remaining = db.Count($"SELECT COUNT(DISTINCT EntitlementDate) FROM meal_entitlements WHERE StudentId = '{Sql(student.Id!.Value)}' AND Status = 'Active' AND Quantity > ConsumedQuantity AND EntitlementDate >= '{today}'");
            Assert.True(remaining <= 2, "iptal sonrası kalan gün: " + remaining);
            var keyPrefix = "oto:hak:" + DateOnly.FromDateTime(DateTime.Now).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ":";
            var alreadyToday = db.Count($"SELECT COUNT(*) FROM sms_logs WHERE IdempotencyKey = '{keyPrefix}{student.Id:D}'");

            ui.Navigate("settings"); tabs.SelectedIndex = 2; ui.Pump();
            vm.RunEntitlementWarningCommand.Execute(null); ui.Delay(4000); ui.Pump();
            Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
            Assert.NotNull(vm.EntitlementRunText);
            ui.Note("hak uyarısı sonucu: " + vm.EntitlementRunText);
            var expectedQueued = alreadyToday == 0 ? 1 : 0;
            var match = System.Text.RegularExpressions.Regex.Match(vm.EntitlementRunText!, @": (\d+) SMS kuyruğa alındı \((\d+) aday");
            Assert.True(match.Success, vm.EntitlementRunText);
            Assert.True(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) >= expectedQueued, vm.EntitlementRunText);
            Assert.True(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) >= 1, vm.EntitlementRunText);
            // Gunde tek kayit: ilk kosuda yeni yazilir, ayni gunku tekrar kosuda mevcut kayit korunur.
            Assert.Equal(1, db.Count($"SELECT COUNT(*) FROM sms_logs WHERE IdempotencyKey = '{keyPrefix}{student.Id:D}'"));
            var warning = db.Text($"SELECT Message FROM sms_logs WHERE IdempotencyKey = '{keyPrefix}{student.Id:D}'")!;
            Assert.Contains(student.FirstName!, warning, StringComparison.Ordinal);
            // Kalan gun sayisi yalnizca SMS'i BU kosu yazdiysa dogrulanabilir: ayni gun daha once
            // kosulduysa kayit o anki (farkli) kalan gun degeriyle yazilmistir ve dedupe korur.
            if (alreadyToday == 0) Assert.Contains($"{remaining} gün", warning, StringComparison.Ordinal);
            ui.Note("hak uyarısı SMS: " + warning);
            ui.Shot("otosms-07-simdi-gonder");

            // Ikinci kosu ayni gun: ayni ogrenciye ikinci SMS yok.
            vm.RunEntitlementWarningCommand.Execute(null); ui.Delay(4000); ui.Pump();
            Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
            Assert.Contains(": 0 SMS kuyruğa alındı", vm.EntitlementRunText, StringComparison.Ordinal);
            Assert.Equal(1, db.Count($"SELECT COUNT(*) FROM sms_logs WHERE IdempotencyKey = '{keyPrefix}{student.Id:D}'"));

            // Gecmiste kaynak filtresi + Turkce kaynak metni.
            ui.Navigate("sms"); smsTabs.SelectedIndex = 2; ui.Pump();
            sms.HistorySource = SmsSources.AutoEntitlement;
            sms.RefreshHistoryCommand.Execute(null); ui.Delay(1500); ui.Pump();
            Assert.Contains(sms.History, h => h.StudentId == student.Id && h.Source == SmsSources.AutoEntitlement);
            texts = ui.FindAll<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("Otomatik: hak uyarısı", texts);
            ui.Shot("otosms-08-gecmis-hak");
            sms.HistorySource = "";
        }
        finally
        {
            db.Execute($"UPDATE meal_entitlements SET Status = 'Active' WHERE StudentId = '{Sql(student.Id!.Value)}' AND Status = 'Cancelled' AND EntitlementDate > '{today}'");
            ui.Note($"iptal edilen {cancelled} hak geri alındı");
        }

        // ---- Kurallari kapat (diger yolculuklarin gelir/kart islemleri SMS uretmesin).
        ui.Navigate("settings"); tabs.SelectedIndex = 2; ui.Pump();
        vm.AutoIncomeEnabled = false; vm.AutoEntitlementEnabled = false; vm.AutoCardEnabled = false;
        vm.SaveCommand.Execute(null); ui.Delay(2500); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
        Assert.False(LiveApi.Get<SmsAutomationStatus>(ui, "api/settings/sms-automation").Settings.IncomeNotice.Enabled);
    }, TimeSpan.FromMinutes(10));

    /// <summary>
    /// GUID'i EF Core'un SUTUNA yazdigi bicimde (BUYUK harf) verir. C# varsayilani
    /// <c>{id:D}</c> kucuk harftir; SQLite'ta <c>=</c> harf duyarli oldugundan
    /// <c>StudentId = '{id:D}'</c> HIC eslesmez ve sorgu sessizce bos doner.
    /// DIKKAT: yalnizca Guid SUTUNLARI icin. <c>IdempotencyKey</c> gibi bizim
    /// METIN olarak urettigimiz degerler kucuk harf yazilir; orada <c>{id:D}</c> dogrudur.
    /// </summary>
    private static string Sql(Guid id) => id.ToString("D").ToUpperInvariant();

    private sealed record StudentSnapshot(Guid? Id, string? CardNumber, string? FirstName);

    private static StudentSnapshot ApiStudent(LiveUiHarness ui, string studentNo)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(LiveApi.Get<JsonElement>(ui, $"api/students?studentNo={studentNo}&pageSize=5")));
        var items = doc.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0) return new(null, null, null);
        var item = items[0];
        return new(item.GetProperty("id").GetGuid(), item.GetProperty("cardNumber").GetString(), item.GetProperty("firstName").GetString());
    }
}
