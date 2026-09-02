using System.Windows.Controls;
using Xunit;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Toplu islem sihirbazi: Hakedisler listesinden secimle acilir, 7 adim sirayla
/// gecilir, uygulanir, gecmisten geri alinir; her adimda liste ve SQLite dogrulanir.
/// </summary>
[Collection("UI")]
public class EntitlementsJourneyBulk
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void YediAdimUygulaGecmisGeriAl() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("entitlements");
        var vm = ui.Entitlements; var wizard = ui.EntitlementBulk;
        Assert.NotNull(vm.BulkWizard); Assert.Same(wizard, vm.BulkWizard);

        // 2026-09-08 gununden iki AKTIF 5B satiri sec
        vm.StartsOn = new DateTime(2026, 9, 8); vm.EndsOn = new DateTime(2026, 9, 8); vm.ClassName = "5B"; vm.Status = "Active";
        Assert.True(LiveUiHarness.Wait(vm.LoadAsync(1), Timeout)); ui.Pump();
        Assert.True(vm.Items.Count >= 2, "5B icin en az iki satir bekleniyordu");
        var grid = ui.FindAll<DataGrid>().Single(x => x.Name == "EntitlementsGrid");
        grid.SelectedItems.Clear(); grid.SelectedItems.Add(vm.Items[0]); grid.SelectedItems.Add(vm.Items[1]); ui.Pump();
        var picked = vm.SelectedItems.Select(x => (x.Id, x.StudentNo, x.StudentId)).ToArray();

        vm.OpenBulkCommand.Execute(null); ui.Pump();
        Assert.True(wizard.IsOpen); Assert.Equal(1, wizard.Step);
        ui.Shot("bulk-01-adim1-islem");
        Assert.Equal("CancelEntitlements", wizard.Operation);

        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        Assert.Equal(2, wizard.Step);
        Assert.True(wizard.IsManualScope, "secimle acilinca kapsam Manuel olmali");
        Assert.All(picked, p => Assert.Contains(p.StudentId.ToString(), wizard.ManualStudentIds));
        ui.Shot("bulk-02-adim2-kapsam");

        // Zorunlu alan bos: Manuel kapsamda ogrenci yoksa Ileri gitmez, Turkce mesaj
        var keep = wizard.ManualStudentIds; wizard.ManualStudentIds = ""; ui.Pump();
        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        Assert.Equal(2, wizard.Step); Assert.True(wizard.HasError); Assert.Contains("numara", wizard.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        ui.Shot("bulk-02b-adim2-bos-hata");
        wizard.ManualStudentIds = keep;

        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        Assert.Equal(3, wizard.Step); Assert.False(wizard.HasError, wizard.ErrorMessage);
        wizard.StartsOn = new DateTime(2026, 9, 8); wizard.EndsOn = new DateTime(2026, 9, 8); ui.Pump();
        ui.Shot("bulk-03-adim3-tarih");

        // Bitis < baslangic: Ileri gitmez
        wizard.EndsOn = new DateTime(2026, 9, 7); wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        Assert.Equal(3, wizard.Step); Assert.True(wizard.HasError);
        wizard.EndsOn = new DateTime(2026, 9, 8);

        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        Assert.Equal(4, wizard.Step);
        Assert.Equal("Delete", wizard.TransferBehavior);
        ui.Shot("bulk-04-adim4-davranis");

        wizard.NextCommand.Execute(null); ui.Delay(2500); ui.Pump();
        Assert.Equal(5, wizard.Step); Assert.False(wizard.HasError, wizard.ErrorMessage);
        Assert.NotNull(wizard.Preview);
        Assert.Equal(2, wizard.Preview!.StudentCount); Assert.Equal(2, wizard.Preview.EntitlementCount);
        Assert.Equal(2, wizard.Preview.CancelledCount);
        // Onizleme tablosu ogrenciyi no + ad + sinif ile gosterir (GUID degil)
        Assert.All(wizard.Preview.Entitlements, x => { Assert.False(string.IsNullOrEmpty(x.StudentNo)); Assert.False(string.IsNullOrEmpty(x.StudentName)); Assert.Equal("5B", x.ClassName); });
        Assert.All(picked, p => Assert.Contains(wizard.Preview.Entitlements, x => x.StudentNo == p.StudentNo));
        ui.Shot("bulk-05-adim5-onizleme");

        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        Assert.Equal(6, wizard.Step);
        ui.Note("onay metni: " + wizard.ConfirmationText);
        Assert.DoesNotContain("CancelEntitlements", wizard.ConfirmationText);
        Assert.Contains("Hak iptali", wizard.ConfirmationText);
        ui.Shot("bulk-06-adim6-onay");

        // Geri / Ileri calisir
        wizard.BackCommand.Execute(null); ui.Pump(); Assert.Equal(5, wizard.Step);
        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump(); Assert.Equal(6, wizard.Step);

        wizard.ApplyCommand.Execute(null); ui.Delay(3500); ui.Pump();
        Assert.True(wizard.Step == 7, $"uygulama basarisiz (adim {wizard.Step}): {wizard.ErrorMessage}");
        Assert.NotNull(wizard.ResultMessage);
        ui.Note("sonuc: " + wizard.ResultMessage);
        ui.Shot("bulk-07-adim7-sonuc");
        foreach (var p in picked)
            Assert.Equal(1, LiveDb.Scalar("select count(*) from meal_entitlements where upper(Id)=@p0 and Status='Cancelled'", p.Id.ToString().ToUpperInvariant()));
        // Sihirbaz uygulayinca arkadaki liste kendini yeniledi (Changed olayi)
        ui.Delay(2000); ui.Pump();
        Assert.All(picked, p => Assert.DoesNotContain(vm.Items, x => x.Id == p.Id && x.Status == "Active"));

        // Gecmis: kayit var, geri alinabilir, Turkce durum
        wizard.OpenHistoryCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(wizard.IsHistoryOpen); Assert.True(wizard.History.Count >= 1);
        var last = wizard.History[0];
        Assert.Equal("CancelEntitlements", last.Operation); Assert.Equal("Completed", last.Status); Assert.True(last.CanUndo);
        ui.Shot("bulk-08-gecmis");
        var texts = ui.FindAll<TextBlock>().Select(x => x.Text).ToList();
        Assert.Contains("Hak iptali / aktarımı", texts); Assert.Contains("Tamamlandı", texts);
        Assert.DoesNotContain("Completed", texts); Assert.DoesNotContain("CancelEntitlements", texts);

        wizard.UndoCommand.Execute(last); ui.Delay(3500); ui.Pump();
        Assert.False(wizard.HasError, wizard.ErrorMessage);
        Assert.NotNull(wizard.ResultMessage);
        ui.Note("geri alma: " + wizard.ResultMessage);
        foreach (var p in picked)
            Assert.Equal(1, LiveDb.Scalar("select count(*) from meal_entitlements where upper(Id)=@p0 and Status='Active'", p.Id.ToString().ToUpperInvariant()));
        Assert.Equal("Reverted", wizard.History[0].Status); Assert.False(wizard.History[0].CanUndo);
        ui.Shot("bulk-09-geri-alindi");
        // Liste de geri alindi
        vm.Status = "Active"; Assert.True(LiveUiHarness.Wait(vm.LoadAsync(1), Timeout)); ui.Pump();
        Assert.All(picked, p => Assert.Contains(vm.Items, x => x.Id == p.Id && x.Status == "Active"));

        wizard.CloseHistoryCommand.Execute(null); wizard.CloseCommand.Execute(null); ui.Pump();
        Assert.False(wizard.IsOpen);
    });

    [Fact]
    public void ManuelKapsamOgrenciNumarasiKabulEder() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("entitlements");
        var wizard = ui.EntitlementBulk;
        wizard.OpenCommand.Execute(null); ui.Pump();
        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        wizard.SelectedScope = wizard.Scopes.Single(x => x.ScopeType == "Manual");
        wizard.ManualStudentIds = "5020, 5021, 5999";
        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        wizard.StartsOn = new DateTime(2026, 9, 9); wizard.EndsOn = new DateTime(2026, 9, 9);
        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        Assert.Equal(4, wizard.Step);
        wizard.NextCommand.Execute(null); ui.Delay(2500); ui.Pump();
        // 5999 yok: sunucu adiyla reddeder, adim 4'te kalir
        Assert.Equal(4, wizard.Step); Assert.True(wizard.HasError); Assert.Contains("5999", wizard.ErrorMessage);
        ui.Shot("bulk-10-bilinmeyen-no");
        wizard.BackCommand.Execute(null); wizard.BackCommand.Execute(null); ui.Pump();
        Assert.Equal(2, wizard.Step);
        wizard.ManualStudentIds = "5020, 5021";
        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        wizard.NextCommand.Execute(null); ui.Delay(2500); ui.Pump();
        Assert.Equal(5, wizard.Step); Assert.Equal(2, wizard.Preview!.StudentCount);
        Assert.Contains(wizard.Preview.Entitlements, x => x.StudentNo == "5020");
        wizard.CloseCommand.Execute(null); ui.Pump();
    });
}
