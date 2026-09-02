using System.IO;
using System.Windows.Controls;
using Xunit;
using Yemekhane.Application.Settings;
using Yemekhane.Application.Students;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Sistem Ayarlari: alti sekmenin tamami gercek API ile surulur. Kaydedilen her deger
/// API'den GERI OKUNARAK dogrulanir; yedekleme dosyasi diskte aranir; geri yukleme
/// gercek veriyle (ogrenci ekle -> yedek al -> pasiflestir -> geri yukle) sinanir.
/// </summary>
[Collection("UI")]
public class SettingsJourney
{
    [Fact]
    public void AltiSekmeninTamAkisi() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        ui.Navigate("settings");
        var vm = ui.Settings;
        var tabs = ui.TabControlWith("Okul");
        Assert.NotNull(tabs);
        Assert.False(vm.IsDirty);
        ui.Shot("settings-01-okul");

        // ================= OKUL: degistir -> kirli -> kaydet -> API'den geri oku -> iptal -> yenile
        var schoolName = "Şükrü Çağlayan İlköğretim Okulu " + DateTime.Now.ToString("HHmmss");
        const string address = "Güneş Mah. Işık Sk. No:3 Çankaya/ANKARA";
        vm.SchoolName = schoolName; vm.SchoolAddress = address; vm.SchoolContact = "0312 555 44 33";
        Assert.True(vm.IsDirty);
        Assert.True(vm.SaveCommand.CanExecute(null));
        ui.Shot("settings-02-okul-kirli");
        vm.SaveCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
        Assert.False(vm.IsDirty);
        var doc = LiveApi.Get<SettingsDocument>(ui, "api/settings");
        Assert.Equal(schoolName, doc.School.Name);
        Assert.Equal(address, doc.School.Address);
        Assert.Equal("0312 555 44 33", doc.School.Contact);

        vm.SchoolName = "Bu kaydedilmeyecek";
        Assert.True(vm.IsDirty);
        vm.CancelCommand.Execute(null);
        Assert.Equal(schoolName, vm.SchoolName);
        Assert.False(vm.IsDirty);
        vm.RefreshCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.Equal(schoolName, vm.SchoolName);

        // ================= SMS: kimlik dogrulama Turkce; zaman asimi dogrulamasi; gizli bilgi
        tabs!.SelectedIndex = 2; ui.Pump();
        Assert.All(vm.SmsAuthTypes, o => Assert.NotEqual("None", o.Name));
        var authCombo = ui.FindAll<ComboBox>().First(c => c.ItemsSource == vm.SmsAuthTypes);
        // Yolculuk tekrar kosulabilir: ekranda o anki degerin TURKCE adi gorunmeli, ham kod degil.
        Assert.Equal(vm.SmsAuthTypes.Single(o => o.Value == vm.SmsAuthType).Name, authCombo.Text);
        Assert.DoesNotContain(authCombo.Text, new[] { "None", "Basic", "Bearer", "ApiKey" });
        ui.Shot("settings-03-sms");

        vm.SmsTimeoutText = "abc";
        Assert.True(vm.IsDirty);
        vm.SaveCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.Contains("sayı olmalıdır", vm.ErrorMessage, StringComparison.Ordinal);
        Assert.False(vm.IsOffline);
        ui.Shot("settings-04-sms-hatali-sayi");
        vm.SmsTimeoutText = "-5";
        vm.SaveCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.Contains("1 ile 300", vm.ErrorMessage, StringComparison.Ordinal);

        vm.SmsTimeoutText = "45"; vm.SmsAuthType = "Basic"; vm.SmsUsername = "okul"; vm.SmsSender = "OKUL";
        vm.SetSmsSecret("gizli-123");
        vm.SaveCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
        Assert.True(vm.SmsSecretConfigured, "gizli bilgi işareti yanmadı");
        Assert.Equal(45, vm.SmsTimeoutSeconds);
        Assert.Contains("yeniden başlat", vm.StatusMessage, StringComparison.Ordinal);
        doc = LiveApi.Get<SettingsDocument>(ui, "api/settings");
        Assert.Equal("Basic", doc.Sms.AuthType);
        Assert.Equal(45, doc.Sms.TimeoutSeconds);
        Assert.True(doc.Sms.SecretConfigured);
        Assert.Equal("Temel (kullanıcı adı / şifre)", authCombo.Text);
        ui.Shot("settings-05-sms-kaydedildi");

        // ================= YEDEKLEME: dogrulama, kaydet, simdi yedekle, dogrula, onay metni, geri yukle
        tabs.SelectedIndex = 3; ui.Pump();
        ui.Shot("settings-06-yedekleme");
        vm.BackupEnabled = true; vm.BackupFrequency = "Weekly"; vm.BackupWeeklyDay = DayOfWeek.Friday; vm.BackupTime = "25:99";
        vm.SaveCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.Contains("SS:dd", vm.ErrorMessage, StringComparison.Ordinal);
        ui.Shot("settings-07-yedekleme-saat-hatasi");
        vm.BackupTime = "03:30"; vm.BackupRetentionText = "0";
        vm.SaveCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.Contains("1 ile 365", vm.ErrorMessage, StringComparison.Ordinal);
        vm.BackupRetentionText = "-1";
        vm.SaveCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.Contains("1 ile 365", vm.ErrorMessage, StringComparison.Ordinal);
        vm.BackupRetentionText = "abc";
        vm.SaveCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.Contains("sayı olmalıdır", vm.ErrorMessage, StringComparison.Ordinal);
        // Yolculuk tekrar kosulabilir: bir onceki kosudan kalan degerden FARKLI bir sayi secilir
        // ki gercekten bir kayit istegi gitsin.
        var retention = doc.Backup.RetentionCount == 2 ? 3 : 2;
        vm.BackupRetentionText = retention.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(vm.ErrorMessage is null, "Geçerli değer girilince eski uyarı kalkmalıydı: " + vm.ErrorMessage);
        Assert.True(vm.IsDirty);
        vm.SaveCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
        doc = LiveApi.Get<SettingsDocument>(ui, "api/settings");
        Assert.True(doc.Backup.Enabled);
        Assert.Equal("Weekly", doc.Backup.Frequency);
        Assert.Equal(DayOfWeek.Friday, doc.Backup.WeeklyDay);
        Assert.Equal(new TimeOnly(3, 30), doc.Backup.Time);
        Assert.Equal(retention, doc.Backup.RetentionCount);

        // Geri yukleme senaryosu icin once bir ogrenci ekle.
        var studentNo = "YDK" + DateTime.Now.ToString("HHmmss");
        var created = LiveApi.Post<StudentDetails>(ui, "api/students", new { studentNo, firstName = "Yedek", lastName = "Deneme" });
        Assert.True(created.IsActive);

        var dataDir = Path.GetDirectoryName(LiveUiHarness.ShotDir.TrimEnd('\\', '/'))!;
        var backupDir = Path.Combine(dataDir, "Backups");
        vm.BackupNowCommand.Execute(null); ui.Delay(5000); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
        Assert.StartsWith("Yedek oluşturuldu", vm.StatusMessage, StringComparison.Ordinal);
        Assert.NotNull(vm.LastBackupFile);
        var backupFile = Path.Combine(backupDir, vm.LastBackupFile!);
        Assert.True(File.Exists(backupFile), "yedek dosyası diskte yok: " + backupFile);
        ui.Note("yedek dosyası: " + backupFile + " (" + new FileInfo(backupFile).Length + " bayt)");
        ui.Shot("settings-08-yedek-alindi");

        vm.RestorePath = backupFile;
        Assert.True(vm.ValidateBackupCommand.CanExecute(null));
        vm.ValidateBackupCommand.Execute(null); ui.Delay(5000); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
        Assert.StartsWith("Yedek doğrulandı", vm.StatusMessage, StringComparison.Ordinal);

        vm.RestoreConfirmation = "geri yukle";
        Assert.False(vm.RestoreCommand.CanExecute(null));
        vm.RestoreConfirmation = "GERİ YÜKLE";
        Assert.False(vm.RestoreCommand.CanExecute(null));
        Assert.Contains("eşleşmiyor", vm.RestoreConfirmationHint, StringComparison.Ordinal);
        ui.Shot("settings-09-onay-yanlis");
        vm.RestoreConfirmation = "GERI YUKLE";
        Assert.True(vm.RestoreCommand.CanExecute(null));

        // Ogrenciyi sil (API yumusak siler; sorgu filtresi 404 dondurur), sonra yedekten
        // geri don: ogrenci yeniden bulunmali ve aktif olmali.
        using (var deleted = LiveApi.Delete(ui, $"api/students/{created.Id:D}"))
            Assert.True(deleted.IsSuccessStatusCode, "öğrenci silinemedi: " + (int)deleted.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, LiveApi.StatusOf(ui, $"api/students/{created.Id:D}"));

        vm.RestoreCommand.Execute(null); ui.Delay(10000); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
        Assert.Contains("Geri yükleme tamamlandı", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(System.Net.HttpStatusCode.OK, LiveApi.StatusOf(ui, $"api/students/{created.Id:D}"));
        Assert.True(LiveApi.Get<StudentDetails>(ui, $"api/students/{created.Id:D}").IsActive, "geri yükleme sonrası öğrenci geri gelmedi");
        Assert.NotEmpty(Directory.GetFiles(backupDir, "okulyemek-pre-restore-*.zip"));
        ui.Note($"yedek klasörü: düzenli {Directory.GetFiles(backupDir, "okulyemek-backup-*.zip").Length}, güvenlik {Directory.GetFiles(backupDir, "okulyemek-pre-restore-*.zip").Length}");
        ui.Shot("settings-10-geri-yuklendi");
        vm.BackupEnabled = false;
        vm.SaveCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);

        // ================= SENKRONIZASYON: uc nokta yokken kaydetme reddi; calistirma hatasi Turkce
        tabs.SelectedIndex = 4; ui.Pump();
        ui.Shot("settings-11-sync");
        Assert.False(vm.SyncNowCommand.CanExecute(null));
        vm.SyncEnabled = true;
        vm.SaveCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.Contains("zorunlu", vm.ErrorMessage, StringComparison.Ordinal);
        vm.SyncEndpoint = "https://esitleme.example.invalid/"; vm.SyncDeviceId = "okul-pc-1"; vm.SyncIntervalText = "15";
        vm.SaveCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
        Assert.True(vm.SyncNowCommand.CanExecute(null));
        vm.SyncNowCommand.Execute(null); ui.Delay(15000); ui.Pump();
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage), "ulaşılamayan uç nokta için hata gösterilmedi");
        Assert.DoesNotContain("Exception", vm.ErrorMessage, StringComparison.Ordinal);
        ui.Note("sync çalıştırma mesajı: " + vm.ErrorMessage);
        ui.Shot("settings-12-sync-hatasi");
        vm.SyncEnabled = false;
        vm.SaveCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);

        // ================= LOGLAR: seviye degistir, kaydet, yenile; sutunlar kesik degil
        tabs.SelectedIndex = 5; ui.Pump();
        vm.LogLevel = "Warning"; vm.LogRetentionText = "0";
        vm.SaveCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.Contains("1 ile 3650", vm.ErrorMessage, StringComparison.Ordinal);
        vm.LogRetentionText = "45";
        vm.SaveCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
        doc = LiveApi.Get<SettingsDocument>(ui, "api/settings");
        Assert.Equal("Warning", doc.Logs.Level);
        Assert.Equal(45, doc.Logs.RetentionDays);
        vm.RefreshLogsCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.Logs.Count > 0, "log listesi boş");
        var grid = ui.FindAll<DataGrid>().First(g => g.ItemsSource == vm.Logs);
        Assert.All(grid.Columns, c => Assert.True(c.ActualWidth > 40, $"'{c.Header}' sütunu {c.ActualWidth:F0}px"));
        var texts = ui.FindAll<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(texts, t => t is "Bilgi" or "Hata" or "Uyarı");
        Assert.DoesNotContain(texts, t => t is "Information" or "Error" or "Warning");
        ui.Shot("settings-13-loglar");
        // Sonraki kosular icin baslangic degerlerine don.
        vm.LogLevel = "Information"; vm.SmsAuthType = "None";
        vm.SaveCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.ErrorMessage is null, "Beklenmeyen hata: " + vm.ErrorMessage);
    }, TimeSpan.FromMinutes(15));
}
