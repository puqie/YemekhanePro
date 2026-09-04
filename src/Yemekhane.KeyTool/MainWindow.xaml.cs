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
    private LicenseKeyPair? keyPair;

    public MainWindow()
    {
        InitializeComponent();
        secret = SecretStore.Load();
        keyPair = SecretStore.LoadKeyPair();
        ApplyKeyPairState();
        ApplySecretState();
        ReloadHistory();
    }

    /// <summary>
    /// Anahtar cifti uretir. BIR KEZ yapilir; sonra hep ayni cift kullanilir.
    ///
    /// Yeni cift uretmek, daha once satilmis TUM lisanslari gecersiz kilar --
    /// eski kurulumlardaki acik anahtar yeni imzalari dogrulayamaz. Bu yuzden
    /// mevcut cift varken onay istenir.
    /// </summary>
    private void CreateKeyPairClick(object sender, RoutedEventArgs e)
    {
        if (keyPair is not null)
        {
            var answer = MessageBox.Show(
                "Zaten bir anahtar çiftiniz var.\n\n" +
                "Yeni çift üretirseniz DAHA ÖNCE SATTIĞINIZ TÜM LİSANSLAR geçersiz olur; " +
                "her müşteriye yeni kurulum ve yeni lisans göndermeniz gerekir.\n\n" +
                "Yine de yeni çift üretilsin mi?",
                "Anahtar çifti değiştir", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        keyPair = LicenseKeyPairFactory.Create();
        SecretStore.SaveKeyPair(keyPair);
        ApplyKeyPairState();
        Say("Anahtar çifti üretildi. Açık anahtarı kopyalayıp kurulumu üretin.");
    }

    /// <summary>
    /// Kurulum exesini uretir. Acik anahtar zaten burada oldugu icin kullaniciya
    /// sorulmaz; kopyalama, ortam degiskeni ve PowerShell adimlarinin tamami kalkti.
    /// </summary>
    private async void BuildInstallerClick(object sender, RoutedEventArgs e)
    {
        if (keyPair is null)
        {
            Warn("Önce anahtar çifti üretin.");
            return;
        }

        var version = VersionBox.Text.Trim();
        if (!InstallerBuilder.IsValidVersion(version))
        {
            Warn("Sürüm 1.3.0 gibi üç parçalı olmalı.");
            VersionBox.Focus();
            return;
        }

        // Arac yayinlanmis klasorden calisiyorsa depo yoktur ve uretim yapilamaz.
        // Sessizce denemek yerine ACIKCA soylenir.
        var repository = InstallerBuilder.FindRepositoryRoot(AppContext.BaseDirectory);
        if (repository is null)
        {
            Warn("Kaynak klasörü bulunamadı. Kurulum üretimi için bu aracı proje klasöründen çalıştırın.");
            return;
        }

        var target = InstallerBuilder.OutputPathFor(repository, version);
        if (File.Exists(target))
        {
            // Ayni surumu yeniden uretmek eskisini SILER. Musteriye gonderilmis bir
            // surumu farkinda olmadan degistirmek, hangi kurulumun nerede oldugunu
            // izlenemez kilardi.
            var answer = MessageBox.Show(
                $"{version} sürümü zaten üretilmiş.{Environment.NewLine}{Environment.NewLine}" +
                "Yeniden üretilirse eski dosya silinir. Devam edilsin mi?",
                "Sürüm zaten var", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        SetBuilding(true);
        Say($"{version} üretiliyor… Birkaç dakika sürer, pencereyi kapatmayın.");

        // ACIK anahtar gecirilir; ozel anahtar bu bilgisayardan ASLA cikmaz.
        var result = await InstallerBuilder.BuildAsync(repository, version, keyPair.PublicKey, CancellationToken.None);

        SetBuilding(false);

        if (!result.Succeeded)
        {
            Warn("Kurulum üretilemedi. Ayrıntı için açılan pencereye bakın.");
            ShowBuildLog(result.Log);
            return;
        }

        Say($"Kurulum hazır: {Path.GetFileName(result.OutputPath!)} — okula bu tek dosyayı gönderin.");
        Reveal(result.OutputPath!);
    }

    /// <summary>Uretim sirasinda dugmeleri kilitler: iki uretim ayni klasore yazardi.</summary>
    private void SetBuilding(bool building)
    {
        BuildInstallerButton.IsEnabled = !building;
        BuildInstallerButton.Content = building ? "Üretiliyor…" : "Kurulum exesi üret";
        CreateKeyPairButton.IsEnabled = !building;
        VersionBox.IsEnabled = !building;
        Cursor = building ? System.Windows.Input.Cursors.Wait : null;
    }

    /// <summary>Uretim gunlugunu ayri bir pencerede gosterir; hatalar uzundur.</summary>
    private void ShowBuildLog(string log)
    {
        var window = new Window
        {
            Title = "Kurulum üretim günlüğü",
            Width = 900,
            Height = 560,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new System.Windows.Controls.TextBox
            {
                Text = log,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 11,
            },
        };
        window.ShowDialog();
    }

    /// <summary>Dosyayi Gezgin'de secili olarak acar: kullanici aramak zorunda kalmaz.</summary>
    private static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        // Gezgin acilamamasi uretimi gecersiz kilmaz; dosya yerinde duruyor.
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
        }
    }

    /// <summary>
    /// Makine kodunu panodan alir. Kod her zaman kopyalanarak gelir; elle
    /// yapistirmak yerine tek dugme, kodun bir kismini secip yapistirma hatasini
    /// da onler.
    /// </summary>
    private void PasteCodeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text))
            {
                Warn("Pano boş. Okuldan gelen makine kodunu kopyalayıp yeniden deneyin.");
                return;
            }
            MachineCodeBox.Text = text.Trim();
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException)
        {
            Warn("Pano okunamadı. Kodu kutuya elle yapıştırabilirsiniz.");
        }
    }

    private void ApplyKeyPairState()
    {
        var has = keyPair is not null;
        PublicKeyArea.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        CreateKeyPairButton.Content = has ? "Yeni çift üret" : "Anahtar çifti üret";
        KeyPairTitle.Text = has ? "Lisans anahtar çifti hazır" : "Lisans anahtar çifti";
        KeyPairHint.Text = has
            // Ozel anahtar ASLA ekranda gosterilmez: ekran goruntusu, omuz ustunden
            // bakma ve ekran paylasimi hepsi sizinti yoludur.
            ? "Özel anahtar bu bilgisayarda şifreli saklanıyor ve hiçbir yere gönderilmez."
            : "Önce bir anahtar çifti üretin. Özel anahtar burada kalır; kuruluma yalnızca açık anahtar girer.";
        if (has) PublicKeyBox.Text = keyPair!.PublicKey;
        // Anahtar cifti UYGUN bir imzalama yoludur: cift uretildiginde "Anahtar uret"
        // dugmesi de acilmalidir. Yenilenmedigi icin cift uretmis satici, HMAC sirri
        // da girmedigi surece dugmeyi gri goruyordu.
        GenerateButton.IsEnabled = has || !string.IsNullOrWhiteSpace(secret);
        RefreshBuildHint();
        RefreshFileButton();
    }

    /// <summary>
    /// Kurulum uretiminin bu bilgisayarda mumkun olup olmadigini soyler. Arac
    /// yayinlanmis klasorden calisiyorsa kaynak agaci yoktur; kullanici dugmeye
    /// basip bekledikten sonra ogrenmemelidir.
    /// </summary>
    private void RefreshBuildHint()
    {
        if (keyPair is null) return;

        var repository = InstallerBuilder.FindRepositoryRoot(AppContext.BaseDirectory);
        if (repository is null)
        {
            BuildInstallerButton.IsEnabled = false;
            BuildHint.Text = "Kurulum üretimi için aracı proje klasöründen çalıştırın.";
            return;
        }

        BuildInstallerButton.IsEnabled = true;
        BuildHint.Text = string.Empty;
        if (string.IsNullOrWhiteSpace(VersionBox.Text)) VersionBox.Text = NextVersion(repository);
    }

    /// <summary>
    /// Onerilen sonraki surum: uretilmis en yuksek surumun yama hanesi bir artirilir.
    /// Kullanici surum numarasi uydurmak zorunda kalmaz ve ayni surumu ikinci kez
    /// uretip oncekini silmesi de onlenir.
    /// </summary>
    private static string NextVersion(string repository)
    {
        var directory = Path.Combine(repository, "artifacts", "installer");
        var highest = new Version(1, 0, 0);
        var found = false;

        if (Directory.Exists(directory))
        {
            // Ad sabiti InstallerBuilder'dan gelir: burada tekrar yazilsaydi ve
            // kurulum dosyasi yeniden adlandirilsaydi hicbir dosya bulunamaz,
            // surum onerisi sessizce 1.0.0'da kalir ve yayinlanmis surumun
            // uzerine yazilmasi onerilirdi.
            var prefix = InstallerBuilder.OutputNameStem + "-";
            foreach (var file in Directory.EnumerateFiles(directory, prefix + "*.exe"))
            {
                var text = Path.GetFileNameWithoutExtension(file)[prefix.Length..];
                if (Version.TryParse(text, out var parsed) && parsed > highest)
                {
                    highest = parsed;
                    found = true;
                }
            }
        }

        return found
            ? $"{highest.Major}.{highest.Minor}.{highest.Build + 1}"
            : "1.0.0";
    }

    /// <summary>
    /// Dosya uretimi ozel anahtar VEYA eski HMAC sirri ile yapilabilir; ikisi de
    /// yoksa dugme kapali kalir.
    /// </summary>
    private void RefreshFileButton() =>
        MakeFileButton.IsEnabled = (keyPair is not null || !string.IsNullOrWhiteSpace(secret))
            && MachineCode.MachineIdOf(MachineCodeBox.Text) is not null;

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
        // Anahtar cifti varsa OZEL anahtarla, yoksa eski HMAC sirriyla imzalanir.
        var signingKey = keyPair?.PrivateKey ?? secret;
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            Warn("Önce anahtar çifti üretin.");
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
                var key = OfflineLicenseKey.Create(now, signingKey);
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
        RefreshFileButton();
    }

    /// <summary>
    /// Makine koduna kilitli lisans dosyasi uretir ve Masaustu'ne kaydeder.
    ///
    /// Kaydetme penceresi ACILMAZ: dosyanin nereye gittigi hep aynidir ve
    /// kullanicidan klasor secmesini istemek, gonderilecek dosyayi sonradan
    /// aramak zorunda birakiyordu.
    /// </summary>
    private void MakeFileClick(object sender, RoutedEventArgs e)
    {
        var hashes = MachineCode.Parse(MachineCodeBox.Text);
        if (hashes is null)
        {
            Warn("Makine kodu geçersiz. Okuldan kodun TAMAMINI yeniden göndermesini isteyin.");
            return;
        }

        var customer = CustomerBox.Text.Trim();
        if (customer.Length == 0)
        {
            Warn("Okul / müşteri adını yazın.");
            CustomerBox.Focus();
            return;
        }

        // ON KOSUL LicenseFileIssuer'da: anahtar cifti VEYA HMAC sirri yeter.
        // Burada yalnizca sir sorulmasi, asimetrik modu tamamen calismaz kiliyordu.
        var issued = LicenseFileIssuer.Issue(hashes, customer, keyPair, secret, DateTimeOffset.Now);
        if (issued is null)
        {
            Warn("Önce anahtar çifti üretin.");
            return;
        }

        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            issued.SuggestedFileName);

        try
        {
            File.WriteAllText(path, issued.Content);
            SalesLog.Append(new SaleRecord(issued.Key, customer,
                string.IsNullOrWhiteSpace(NoteBox.Text) ? $"dosya · {issued.MachineId}"
                    : $"{NoteBox.Text.Trim()} · dosya · {issued.MachineId}",
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
        Say($"Masaüstüne kaydedildi: {issued.SuggestedFileName} — okula bu dosyayı gönderin.");
        Reveal(path);
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
        // Anahtar uretimi: cift varsa ozel anahtarla, yoksa eski HMAC sirriyla.
        GenerateButton.IsEnabled = hasSecret || keyPair is not null;
        // Dosya dugmesi HEM sir HEM gecerli makine kodu ister; sir kaydedildiginde
        // kod kutusu doluysa acilmalidir, aksi halde kullanici kodu silip yeniden
        // yapistirmak zorunda kalirdi.
        RefreshFileButton();

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
