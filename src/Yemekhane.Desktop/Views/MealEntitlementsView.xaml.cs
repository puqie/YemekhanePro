using System.Windows.Controls;
using Yemekhane.Application.Entitlements;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.Desktop.Views;

public partial class MealEntitlementsView : UserControl
{
    public MealEntitlementsView() => InitializeComponent();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MealEntitlementsViewModel viewModel && sender is DataGrid grid)
            viewModel.SetSelection(grid.SelectedItems.Cast<MealEntitlementListItem>());
    }
}
