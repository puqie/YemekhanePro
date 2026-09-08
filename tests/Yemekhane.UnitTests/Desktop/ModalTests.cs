using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Yemekhane.Desktop.Controls;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ortalanmis diyalog kontrolu. Once yedi ekran ayni seyi elle yaziyordu; bu kontrol tek
/// davranisi tasir: Kapat/karartma kapatir ve CloseCommand'i calistirir, CanClose=false
/// kapanmayi engeller (oturum suresi doldu), alt satir bos ise gizlenir.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class ModalTests
{
    [Fact]
    public void OpeningShowsAndClosingHidesTheModal()
    {
        UiThread.Run(() =>
        {
            var modal = new Modal();
            ApplyModalTheme(modal);
            Assert.Equal(Visibility.Collapsed, modal.Visibility);

            modal.IsOpen = true;
            Assert.Equal(Visibility.Visible, modal.Visibility);

            modal.Close();
            Assert.False(modal.IsOpen);
            Assert.Equal(Visibility.Collapsed, modal.Visibility);
        });
    }

    [Fact]
    public void CloseButtonClosesAndRunsTheCloseCommandOnce()
    {
        UiThread.Run(() =>
        {
            var modal = new Modal { IsOpen = true };
            ApplyModalTheme(modal);
            var executions = 0;
            modal.CloseCommand = new CountingCommand(() => executions++);
            modal.ApplyTemplate();
            modal.OnApplyTemplate();   // template yeniden uygulansa da abonelik birikmemeli

            var close = (ButtonBase)modal.Template.FindName("PART_Close", modal);
            close.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.False(modal.IsOpen);
            Assert.Equal(1, executions);
        });
    }

    /// <summary>Tek yonlu IsOpen baglamasi Kapat ile KOPMAMALI: SetCurrentValue kullanilir.</summary>
    [Fact]
    public void ClosingKeepsAOneWayBindingAlive()
    {
        UiThread.Run(() =>
        {
            var source = new OpenState { IsOpen = true };
            var modal = new Modal { DataContext = source };
            ApplyModalTheme(modal);
            modal.SetBinding(Modal.IsOpenProperty, new System.Windows.Data.Binding(nameof(OpenState.IsOpen)) { Mode = System.Windows.Data.BindingMode.OneWay });
            Assert.True(modal.IsOpen);

            modal.Close();
            Assert.False(modal.IsOpen);

            source.IsOpen = false;
            source.IsOpen = true;
            Assert.True(modal.IsOpen);
        });
    }

    [Fact]
    public void CanCloseFalseIgnoresCloseAndHidesTheButton()
    {
        UiThread.Run(() =>
        {
            var modal = new Modal { IsOpen = true, CanClose = false };
            ApplyModalTheme(modal);
            var executions = 0;
            modal.CloseCommand = new CountingCommand(() => executions++);
            modal.ApplyTemplate();

            modal.Close();

            Assert.True(modal.IsOpen);
            Assert.Equal(0, executions);
            var close = (FrameworkElement)modal.Template.FindName("PART_Close", modal);
            Assert.Equal(Visibility.Collapsed, close.Visibility);
        });
    }

    [Fact]
    public void FooterAndSubtitleAreHiddenWhenEmpty()
    {
        UiThread.Run(() =>
        {
            var modal = new Modal { IsOpen = true, Title = "Başlık" };
            ApplyModalTheme(modal);
            modal.ApplyTemplate();
            var footer = (FrameworkElement)modal.Template.FindName("PART_Footer", modal);
            var subtitle = (FrameworkElement)modal.Template.FindName("PART_Subtitle", modal);
            Assert.Equal(Visibility.Collapsed, footer.Visibility);
            Assert.Equal(Visibility.Collapsed, subtitle.Visibility);

            modal.Footer = new System.Windows.Controls.Button { Content = "Kaydet" };
            modal.Subtitle = "Açıklama";
            Assert.Equal(Visibility.Visible, footer.Visibility);
            Assert.Equal(Visibility.Visible, subtitle.Visibility);
        });
    }

    private static void ApplyModalTheme(FrameworkElement element)
    {
        UiThread.ApplyResources(element);
        element.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Yemekhane.Desktop;component/Themes/Modal.xaml")
        });
    }

    private sealed class OpenState : System.ComponentModel.INotifyPropertyChanged
    {
        private bool isOpen;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public bool IsOpen
        {
            get => isOpen;
            set { isOpen = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsOpen))); }
        }
    }

    private sealed class CountingCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
