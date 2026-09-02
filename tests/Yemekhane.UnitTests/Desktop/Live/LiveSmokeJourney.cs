using Xunit;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>Iskeletin kendisini dogrular: giris, tum ekranlarin yuklenmesi, her rotaya gecis ve ekran cekimi.</summary>
[Collection("UI")]
public class LiveSmokeJourney
{
    [Fact]
    public void TumEkranlarYuklenirVeCekilir() => LiveUiHarness.Run(ui =>
    {
        Assert.True(ui.Permissions.Count > 20, $"admin izinleri okunamadi: {ui.Permissions.Count}");
        ui.LoadAll();
        Assert.Empty(ui.Log.Where(x => x.StartsWith("YÜKLEME HATASI") || x.StartsWith("ZAMAN")));
        Assert.True(ui.Students.Students.Count > 0, "ogrenci listesi bos geldi");

        foreach (var route in new[] { "dashboard", "daily-tracking", "students", "cash", "entitlements",
                     "holiday-transfer", "reports", "sms", "devices", "device-cards", "student-import", "settings", "definitions" })
        {
            ui.Navigate(route);
            ui.Shot("smoke-" + route);
        }
    });
}
