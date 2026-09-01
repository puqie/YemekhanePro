using System.Runtime.ExceptionServices;
using Yemekhane.Desktop;

namespace Yemekhane.UnitTests.Dashboard;

public sealed class DashboardUiSmokeTests
{
    [Fact]
    public void MainWindowXamlLoadsOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow(); Yemekhane.UnitTests.Desktop.UiThread.ApplyResources(window);
                Assert.Equal(1280, window.MinWidth);
                Assert.Equal(720, window.MinHeight);
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
