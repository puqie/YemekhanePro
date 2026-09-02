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
                // Gorev 3: view'in kendi RowHeight="30" gecersiz kilmasi silindi;
                // artik DesignSystem.xaml'in DataGrid stili (34) gecerli.
                Assert.Equal(34, grid.RowHeight);
                // Sutun sayisi 12'den 7'ye indirildi: 12 sutunun sabit genislikleri toplami
                // 911px idi ama liste alani 1440x900'de ~745px, bu yuzden HER sutunun icerigi
                // kesiliyordu ("5001" -> "500'", "Aktif" -> "Ak"). Ayrintili sutunlar
                // (BÖLÜM, VELİ TEL, BUGÜNKÜ HAK, BUGÜN GİRİŞ, SON GİRİŞ) sagdaki form
                // panelinde ve detay sekmelerinde zaten gorunuyor.
                Assert.Equal(7, grid.Columns.Count);
                Assert.IsType<TextBox>(view.FindName("StudentSearchBox"));
                Assert.IsType<Border>(view.FindName("CardWorkflowHost"));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
