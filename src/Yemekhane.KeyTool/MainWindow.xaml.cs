using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Yemekhane.Licensing;

namespace Yemekhane.KeyTool;

public partial class MainWindow : Window
{
    /// <summary>Tek seferde uretilebilecek anahtar sayisi ust siniri.</summary>
    private const int MaximumCount = 200;

    private string? secret;

    public MainWindow()
    {
        InitializeComponent();
        secret = SecretStore.Load();
        ApplySecretState();
        ReloadHistory();
    }

    private void SaveSecretClick(object sender, RoutedEventArgs e)
    {
        var value = SecretBox.Password;
        if (string.IsNullOrWhiteSpace(value))
        {
            Warn("İmza sırrını girin.");
            return;
        }

        // Kisa sir, anahtar imzasini tahmin edilebilir kilar. Kurulum sirri 48 bayt
        // base64 olarak uretilir; buradaki alt sinir yalnizca kaba bir kontroldur.
        if (value.Trim().Length < 16)
        {
            Warn("İmza sırrı çok kısa görünüyor. Kurulumu üretirken kullandığınız sırrın tamamını yapıştırın.");
            return;
        }

        secret = value.Trim();
        SecretStore.Save(secret);
        SecretBox.Clear();
        ApplySecretState();
        Say("İmza sırrı bu bilgisayara şifrelenerek kaydedildi.");
    }

    private void ChangeSecretClick(object sender, RoutedEventArgs e)
    {
        // Kayitli sir SILINIR: yenisi kaydedilene kadar anahtar uretilemez. Eskisini
        // bellekte tutup "yedek" gibi kullanmak, kullanicinin hangi sirla urettigini
        // bilememesine yol acardi.
        SecretStore.Clear();
        secret = null;
        ApplySecretState();
        Say("Kayıtlı sır silindi. Yeni sırrı girip Kaydet'e basın.");
    }

    private void GenerateClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            Warn("Önce imza sırrını kaydedin.");
            return;
        }

        var customer = CustomerBox.Text.Trim();
        if (customer.Length == 0)
        {
            Warn("Okul / müşteri adını yazın. Sunucusuz modda kime ne sattığınızı başka hiçbir yer bilmez.");
            CustomerBox.Focus();
            return;
        }

        if (!int.TryParse(CountBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            || count < 1 || count > MaximumCount)
        {
            Warn($"Adet 1 ile {MaximumCount} arasında bir sayı olmalı.");
            CountBox.Focus();
            return;
        }

        var note = NoteBox.Text.Trim();
        var now = DateTimeOffset.Now;
        var keys = new List<string>(count);

        try
        {
            for (var index = 0; index < count; index++)
            {
                var key = OfflineLicenseKey.Create(now, secret);
                SalesLog.Append(new SaleRecord(key, customer, note, now));
                keys.Add(key);
            }
        }
        // Kayit tutulamiyorsa uretmek TEHLIKELIDIR: anahtari musteriye verirsiniz ama
        // kimde oldugunu bilemezsiniz. Bu yuzden hata yutulmaz.
        catch (IOException exception)
        {
            Warn("Satış kaydı yazılamadı, anahtar üretilmedi: " + exception.Message);
            return;
        }

        LatestKeyBox.Text = string.Join(Environment.NewLine, keys);
        LatestPanel.Visibility = Visibility.Visible;
        NoteBox.Clear();
        ReloadHistory();
        Say(count == 1
            ? "Anahtar üretildi ve satış geçmişine yazıldı."
            : $"{count} anahtar üretildi ve satış geçmişine yazıldı.");
    }

    /// <summary>
    /// Makine kodu degistikce dogrulanir: gecerliyse hangi bilgisayara ait oldugu
    /// gosterilir. Boylece satici, dosyayi URETMEDEN once dogru makineye baktigini
    /// karsilastirabilir -- yanlis makineye kilitli dosya gondermek, musterinin
    /// "calismiyor" demesiyle ortaya cikan pahali bir hatadir.
    /// </summary>
    private void MachineCodeChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var code = MachineCodeBox.Text;
        if (string.IsNullOrWhiteSpace(code))
        {
            MachineCodeHint.Text = string.Empty;
            MakeFileButton.IsEnabled = false;
            return;
        }

        var machineId = MachineCode.MachineIdOf(code);
        if (machineId is null)
        {
            MachineCodeHint.Text = "Kod okunamadı. Müşteriden kodun TAMAMINI yeniden göndermesini isteyin.";
            MakeFileButton.IsEnabled = false;
            return;
        }

        MachineCodeHint.Text = $"Bilgisayar kimliği: {machineId} — müşterinin ekranında yazan ile aynı olmalı.";
        MakeFileButton.IsEnabled = !string.IsNullOrWhiteSpace(secret);
    }

    /// <summary>
    /// Makine koduna kilitli lisans dosyasi uretir ve kaydeder.
    /// </summary>
    private void MakeFileClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            Warn("Önce imza sırrını kaydedin.");
            return;
        }

        var hashes = MachineCode.Parse(MachineCodeBox.Text);
        if (hashes is null)
        {
            Warn("Makine kodu geçersiz.");
            return;
        }

        var customer = CustomerBox.Text.Trim();
        if (customer.Length == 0)
        {
            Warn("Okul / müşteri adını yazın.");
            CustomerBox.Focus();
            return;
        }

        var machineId = new HardwareFingerprint(hashes).MachineId;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Lisans dosyasını kaydet",
            Filter = "Lisans dosyası (*.lic)|*.lic",
            FileName = LicenseFile.SuggestFileName(customer, machineId)
        };
        if (dialog.ShowDialog(this) != true) return;

        // Anahtar da uretilir: dosyanin icinde tasinir ve satis kaydinda gorunur,
        // boylece hangi lisansin kime gittigi izlenebilir kalir.
        var key = OfflineLicenseKey.Create(DateTimeOffset.Now, secret);
        var license = LicenseIssuer.Issue(key, customer, "Standart", hashes,
            DateTimeOffset.Now, expiresAt: null, secret);

        try
        {
            File.WriteAllText(dialog.FileName, LicenseFile.Write(license));
            SalesLog.Append(new SaleRecord(key, customer,
                string.IsNullOrWhiteSpace(NoteBox.Text) ? $"dosya · {machineId}"
                    : $"{NoteBox.Text.Trim()} · dosya · {machineId}",
                DateTimeOffset.Now));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Warn("Dosya yazılamadı: " + exception.Message);
            return;
        }

        MachineCodeBox.Clear();
        NoteBox.Clear();
        ReloadHistory();
        Say($"Lisans dosyası kaydedildi: {Path.GetFileName(dialog.FileName)} — yalnızca {machineId} kimlikli bilgisayarda çalışır.");
    }

    private void CopyClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LatestKeyBox.Text)) return;
        try
        {
            Clipboard.SetText(LatestKeyBox.Text);
            Say("Panoya kopyalandı. Müşteriye gönderebilirsiniz.");
        }
        // Pano baska bir uygulama tarafindan kilitlenmis olabilir; cokmek yerine soyle.
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException)
        {
            Warn("Panoya kopyalanamadı. Anahtarı elle seçip kopyalayabilirsiniz.");
        }
    }

    private void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(SalesLog.FilePath)!;
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    private void ApplySecretState()
    {
        var hasSecret = !string.IsNullOrWhiteSpace(secret);
        SecretEntry.Visibility = hasSecret ? Visibility.Collapsed : Visibility.Visible;
        SecretSaved.Visibility = hasSecret ? Visibility.Visible : Visibility.Collapsed;
        SaveSecretButton.Visibility = hasSecret ? Visibility.Collapsed : Visibility.Visible;
        ChangeSecretButton.Visibility = hasSecret ? Visibility.Visible : Visibility.Collapsed;
        GenerateButton.IsEnabled = hasSecret;
        // Dosya dugmesi HEM sir HEM gecerli makine kodu ister; sir kaydedildiginde
        // kod kutusu doluysa acilmalidir, aksi halde kullanici kodu silip yeniden
        // yapistirmak zorunda kalirdi.
        MakeFileButton.IsEnabled = hasSecret && MachineCode.MachineIdOf(MachineCodeBox.Text) is not null;

        // Sirrin KENDISI gosterilmez; yalnizca dogru sir mi diye ayirt etmeye yetecek
        // kadar ipucu verilir.
        if (hasSecret && secret!.Length >= 4)
            SecretHint.Text = $"…{secret[^4..]} ile biten sır kullanılıyor";
    }

    private void ReloadHistory()
    {
        var rows = SalesLog.Load();
        HistoryGrid.ItemsSource = rows;
        CountLabel.Text = rows.Count == 0 ? "Henüz kayıt yok" : $"{rows.Count} anahtar";
    }

    private void Say(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = System.Windows.Media.Brushes.DimGray;
    }

    private void Warn(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = System.Windows.Media.Brushes.Firebrick;
    }
}
