using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using Yemekhane.Desktop;

namespace Yemekhane.UnitTests.Search;

[Collection(Yemekhane.UnitTests.Desktop.UiCollection.Name)]
public sealed class GlobalSearchUiSmokeTests
{
    [Fact]
    public void MainWindowLoadsSearchOverlayWithKeyboardTargets()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow(); Yemekhane.UnitTests.Desktop.UiThread.ApplyResources(window);
                Assert.IsType<Grid>(window.FindName("GlobalSearchHost"));
                Assert.IsType<TextBox>(window.FindName("GlobalSearchBox"));
                Assert.IsType<ListBox>(window.FindName("SearchResults"));
                Assert.IsType<Grid>(window.FindName("ShortcutHelpHost"));
                Assert.IsType<ItemsControl>(window.FindName("ShortcutHelpList"));
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
