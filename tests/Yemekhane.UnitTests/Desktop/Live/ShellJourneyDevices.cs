using System.Windows;
using Xunit;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Cihazlar ekrani YALNIZCA RAPOR icin surulur (dosyalar kullanici tarafindan duzenleniyor;
/// burada hicbir sey duzeltilmez). Her gozlem journey-notes.txt'ye yazilir; iddialar gevsektir.
/// </summary>
[Collection("UI")]
public class ShellJourneyDevices
{
    private static bool Until(LiveUiHarness ui, Func<bool> condition, int milliseconds = 10000)
    {
        var end = Environment.TickCount64 + milliseconds;
        while (!condition() && Environment.TickCount64 < end) { ui.Delay(100); ui.Pump(2); }
        return condition();
    }

    private static void Describe(LiveUiHarness ui, string prefix, DeviceCardViewModel card) =>
        ui.Note($"{prefix}: '{card.Name}' Model='{card.Model}' Uc='{card.Endpoint}' Durum='{card.Status}'->'{card.StatusText}' Konum='{card.Location}' Yon='{card.Item.Direction}' Aktif={card.Item.IsActive} Turnike={card.Item.HasTurnstile} Sim={card.Item.IsSimulator} Mesaj='{card.OperationMessage}'");

    [Fact]
    public void CihazlarEkraniRapor() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        ui.Navigate(ShellRoutes.Devices);
        var vm = ui.Devices;
        Assert.True(Until(ui, () => !vm.IsLoading));
        ui.Note($"CIHAZ: {vm.Devices.Count} kayit; SimulatorAllowed={vm.SimulatorAllowed}; turler=[{string.Join(", ", vm.DeviceTypes)}]; yonler=[{string.Join(", ", vm.Directions)}]; hata='{vm.ErrorMessage}'");
        foreach (var card in vm.Devices) Describe(ui, "CIHAZ liste", card);
        ui.Shot("cihaz-01-liste");

        // Ethernet cihaz ekle.
        vm.AddCommand.Execute(null); ui.Pump();
        Assert.True(vm.IsEditorOpen);
        ui.Note($"CIHAZ yeni form varsayilanlari: tur={vm.SelectedType} ip='{vm.IpAddress}' port={vm.Port} com={vm.ComPort} baud={vm.BaudRate} yon={vm.Direction} aktif={vm.IsActive} otoBaglan={vm.AutoConnect} turnike={vm.HasTurnstile} baslik='{vm.EditorTitle}'");
        ui.Shot("cihaz-02-yeni-form");
        vm.Name = "Deneme Ethernet Okuyucu"; vm.SelectedType = "EthernetReader"; vm.IpAddress = "192.168.1.250"; vm.Port = 4370; vm.Location = "Kantin kapısı"; vm.Direction = "Entry";
        vm.SaveCommand.Execute(null);
        Assert.True(Until(ui, () => !vm.IsLoading));
        ui.Note($"CIHAZ ethernet kaydet: editorAcik={vm.IsEditorOpen} hata='{vm.ErrorMessage}'");
        var ethernet = vm.Devices.FirstOrDefault(c => c.Name == "Deneme Ethernet Okuyucu");
        if (ethernet is not null) Describe(ui, "CIHAZ ethernet listede", ethernet);
        else ui.Note("CIHAZ HATA: ethernet cihaz listede yok");

        // COM cihaz ekle.
        vm.AddCommand.Execute(null); ui.Pump();
        vm.Name = "Deneme COM Okuyucu"; vm.SelectedType = "ComReader"; vm.ComPort = "COM7"; vm.BaudRate = 115200; vm.Location = "Yemekhane"; vm.Direction = "Exit";
        vm.SaveCommand.Execute(null);
        Assert.True(Until(ui, () => !vm.IsLoading));
        ui.Note($"CIHAZ com kaydet: editorAcik={vm.IsEditorOpen} hata='{vm.ErrorMessage}'");
        var com = vm.Devices.FirstOrDefault(c => c.Name == "Deneme COM Okuyucu");
        if (com is not null) Describe(ui, "CIHAZ com listede", com);
        ui.Shot("cihaz-03-eklendi");

        // Dogrulama: bos ad, gecersiz IP, gecersiz port.
        vm.AddCommand.Execute(null); ui.Pump();
        vm.Name = ""; vm.SelectedType = "EthernetReader"; vm.IpAddress = "192.168.1.1";
        vm.SaveCommand.Execute(null);
        Assert.True(Until(ui, () => !vm.IsLoading));
        ui.Note($"CIHAZ dogrulama bos ad: editorAcik={vm.IsEditorOpen} hata='{vm.ErrorMessage}'");
        ui.Shot("cihaz-04-dogrulama-bos-ad");
        vm.Name = "Gecersiz IP"; vm.IpAddress = "999.1.1.1";
        vm.SaveCommand.Execute(null);
        Assert.True(Until(ui, () => !vm.IsLoading));
        ui.Note($"CIHAZ dogrulama gecersiz ip: editorAcik={vm.IsEditorOpen} hata='{vm.ErrorMessage}'");
        vm.IpAddress = "192.168.1.1"; vm.Port = 70000;
        vm.SaveCommand.Execute(null);
        Assert.True(Until(ui, () => !vm.IsLoading));
        ui.Note($"CIHAZ dogrulama port 70000: editorAcik={vm.IsEditorOpen} hata='{vm.ErrorMessage}'");
        vm.CloseEditorCommand.Execute(null); ui.Pump();

        // Duzenle -> kaydet.
        if (ethernet is not null)
        {
            vm.EditCommand.Execute(ethernet); ui.Pump();
            ui.Note($"CIHAZ duzenle formu: baslik='{vm.EditorTitle}' ad='{vm.Name}' tur={vm.SelectedType} ip={vm.IpAddress} port={vm.Port} konum='{vm.Location}' yon={vm.Direction}");
            vm.Location = "Kantin kapısı (güncel)"; vm.Direction = "Bidirectional"; vm.HasTurnstile = true;
            vm.SaveCommand.Execute(null);
            Assert.True(Until(ui, () => !vm.IsLoading));
            ui.Note($"CIHAZ duzenle kaydet: editorAcik={vm.IsEditorOpen} hata='{vm.ErrorMessage}' konum='{ethernet.Location}' yon='{ethernet.Item.Direction}' turnike={ethernet.Item.HasTurnstile}");
            ui.Shot("cihaz-05-duzenlendi");
        }

        // Simulator cihazda Baglan/Test/Kes/Yeniden.
        var simulator = vm.Devices.FirstOrDefault(c => c.Item.IsSimulator || c.Item.DeviceType == "Simulator");
        if (simulator is null && vm.SimulatorAllowed)
        {
            vm.AddCommand.Execute(null); ui.Pump();
            vm.Name = "Deneme Simülatör"; vm.SelectedType = "Simulator"; vm.Location = "Test";
            vm.SaveCommand.Execute(null);
            Assert.True(Until(ui, () => !vm.IsLoading));
            ui.Note($"CIHAZ simulator ekle: editorAcik={vm.IsEditorOpen} hata='{vm.ErrorMessage}'");
            simulator = vm.Devices.FirstOrDefault(c => c.Name == "Deneme Simülatör");
        }
        if (simulator is null) ui.Note("CIHAZ: simulator cihaz yok, baglanti eylemleri surulemedi");
        else
        {
            foreach (var action in new[] { (vm.ConnectCommand, "Bağlan"), (vm.TestCommand, "Test"), (vm.ReconnectCommand, "Yeniden"), (vm.DisconnectCommand, "Kes") })
            {
                Assert.True(action.Item1.CanExecute(simulator), action.Item2);
                action.Item1.Execute(simulator);
                Assert.True(Until(ui, () => !simulator.IsBusy, 20000), action.Item2 + " bitmedi");
                Describe(ui, "CIHAZ " + action.Item2, simulator);
            }
            ui.Shot("cihaz-06-simulator-eylemler");
        }

        // Gercek (baglanamayan) cihazda Baglan: hata mesaji nasil?
        var real = vm.Devices.FirstOrDefault(c => !c.Item.IsSimulator && c.Item.IsActive && c.Item.DeviceType != "Simulator");
        if (real is not null)
        {
            vm.TestCommand.Execute(real);
            Assert.True(Until(ui, () => !real.IsBusy, 30000), "test bitmedi");
            Describe(ui, "CIHAZ gercek cihaz Test", real);
        }

        // Loglar cekmecesi.
        var logTarget = real ?? vm.Devices.FirstOrDefault();
        if (logTarget is not null)
        {
            vm.LogsCommand.Execute(logTarget);
            Assert.True(Until(ui, () => vm.IsLogsOpen || logTarget.OperationMessage?.Contains("Log") == true));
            ui.Note($"CIHAZ loglar: acik={vm.IsLogsOpen} adet={vm.Logs.Count} ilk=[{string.Join(" | ", vm.Logs.Take(3).Select(l => $"{l.Timestamp:dd.MM HH:mm:ss zzz} {l.Severity}/{l.EventType}: {l.Message}"))}]");
            ui.Shot("cihaz-07-loglar");
            vm.CloseLogsCommand.Execute(null); ui.Pump();
        }

        // Pasiflestir.
        if (com is not null)
        {
            vm.DeactivateCommand.Execute(com);
            Assert.True(Until(ui, () => !com.Item.IsActive || com.OperationMessage is not null));
            Describe(ui, "CIHAZ pasiflestir", com);
            ui.Note($"CIHAZ pasif cihazda Baglan calistirilabilir mi: {vm.ConnectCommand.CanExecute(com)}");
            ui.Shot("cihaz-08-pasif");
        }

        // Kabuk: F5 ve Esc davranisi bu ekranda.
        vm.LogsCommand.Execute(vm.Devices[0]);
        Until(ui, () => vm.IsLogsOpen);
        ((Yemekhane.Desktop.Services.IShortcutCommandTarget)ui.Window).Execute(ShortcutCommand.CloseTopmost); ui.Pump();
        ui.Note($"CIHAZ Esc loglari kapatti: {!vm.IsLogsOpen}");
        Assert.Equal(Visibility.Visible, ((FrameworkElement)ui.Window.FindName("DevicesHost")!).Visibility);
        ShellJourney.Flush(ui, "cihazlar");
    }, TimeSpan.FromMinutes(8));
}
