using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Xunit;
using Yemekhane.Application.Devices;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop.Live;

[Collection("UI")]
public class DeviceCardsJourney
{
    [Fact]
    public void KartYuklemeDurumuBekleyenlerVeSimdiYukle() => LiveUiHarness.Run(ui =>
    {
        using var db = LiveDb.Open();
        ui.LoadAll();
        ui.Navigate("device-cards");
        var vm = ui.DeviceCards;
        Journey.Until(ui, () => !vm.IsLoading, "kart durumu");
        Assert.Null(vm.Error);
        var view = ui.FindAll<Yemekhane.Desktop.Views.DeviceCardsView>().Single();

        // 1) Cihaz kartlari SQLite ile birebir.
        Assert.Equal(db.Count("SELECT COUNT(*) FROM devices WHERE IsActive = 1"), vm.Devices.Count);
        foreach (var device in vm.Devices)
        {
            var id = device.DeviceId.ToString().ToUpperInvariant();
            Assert.Equal(db.Count($"SELECT COUNT(*) FROM device_card_states WHERE DeviceId = '{id}' AND Status = '{DeviceCardSyncStatus.Loaded}'"), device.Loaded);
            Assert.Equal(db.Count($"SELECT COUNT(*) FROM device_card_states WHERE DeviceId = '{id}' AND Status IN ('{DeviceCardSyncStatus.Pending}', '{DeviceCardSyncStatus.PendingRemoval}')"), device.Pending);
            Assert.Equal(db.Count($"SELECT COUNT(*) FROM device_card_states WHERE DeviceId = '{id}' AND Status = '{DeviceCardSyncStatus.Failed}'"), device.Failed);
            ui.Note($"{device.DeviceName}: yüklü {device.Loaded}, bekliyor {device.Pending}, hatalı {device.Failed} -> {device.StatusText}");
        }
        ui.Shot("device-cards-01");

        // 2) Bir karti yeniden kuyruga al (POST resync) -> bekleyenler listesinde ogrenci kimligi.
        var cardId = db.Text("SELECT c.Id FROM student_cards c JOIN students s ON s.Id = c.StudentId WHERE s.FirstName = 'ADA' ORDER BY c.card_number LIMIT 1")!;
        var resync = ui.Http.SendAsync(Authorized(ui, HttpMethod.Post, $"api/device-cards/cards/{cardId}/resync"));
        Journey.Run(ui, resync, "resync");
        Assert.True(resync.Result.IsSuccessStatusCode, resync.Result.StatusCode.ToString());
        Journey.Run(ui, vm.InitializeAsync(), "yenile");
        var pendingDevice = vm.Devices.First(x => x.Pending > 0);
        Journey.Run(ui, vm.SelectDeviceAsync(pendingDevice), "bekleyenler");
        Assert.True(vm.HasSelection);
        Assert.Equal((int)Math.Min(100, pendingDevice.Pending), vm.PendingCards.Count);
        var ada = vm.PendingCards.Single(x => x.CardNumber == db.Text($"SELECT card_number FROM student_cards WHERE Id = '{cardId}'"));
        Assert.StartsWith("ADA", ada.StudentName, StringComparison.Ordinal);
        Assert.Contains(db.Text($"SELECT s.student_no FROM students s JOIN student_cards c ON c.StudentId = s.Id WHERE c.Id = '{cardId}'")!, ada.IdentityText);
        var texts = Journey.TextsIn(ui, view).ToList();
        Assert.Contains(texts, x => x == ada.IdentityText);
        ui.Shot("device-cards-02-bekleyenler");

        // 3) Simdi yukle: cihazlar cevrimdisi -> sessiz basarisizlik degil, Turkce aciklama.
        var button = ui.FindAll<Button>(view).Single(x => (x.Content as string) == "Şimdi yükle");
        Assert.True(Journey.IsPrimary(ui, button), "Şimdi yükle düğmesi turuncu (Primary) değil");
        var outstanding = vm.TotalOutstanding;
        vm.PushNowCommand.Execute(null);
        ui.Delay(500);
        Journey.Until(ui, () => !vm.IsPushing && !vm.IsLoading, "şimdi yükle", 60000);
        ui.Note($"şimdi yükle: önce {outstanding}, sonra {vm.TotalOutstanding}; mesaj: {vm.StatusMessage ?? vm.Error}");
        Assert.Null(vm.Error);
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusMessage), "yükleme sonrası kullanıcıya hiçbir mesaj gösterilmedi");
        if (vm.TotalOutstanding == outstanding) Assert.Contains("çevrimdışı", vm.StatusMessage!, StringComparison.Ordinal);
        Assert.Contains(Journey.TextsIn(ui, view), x => x == vm.StatusMessage);
        ui.Shot("device-cards-03-simdi-yukle");
    });

    /// <summary>
    /// "Cihazdaki kartlar" sekmesi (eski programdaki Cihaz Sicil Listesi'nin karsiligi):
    /// kart listesi, arama ve sayfalama; kaynak sunucunun kart-cihaz durum tablosudur.
    /// </summary>
    [Fact]
    public void CihazdakiKartlarListesiAramaVeSayfalama() => LiveUiHarness.Run(ui =>
    {
        using var db = LiveDb.Open();
        ui.LoadAll();
        ui.Navigate("device-cards");
        var vm = ui.DeviceCards;
        Journey.Until(ui, () => !vm.IsLoading, "kart durumu");
        var view = ui.FindAll<Yemekhane.Desktop.Views.DeviceCardsView>().Single();
        Assert.True(vm.HasCardList, "gerçek istemci kart listesi arayüzünü uygulamalı");

        // 1) "Cihazdaki kartlar" dugmesi paneli kart listesi sekmesiyle acar.
        // Kart TASIYAN cihaz secilir: tohumda kart okuyucunun (SF300 olmayan) hic kart durumu yok
        // ve gorsel agactaki ILK dugme ona ait olabilir -- o zaman liste hakli olarak bos gelirdi.
        var device = vm.Devices.First(x => x.Loaded + x.Pending + x.Failed > 0);
        var cardsButton = ui.FindAll<Button>(view)
            .Single(x => (x.Content as string) == "Cihazdaki kartlar"
                && ReferenceEquals((x as FrameworkElement)?.DataContext, device));
        cardsButton.Command.Execute(device);
        // AsyncCommand hemen doner; "!IsCardsLoading" istek BASLAMADAN once de dogrudur ve
        // bekleme aninda gecerdi. Gercek bitis isareti listenin dolmasidir.
        ui.Delay(500);
        Journey.Until(ui, () => !vm.IsCardsLoading && vm.HasSelection && vm.DeviceCards.Count > 0, "cihazdaki kartlar");
        Assert.Null(vm.Error);
        Assert.Equal(DeviceCardsViewModel.CardsTab, vm.SelectedPanelTab);

        // 2) Toplam ve satirlar SQLite ile birebir (Removed haric).
        var id = device.DeviceId.ToString().ToUpperInvariant();
        var expected = db.Count($"SELECT COUNT(*) FROM device_card_states WHERE DeviceId = '{id}' AND Status <> 'Removed'");
        Assert.Equal((int)expected, vm.CardsTotal);
        Assert.Equal((int)Math.Min(DeviceCardsViewModel.CardsPageSize, expected), vm.DeviceCards.Count);
        Assert.Contains($"({expected})", vm.CardsTabHeader);
        ui.Note($"{device.DeviceName}: cihazda {expected} kart");

        // Satir icerigi: no + ad + kart, ham durum kodu ekranda YOK.
        var first = vm.DeviceCards[0];
        Assert.False(string.IsNullOrWhiteSpace(first.StudentNo));
        Assert.False(string.IsNullOrWhiteSpace(first.CardNumber));
        Assert.Equal(db.Text($"SELECT s.FirstName || ' ' || s.LastName FROM students s JOIN student_cards c ON c.StudentId = s.Id WHERE c.card_number = '{first.CardNumber}'"), first.StudentName);
        var grid = ui.FindAll<DataGrid>(view).Single(x => x.Name == "DeviceCardsGrid");
        var texts = Journey.TextsIn(ui, grid).ToList();
        Assert.DoesNotContain(texts, x => x is "Pending" or "Loaded" or "Failed" or "PendingRemoval");
        Assert.Contains(texts, x => x is "Bekliyor" or "Yüklendi" or "Hata");
        // 1440px: yatay kaydirma cikmamali, hucre kesilmemeli.
        var scroll = ui.FindAll<ScrollViewer>(grid).FirstOrDefault();
        Assert.False(scroll?.ComputedHorizontalScrollBarVisibility == Visibility.Visible,
            $"cihazdaki kartlar: yatay kaydırma (sütun {grid.Columns.Sum(c => c.ActualWidth):0}px / tablo {grid.ActualWidth:0}px)");
        var clipped = Journey.ClippedCells(ui, grid);
        Assert.True(clipped.Count == 0, "kesik hücre(ler): " + string.Join(" | ", clipped.Take(5)));
        // Tablo, iceren panelin SINIRLARI ICINDE kalmali: eylem sutunu (sabit genislik) ve son satir
        // panel kenarindan tasarsa "Yeniden yükle" dugmesi tiklanamaz hale gelir.
        var panel = ui.FindAll<Border>(view).First(x => x.ActualWidth > 900 && x.ActualHeight > 200);
        var gridTopLeft = grid.TranslatePoint(new Point(0, 0), ui.Window);
        var panelTopLeft = panel.TranslatePoint(new Point(0, 0), ui.Window);
        Assert.True(gridTopLeft.X + grid.ActualWidth <= panelTopLeft.X + panel.ActualWidth + 0.5,
            $"tablo panelin sağından taşıyor: {gridTopLeft.X + grid.ActualWidth:0} > {panelTopLeft.X + panel.ActualWidth:0}");
        Assert.True(gridTopLeft.Y + grid.ActualHeight <= panelTopLeft.Y + panel.ActualHeight + 0.5,
            $"tablo panelin altından taşıyor: {gridTopLeft.Y + grid.ActualHeight:0} > {panelTopLeft.Y + panel.ActualHeight:0}");
        Assert.True(grid.Columns.Sum(c => c.ActualWidth) <= grid.ActualWidth + 0.5,
            $"sütun toplamı tabloyu aşıyor: {grid.Columns.Sum(c => c.ActualWidth):0} > {grid.ActualWidth:0}");
        ui.Note($"cihazdaki kartlar tablosu {grid.ActualWidth:0}px, sütunlar {grid.Columns.Sum(c => c.ActualWidth):0}px, panel içinde");
        ui.Shot("rapor-device-cards-10-liste");

        // 3) Sayfalama: 373 kart 50'lik sayfalara boluner.
        Assert.True(expected > 50, $"tohumda {expected} kart var, sayfalama denemesi için yetersiz");
        var firstNo = vm.DeviceCards[0].StudentNo;
        vm.NextCardsPageCommand.Execute(null);
        Journey.Until(ui, () => !vm.IsCardsLoading && vm.CardsPage == 2, "sonraki sayfa");
        Assert.NotEqual(firstNo, vm.DeviceCards[0].StudentNo);
        ui.Shot("rapor-device-cards-11-sayfa2");
        vm.PreviousCardsPageCommand.Execute(null);
        Journey.Until(ui, () => !vm.IsCardsLoading && vm.CardsPage == 1, "önceki sayfa");

        // 4) Arama: ogrenci no / ad / kart -- SQLite ile ayni sonuc.
        var sampleNo = db.Text($"SELECT s.student_no FROM device_card_states st JOIN students s ON s.Id = st.StudentId WHERE st.DeviceId = '{id}' AND st.Status <> 'Removed' ORDER BY s.student_no LIMIT 1")!;
        vm.CardSearch = sampleNo;
        Journey.Run(ui, vm.LoadCardsAsync(1), "no ile arama");
        Assert.Equal(1, vm.CardsTotal);
        Assert.Equal(sampleNo, vm.DeviceCards.Single().StudentNo);

        var sampleCard = db.Text($"SELECT st.CardNumber FROM device_card_states st WHERE st.DeviceId = '{id}' AND st.Status <> 'Removed' ORDER BY st.CardNumber LIMIT 1")!;
        vm.CardSearch = sampleCard;
        Journey.Run(ui, vm.LoadCardsAsync(1), "kart ile arama");
        Assert.Equal(sampleCard, vm.DeviceCards.Single().CardNumber);

        // Ayni ad-soyadlilar (tohumda uc ADA, dort ALI) no + sinif ile ayirt edilir.
        vm.CardSearch = "ADA";
        Journey.Run(ui, vm.LoadCardsAsync(1), "ad ile arama");
        Assert.True(vm.CardsTotal >= 2, $"ADA araması {vm.CardsTotal} sonuç verdi, ayırt edicilik denemesi için yetersiz");
        Assert.All(vm.DeviceCards, row => Assert.StartsWith("ADA", row.StudentName, StringComparison.Ordinal));
        Assert.Equal(vm.DeviceCards.Count, vm.DeviceCards.Select(x => x.StudentNo).Distinct().Count());
        ui.Note("ADA araması: " + string.Join(", ", vm.DeviceCards.Select(x => $"{x.StudentNo}/{x.ClassName}/{x.CardNumber}")));
        ui.Shot("rapor-device-cards-12-arama");

        // Sonuc yoksa "kayıt yok" metni gorunur.
        vm.CardSearch = "ZZZBULUNMAZ";
        Journey.Run(ui, vm.LoadCardsAsync(1), "sonuçsuz arama");
        Assert.Equal(0, vm.CardsTotal);
        Assert.True(vm.IsCardsEmpty);
        Assert.Contains(Journey.TextsIn(ui, view), x => x.Contains("kayıtlı kart yok", StringComparison.Ordinal));
        ui.Shot("rapor-device-cards-13-bos");

        // 5) Satirdaki "Yeniden yükle" yalnizca HATALI kartta etkin; kuyruga alip mesaj gosterir.
        vm.CardSearch = null;
        Journey.Run(ui, vm.LoadCardsAsync(1), "liste");
        Assert.All(vm.DeviceCards.Where(x => x.IsLoaded), row => Assert.False(row.CanResync));
        var target = vm.DeviceCards.FirstOrDefault(x => x.CanResync);
        if (target is not null)
        {
            Journey.Run(ui, vm.ResyncCardAsync(target), "yeniden yükle");
            Assert.Null(vm.Error);
            Assert.Contains("kuyruğuna alındı", vm.StatusMessage!);
            ui.Shot("rapor-device-cards-14-yeniden-yukle");
        }
        else
        {
            ui.Note("hatalı kart yok; 'Yeniden yükle' düğmesi tüm satırlarda pasif (ipucu nedeni yazıyor)");
            Assert.All(vm.DeviceCards, row => Assert.False(string.IsNullOrWhiteSpace(row.ResyncHint)));
        }
    });

    private static HttpRequestMessage Authorized(LiveUiHarness ui, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ui.Session.AccessToken);
        return request;
    }
}
