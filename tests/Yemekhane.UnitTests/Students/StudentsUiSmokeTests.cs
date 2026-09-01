using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Students;

[Collection(Yemekhane.UnitTests.Desktop.UiCollection.Name)]
public sealed class StudentsUiSmokeTests
{
    [Fact]
    public void StudentsXamlLoadsWithVirtualizedDenseGrid()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new StudentsView(); Yemekhane.UnitTests.Desktop.UiThread.ApplyResources(view);
                var grid = Assert.IsType<DataGrid>(view.FindName("StudentsGrid"));
                Assert.True(grid.EnableRowVirtualization);
                Assert.True(grid.EnableColumnVirtualization);
                Assert.Equal(30, grid.RowHeight);
                Assert.Equal(12, grid.Columns.Count);
                Assert.IsType<TextBox>(view.FindName("StudentSearchBox"));
                Assert.IsType<Border>(view.FindName("CardWorkflowHost"));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
