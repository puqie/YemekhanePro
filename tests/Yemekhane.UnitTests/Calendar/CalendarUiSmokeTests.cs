using System.Runtime.ExceptionServices;
using Yemekhane.Desktop;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Calendar;

[Collection(Yemekhane.UnitTests.Desktop.UiCollection.Name)]
public sealed class CalendarUiSmokeTests
{
    [Fact]
    public void CalendarXamlLoadsOnStaThread()
    {
        Exception? failure = null; var thread = new Thread(() =>
        {
            try { var view = new CalendarView(); Yemekhane.UnitTests.Desktop.UiThread.ApplyResources(view); Assert.True(view.Focusable); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
