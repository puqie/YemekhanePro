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
                Header = item.Header, Binding = new Binding(item.Key),
                // 0 = yildiz: kalan alani doldurur, boylece tablo pencereye sigar ve yatay kaydirma cikmaz.
                Width = item.Width > 0 ? new DataGridLength(item.Width) : new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = item.Width > 0 ? 20 : 110,
                CanUserSort = item.SortKey is not null, SortMemberPath = item.SortKey ?? ""
            };
            column.SetValue(FrameworkElement.TagProperty, item.Key);
            // Uzun metin (neden, aciklama) sutuna sigmayabilir; fareyle ustune gelince tamami okunur.
            var cell = new Style(typeof(TextBlock));
            cell.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(item.Key)));
            if (item.Key is "MealCount" or "Amount")
            {
                // Sayisal sutunlar saga yaslanir: tutarlar alt alta hizalanmazsa 1.250,00 ile
                // 250,00 goz ile karsilastirilamaz. Baslik da ayni hizada durur; tasarim sisteminin
                // baslik stili (ReportGrid.ColumnHeaderStyle) temel alinir ki gorunum bozulmasin.
                cell.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
                var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader), ReportGrid.ColumnHeaderStyle);
                header.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Right));
                column.HeaderStyle = header;
            }
            column.ElementStyle = cell;
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
            // Yildiz sutun piksel olarak kaydedilirse bir sonraki acilista sabitlenir ve tablo yine tasar.
            Width = column.Width.IsStar ? ReportsViewModel.Star : column.ActualWidth
        }).Where(x => x.Key is not null).ToDictionary(x => x.Key!, StringComparer.Ordinal);
        var layouts = viewModel.Columns.Select(column => displayed.TryGetValue(column.Key, out var value)
            ? new ReportColumnLayout(column.Key, value.Index, value.Width, column.IsVisible)
            : new ReportColumnLayout(column.Key, column.DisplayIndex, column.Width, column.IsVisible)).ToArray();
        viewModel.SaveLayout(layouts);
    }
}
