using System.Runtime.ExceptionServices;
using Yemekhane.Desktop;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Calendar;

public sealed class CalendarUiSmokeTests
{
    [Fact]
    public void CalendarXamlLoadsOnStaThread()
    {
        Exception? failure = null; var thread = new Thread(() =>
        {
            try { var view = new CalendarView(); Assert.True(view.Focusable); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
