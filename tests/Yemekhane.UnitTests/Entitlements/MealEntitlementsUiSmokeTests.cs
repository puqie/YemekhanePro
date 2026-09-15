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
                // Coklu secim artik SEC sutunundaki onay kutusuyla yapilir. Tablonun kendi
                // satir secimi Single/Cell: DataGridRow.IsSelected'e bagli onay kutusu ayni
                // tiklamayla hem satiri seciyor hem tiki degistiriyor ve tik geri kapaniyordu.
                Assert.Equal(DataGridSelectionMode.Single, grid.SelectionMode);
                Assert.Equal(DataGridSelectionUnit.Cell, grid.SelectionUnit);
                // Onay kutusunun duzenlenebilmesi icin tablo salt okunur OLMAMALI.
                Assert.False(grid.IsReadOnly);
                // Sade görünüm: seçim + 9 karar sütunu. Kart ve teknik kaynak bilgisi
                // öğrenci/denetim ekranlarında kaldığı için ana listede gösterilmez.
                Assert.Equal(34, grid.RowHeight); Assert.Equal(10, grid.Columns.Count);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
