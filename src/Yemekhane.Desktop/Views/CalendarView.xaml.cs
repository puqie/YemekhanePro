using System.Windows.Controls;
using System.Windows.Input;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.Desktop.Views;

public partial class CalendarView : UserControl
{
    public CalendarView() => InitializeComponent();

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CalendarViewModel viewModel) return;
        var movement = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -7, Key.Down => 7, _ => 0 };
        if (movement != 0) { e.Handled = true; await viewModel.MoveSelectionAsync(movement); }
        else if (e.Key == Key.Enter) { e.Handled = true; await viewModel.SelectDayAsync(viewModel.SelectedDate); }
        else if (e.Key == Key.Escape) { e.Handled = true; viewModel.CloseDrawer(); }
    }
}
