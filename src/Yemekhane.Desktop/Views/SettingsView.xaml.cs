using System.Windows;
using System.Windows.Controls;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    private void SmsSecretChanged(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel vm && sender is PasswordBox box) vm.SetSmsSecret(box.Password); }
    private void SyncSecretChanged(object sender, RoutedEventArgs e)
    { if (DataContext is SettingsViewModel vm && sender is PasswordBox box) vm.SetSyncSecret(box.Password); }
}
