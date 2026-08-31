using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.Views;

public partial class LoginWindow : Window, INotifyPropertyChanged
{
    private readonly AuthenticationClient client;
    private bool isBusy;
    private string? errorMessage;
    private string username = string.Empty;

    public LoginWindow(AuthenticationClient client, InitialAdminCredentials? initialAdmin = null,
        bool hasExistingDatabase = false)
    {
        this.client = client;
        InitializeComponent();
        DataContext = this;
        SetupMessage = BuildSetupMessage(initialAdmin, hasExistingDatabase);
        if (initialAdmin is not null)
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
            ? "İlk yönetici için tek kullanımlık güvenli parola oluşturuldu ve otomatik dolduruldu. Girişten sonra parolanızı değiştirin."
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
        if (string.IsNullOrWhiteSpace(Username) || PasswordBox.SecurePassword.Length == 0)
        {
            ErrorMessage = "Kullanıcı adı ve parola zorunludur.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await client.LoginAsync(Username.Trim(), PasswordBox.Password);
            PasswordBox.Clear();
            client.Session.Set(result.AccessToken, result.ExpiresAt);
            DialogResult = true;
        }
        catch (AuthenticationException exception) { ErrorMessage = exception.Message; PasswordBox.Clear(); PasswordBox.Focus(); }
        // Son savunma: bu bir "async void" isleyicidir. Buradan disari sizan herhangi bir
        // exception WPF tarafindan yakalanamaz ve uygulama HICBIR MESAJ GOSTERMEDEN kapanir --
        // kullanici "giris tusuna bastim, program kayboldu" der. Beklenmeyen hata da olsa
        // ekranda kalip nedeni soylemek, sessizce kapanmaktan her zaman iyidir.
        catch (Exception exception)
        {
            ErrorMessage = $"Giriş sırasında beklenmeyen bir hata oluştu: {exception.Message}";
            PasswordBox.Clear();
            PasswordBox.Focus();
        }
        finally { IsBusy = false; }
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
