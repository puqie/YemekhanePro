using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.Desktop.Views;

public partial class ReportsView : UserControl
{
    private ReportsViewModel? subscribed;

    public ReportsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => SaveLayout();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (subscribed is not null) subscribed.PropertyChanged -= ViewModelPropertyChanged;
        subscribed = e.NewValue as ReportsViewModel;
        if (subscribed is not null) subscribed.PropertyChanged += ViewModelPropertyChanged;
        RebuildColumns();
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReportsViewModel.SelectedReport)) RebuildColumns();
    }

    private void RebuildColumns()
    {
        ReportGrid.Columns.Clear();
        if (DataContext is not ReportsViewModel viewModel) return;
        foreach (var item in viewModel.Columns.Where(x => x.IsVisible).OrderBy(x => x.DisplayIndex))
        {
            var column = new DataGridTextColumn
            {
                Header = item.Header, Binding = new Binding(item.Key), Width = item.Width,
                CanUserSort = item.SortKey is not null, SortMemberPath = item.SortKey ?? ""
            };
            column.SetValue(FrameworkElement.TagProperty, item.Key);
            ReportGrid.Columns.Add(column);
        }
    }

    private async void ReportGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (DataContext is ReportsViewModel viewModel && !string.IsNullOrWhiteSpace(e.Column.SortMemberPath))
            await viewModel.SortAsync(e.Column.SortMemberPath);
    }

    private void ReportGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ReportsViewModel viewModel)
            viewModel.ReplaceSelection(ReportGrid.SelectedItems.Cast<ReportGridRow>());
    }

    private void ReportGrid_ColumnReordered(object sender, DataGridColumnEventArgs e) => SaveLayout();

    private void ColumnVisibility_Click(object sender, RoutedEventArgs e)
    {
        SaveLayout();
        RebuildColumns();
    }

    private void SaveLayout()
    {
        if (DataContext is not ReportsViewModel viewModel) return;
        var displayed = ReportGrid.Columns.Select((column, index) => new
        {
            Key = (string?)column.GetValue(FrameworkElement.TagProperty), Index = column.DisplayIndex,
            Width = column.ActualWidth
        }).Where(x => x.Key is not null).ToDictionary(x => x.Key!, StringComparer.Ordinal);
        var layouts = viewModel.Columns.Select(column => displayed.TryGetValue(column.Key, out var value)
            ? new ReportColumnLayout(column.Key, value.Index, value.Width, column.IsVisible)
            : new ReportColumnLayout(column.Key, column.DisplayIndex, column.Width, column.IsVisible)).ToArray();
        viewModel.SaveLayout(layouts);
    }
}
