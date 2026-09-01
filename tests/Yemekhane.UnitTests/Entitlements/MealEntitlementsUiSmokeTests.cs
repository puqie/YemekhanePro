using System.Runtime.ExceptionServices;
using System.Windows.Controls;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Entitlements;

[Collection(Yemekhane.UnitTests.Desktop.UiCollection.Name)]
public sealed class MealEntitlementsUiSmokeTests
{
    [Fact]
    public void XamlLoadsWithDenseVirtualizedMultiSelectGrid()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new MealEntitlementsView(); Yemekhane.UnitTests.Desktop.UiThread.ApplyResources(view);
                var grid = Assert.IsType<DataGrid>(view.FindName("EntitlementsGrid"));
                Assert.True(grid.EnableRowVirtualization); Assert.True(grid.EnableColumnVirtualization);
                Assert.Equal(DataGridSelectionMode.Extended, grid.SelectionMode);
                // Gorev 3: view'in kendi RowHeight="29" gecersiz kilmasi silindi;
                // artik DesignSystem.xaml'in DataGrid stili (34) gecerli.
                Assert.Equal(34, grid.RowHeight); Assert.Equal(11, grid.Columns.Count);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
