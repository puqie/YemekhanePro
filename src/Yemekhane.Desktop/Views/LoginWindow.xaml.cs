using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.Views;

public partial class LoginWindow : Window, INotifyPropertyChanged
{
    /// <summary>API'nin kilit esigi (Authentication:Lockout:MaxFailedAttempts varsayilani).</summary>
    public const int LockoutThreshold = 5;
    public const string LockoutHint = "Üst üste 5 hatalı deneme yapıldı; hesap 15 dakika süreyle kilitlenmiş olabilir. " +
        "Bu süre içinde doğru parola bile kabul edilmez. Lütfen 15 dakika bekleyip yeniden deneyin.";
    public const string ReloginMessage = "Oturum süresi doldu. Devam etmek için parolanızı yeniden girin; açık ekranlar ve formlar korunur.";

    private readonly AuthenticationClient client;
    private bool isBusy;
    private string? errorMessage;
    private string username = string.Empty;
    private int consecutiveFailures;

    public LoginWindow(AuthenticationClient client, InitialAdminCredentials? initialAdmin = null,
        bool hasExistingDatabase = false, string? reloginUsername = null)
    {
        this.client = client;
        InitializeComponent();
        DataContext = this;
        SetupMessage = reloginUsername is not null ? ReloginMessage : BuildSetupMessage(initialAdmin, hasExistingDatabase);
        if (reloginUsername is not null)
        {
            // Yeniden giris: kullanici adi bellidir, imlec dogrudan parolaya gider.
            Username = reloginUsername;
            Loaded += (_, _) => PasswordBox.Focus();
        }
        else if (initialAdmin is not null)
        {
            Username = initialAdmin.Username;
            PasswordBox.Password = initialAdmin.Password;
        }
        else if (hasExistingDatabase)
        {
            Username = "admin";
        }
        Loaded += (_, _) => UsernameBox.Focus();
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape && !IsBusy) { DialogResult = false; args.Handled = true; } };
    }

    public string Username { get => username; set { username = value; Changed(); } }
    public bool IsBusy { get => isBusy; private set { isBusy = value; Changed(); } }
    public string? ErrorMessage { get => errorMessage; private set { errorMessage = value; Changed(); Changed(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string? SetupMessage { get; }

    /// <summary>
    /// Giris ekraninda gosterilecek yonlendirme metni. Parolanin neden dolu geldigini ya da
    /// neden gelmedigini soyler; sessiz davranis kullaniciyi ne yapacagini bilmez halde birakir.
    /// </summary>
    public static string? BuildSetupMessage(InitialAdminCredentials? initialAdmin, bool hasExistingDatabase) =>
        initialAdmin is not null
            // "Parolanizi degistirin" DENMEZ: parola degistirme ekrani henuz yok, olmayan
            // bir seye yonlendirmek kullaniciyi aramaya gonderir. Onun yerine parolanin
            // nasil goruntulenecegi ve kaybedilirse ne yapilacagi soylenir.
            ? "İlk yönetici için tek kullanımlık güvenli parola oluşturuldu ve otomatik dolduruldu. "
              + "Göz simgesine basıp parolayı görebilir ve not alabilirsiniz; bu parola bir daha gösterilmez. "
              + "Kaybederseniz giriş ekranındaki \"Parolamı unuttum\" ile lisans dosyanızı kullanarak sıfırlayabilirsiniz."
            : hasExistingDatabase
                ? "Bu bilgisayarda mevcut bir YemekhanePro veritabanı bulundu; kurulum parolası yalnızca ilk " +
                  "kurulumda oluşturulur. Daha önce belirlediğiniz parolayla giriş yapın. Parolayı " +
                  "bilmiyorsanız yöneticinize başvurun."
                : null;
    public static string VersionText => $"Sürüm {AppVersion.Display}";
    public bool HasSetupMessage => !string.IsNullOrWhiteSpace(SetupMessage);
    public event PropertyChangedEventHandler? PropertyChanged;

    private async void LoginClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(Username) || CurrentPassword.Length == 0)
        {
            ErrorMessage = "Kullanıcı adı ve parola zorunludur.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await client.LoginAsync(Username.Trim(), CurrentPassword);
            ClearPassword();
            client.Session.Set(result.AccessToken, result.ExpiresAt);
            DialogResult = true;
        }
        catch (AuthenticationException exception)
        {
            // API guvenlik geregi kilitli hesaba da "parola gecersiz" der (bkz.
            // AuthenticationTests.RepeatedFailuresTemporarilyLockAccount). Kullanici
            // dogru parolayi yazip yine reddedilince nedenini bilemiyordu; esik asilinca
            // olasi kilidi burada acikca soyleriz.
            consecutiveFailures++;
            ErrorMessage = consecutiveFailures >= LockoutThreshold ? exception.Message + " " + LockoutHint : exception.Message;
            ClearPassword(); PasswordBox.Focus();
        }
        // Son savunma: bu bir "async void" isleyicidir. Buradan disari sizan herhangi bir
        // exception WPF tarafindan yakalanamaz ve uygulama HICBIR MESAJ GOSTERMEDEN kapanir --
        // kullanici "giris tusuna bastim, program kayboldu" der. Beklenmeyen hata da olsa
        // ekranda kalip nedeni soylemek, sessizce kapanmaktan her zaman iyidir.
        catch (Exception exception)
        {
            ErrorMessage = $"Giriş sırasında beklenmeyen bir hata oluştu: {exception.Message}";
            ClearPassword();
            PasswordBox.Focus();
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Lisans dosyasiyla parola sifirlama ekranini acar.
    ///
    /// Sifirlama basarili olursa kullanici adi giris formuna tasinir: okul yeni
    /// parolayla hemen giris yapabilsin, hangi hesabi sifirladigini tekrar yazmasin.
    /// </summary>
    private void ForgotPasswordClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;

        var reset = new PasswordResetWindow(client, Username) { Owner = this };
        if (reset.ShowDialog() != true) return;

        Username = reset.Username.Trim();
        ErrorMessage = null;
        ClearPassword();
        PasswordBox.Focus();
    }

    /// <summary>
    /// Iki kutu arasindaki degeri kopyalarken tetiklenen olaylarin birbirini
    /// yeniden tetiklemesini onler; aksi halde her tusta sonsuz dongu olusur.
    /// </summary>
    private bool syncingPassword;

    /// <summary>Parola su anda ACIK METIN olarak mi gosteriliyor.</summary>
    public bool IsPasswordVisible { get; private set; }

    /// <summary>
    /// Girilen parola. Hangi kutunun gorunur oldugundan bagimsiz olarak dogru degeri
    /// verir; cagiranlarin hangi kutuya bakacagini bilmesi gerekmez.
    /// </summary>
    private string CurrentPassword =>
        IsPasswordVisible ? PasswordTextBox.Text : PasswordBox.Password;

    /// <summary>
    /// Parolayi acik metin ile maskeli gorunum arasinda takas eder.
    ///
    /// Ilk kurulumda uretilen tek kullanimlik parola PasswordBox'a dolduruluyor ama
    /// o kutu icerigini ASLA gostermez: kullanici "not alin" uyarisini goruyor,
    /// notu alacak metni goremiyordu.
    /// </summary>
    private void TogglePasswordClick(object sender, RoutedEventArgs e)
    {
        syncingPassword = true;
        try
        {
            if (IsPasswordVisible)
            {
                PasswordBox.Password = PasswordTextBox.Text;
                PasswordTextBox.Visibility = Visibility.Collapsed;
                PasswordBox.Visibility = Visibility.Visible;
                IsPasswordVisible = false;
                PasswordBox.Focus();
            }
            else
            {
                PasswordTextBox.Text = PasswordBox.Password;
                PasswordBox.Visibility = Visibility.Collapsed;
                PasswordTextBox.Visibility = Visibility.Visible;
                IsPasswordVisible = true;
                PasswordTextBox.Focus();
                PasswordTextBox.CaretIndex = PasswordTextBox.Text.Length;
            }
        }
        finally { syncingPassword = false; }
        TogglePasswordButton.ToolTip = IsPasswordVisible ? "Parolayı gizle" : "Parolayı göster";
    }

    // Iki kutu tek bir degeri paylasir: kullanici hangisine yazarsa yazsin, otekinin
    // icerigi de guncel kalir. Takas sirasinda kopyalama zaten yapildigi icin
    // syncingPassword ile bastirilir.
    private void PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (syncingPassword || IsPasswordVisible) return;
        syncingPassword = true;
        try { PasswordTextBox.Text = PasswordBox.Password; }
        finally { syncingPassword = false; }
    }

    /// <summary>
    /// Her iki kutuyu da bosaltir. Yalnizca gorunen kutu temizlenseydi, parola
    /// otekinde KALIRDI: basarisiz giristen sonra goz dugmesine basan kullanici
    /// reddedilmis parolayi ekranda gorurdu.
    /// </summary>
    private void ClearPassword()
    {
        syncingPassword = true;
        try
        {
            PasswordBox.Clear();
            PasswordTextBox.Clear();
        }
        finally { syncingPassword = false; }
    }

    private void PasswordTextChanged(object sender, TextChangedEventArgs e)
    {
        if (syncingPassword || !IsPasswordVisible) return;
        syncingPassword = true;
        try { PasswordBox.Password = PasswordTextBox.Text; }
        finally { syncingPassword = false; }
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
