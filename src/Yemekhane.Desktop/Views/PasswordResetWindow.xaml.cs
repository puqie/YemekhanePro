using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.Views;

/// <summary>
/// Lisans dosyasi kaniti ile parola sifirlama ekrani.
///
/// <para>
/// Neden var: parolayi unutan okul programa hic giremiyordu ve tek cikis
/// veritabanini silmekti. Karar mantigi <see cref="PasswordResetForm"/> icindedir;
/// burada yalnizca arayuz baglantisi durur.
/// </para>
/// </summary>
public partial class PasswordResetWindow : Window, INotifyPropertyChanged
{
    private readonly AuthenticationClient client;
    private string? licenseFileContent;
    private string username = string.Empty;
    private string selectedFileName = string.Empty;
    private string passwordHint = string.Empty;
    private string? errorMessage;
    private bool isBusy;

    public PasswordResetWindow(AuthenticationClient client, string? suggestedUsername = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
        InitializeComponent();
        DataContext = this;
        Username = suggestedUsername ?? string.Empty;
        Loaded += (_, _) => ChooseFileButton.Focus();
        PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape && !IsBusy) { DialogResult = false; args.Handled = true; }
        };
        RefreshState();
    }

    public string Username
    {
        get => username;
        set { username = value; Changed(); RefreshState(); }
    }

    public string SelectedFileName
    {
        get => selectedFileName;
        private set { selectedFileName = value; Changed(); Changed(nameof(HasFile)); }
    }

    public bool HasFile => !string.IsNullOrWhiteSpace(SelectedFileName);

    public string PasswordHint
    {
        get => passwordHint;
        private set { passwordHint = value; Changed(); }
    }

    public string? ErrorMessage
    {
        get => errorMessage;
        private set { errorMessage = value; Changed(); Changed(nameof(HasError)); }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsBusy
    {
        get => isBusy;
        private set { isBusy = value; Changed(); RefreshState(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void PasswordChanged(object sender, RoutedEventArgs e) => RefreshState();

    /// <summary>
    /// Dugmenin acik/kapali durumunu ve yonlendirme metnini tazeler.
    ///
    /// Her girdi degisiminde cagrilir: unutulursa dugme kalici olarak gri kalir ve
    /// kullanici formu doldurmus olmasina ragmen devam edemez.
    /// </summary>
    private void RefreshState()
    {
        // InitializeComponent oncesi cagrilirsa denetimler henuz yoktur.
        if (ResetButton is null) return;

        var state = PasswordResetForm.Evaluate(
            licenseFileContent, Username, NewPasswordBox?.Password, ConfirmPasswordBox?.Password);
        ResetButton.IsEnabled = state.CanSubmit && !IsBusy;
        PasswordHint = state.Hint;
    }

    private void ChooseFileClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Lisans dosyasını seçin",
            Filter = "Lisans dosyası (*.lic)|*.lic|Tüm dosyalar (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            licenseFileContent = File.ReadAllText(dialog.FileName);
            SelectedFileName = Path.GetFileName(dialog.FileName);
            ErrorMessage = null;
        }
        // Dosya silinmis, kilitli ya da okunamiyor olabilir; somut sebep gosterilir.
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            licenseFileContent = null;
            SelectedFileName = string.Empty;
            ErrorMessage = "Lisans dosyası okunamadı: " + exception.Message;
        }
        RefreshState();
    }

    private async void ResetClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy) return;
        var state = PasswordResetForm.Evaluate(
            licenseFileContent, Username, NewPasswordBox.Password, ConfirmPasswordBox.Password);
        if (!state.CanSubmit)
        {
            ErrorMessage = string.IsNullOrEmpty(state.Hint) ? "Formu eksiksiz doldurun." : state.Hint;
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var message = await client.ResetPasswordAsync(
                licenseFileContent!, Username.Trim(), NewPasswordBox.Password);
            NewPasswordBox.Clear();
            ConfirmPasswordBox.Clear();
            MessageBox.Show(this, message, "YemekhanePro", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }
        catch (AuthenticationException exception)
        {
            ErrorMessage = exception.Message;
        }
        // Son savunma: bu bir "async void" isleyicidir. Buradan sizan bir exception WPF
        // tarafindan yakalanamaz ve uygulama HICBIR MESAJ GOSTERMEDEN kapanir.
        catch (Exception exception)
        {
            ErrorMessage = $"Sıfırlama sırasında beklenmeyen bir hata oluştu: {exception.Message}";
        }
        finally { IsBusy = false; }
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
