using System.Net.Http;
using System.Windows.Controls;
using Xunit;
using Yemekhane.Application.Devices;

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

    private static HttpRequestMessage Authorized(LiveUiHarness ui, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ui.Session.AccessToken);
        return request;
    }
}
