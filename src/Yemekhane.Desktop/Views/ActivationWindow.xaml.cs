using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Yemekhane.Licensing;

namespace Yemekhane.Desktop.Views;

/// <summary>
/// Lisans etkinlestirme ekrani.
///
/// Giris ekraniyla ayni deseni izler: ShutdownMode=OnExplicitShutdown oldugu icin bu
/// diyalog kapandiginda uygulama kendiliginden bitmez; karari <see cref="App"/> verir.
/// </summary>
public partial class ActivationWindow : Window, INotifyPropertyChanged
{
    private readonly ILicenseService licenseService;
    private bool isBusy;
    private string? errorMessage;
    private string licenseKey = string.Empty;

    public ActivationWindow(ILicenseService licenseService, LicenseCheck check, string machineId)
    {
        ArgumentNullException.ThrowIfNull(licenseService);
        ArgumentNullException.ThrowIfNull(check);

        this.licenseService = licenseService;
        InitializeComponent();
        DataContext = this;
        StatusMessage = check.Message;
        MachineId = machineId;
        Loaded += (_, _) => LicenseKeyBox.Focus();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && !IsBusy) { DialogResult = false; args.Handled = true; }
        };
    }

    /// <summary>Uygulamanin neden kilitli oldugunu soyleyen Turkce aciklama.</summary>
    public string StatusMessage { get; }

    /// <summary>Destege bildirilecek kisa makine kimligi.</summary>
    public string MachineId { get; }

    public static string VersionText => $"Sürüm {AppVersion.Display}";

    public string LicenseKey
    {
        get => licenseKey;
        set { licenseKey = value; Changed(); }
    }

    public bool IsBusy
    {
        get => isBusy;
        private set { isBusy = value; Changed(); Changed(nameof(IsEditable)); }
    }

    /// <summary>
    /// Alanlarin duzenlenebilirligi. Olumlu tutulur: XAML'de tersine cevirici yoktur ve
    /// <c>InverseBooleanConverter</c> icin statik bir <c>Instance</c> alani bulunmaz.
    /// </summary>
    public bool IsEditable => !IsBusy;

    public string? ErrorMessage
    {
        get => errorMessage;
        private set { errorMessage = value; Changed(); Changed(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void ActivateClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(LicenseKey))
        {
            ErrorMessage = "Lütfen lisans anahtarınızı girin.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await licenseService.ActivateAsync(LicenseKey);
            if (result.IsValid) { DialogResult = true; return; }

            // Sunucunun somut sebebi gosterilir: kullanici anahtarini mi yanlis yazdigini,
            // yoksa lisansin baska bir bilgisayarda mi oldugunu bilmelidir.
            ErrorMessage = result.Message;
        }
        // Son savunma: bu bir "async void" isleyicidir. Buradan sizan bir exception WPF
        // tarafindan yakalanamaz ve uygulama HICBIR MESAJ GOSTERMEDEN kapanir.
        catch (Exception exception)
        {
            ErrorMessage = $"Etkinleştirme sırasında beklenmeyen bir hata oluştu: {exception.Message}";
        }
        finally { IsBusy = false; }
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
