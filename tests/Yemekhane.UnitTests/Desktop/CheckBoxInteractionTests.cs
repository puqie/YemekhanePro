using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yemekhane.Desktop;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

[Collection("UI")]
public sealed class CheckBoxInteractionTests
{
    [Fact]
    public void OrtakCheckboxBelirginKutuVeGenisTiklamaAlaniKullanir() => UiThread.Run(() =>
    {
        var checkBox = new CheckBox { Content = "Ücreti kasaya gelir olarak işle" };
        var host = UiThread.Host(checkBox, 360, 80);
        host.Measure(new Size(360, 80));
        host.Arrange(new Rect(0, 0, 360, 80));
        host.UpdateLayout();
        checkBox.ApplyTemplate();

        var box = Assert.IsType<Border>(checkBox.Template.FindName("Box", checkBox));
        Assert.True(checkBox.ActualHeight >= 40,
            $"Checkbox tıklama yüksekliği {checkBox.ActualHeight:F1}px; en az 40px olmalı.");
        Assert.Equal(22d, box.ActualWidth, precision: 1);
        Assert.Equal(22d, box.ActualHeight, precision: 1);
    });

    [Fact]
    public void SatirDavranisiIsSelectedDegeriniHerTiklamadaDegistirir()
    {
        var row = new SelectableRow();

        Assert.True(SelectionRowToggle.TryToggle(row));
        Assert.True(row.IsSelected);
        Assert.True(SelectionRowToggle.TryToggle(row));
        Assert.False(row.IsSelected);
    }

    [Fact]
    public void HakEdisVeSmsSecimListelerindeTumSatirDavranisiEtkin() => UiThread.Run(() =>
    {
        var entitlementView = new MealEntitlementsView();
        var entitlementGrid = Assert.IsType<DataGrid>(entitlementView.FindName("EntitlementsGrid"));
        var pickerGrid = Assert.IsType<DataGrid>(entitlementView.FindName("StudentPickerGrid"));
        var smsView = new SmsView();
        var smsGrid = Assert.IsType<DataGrid>(smsView.FindName("SmsStudentGrid"));

        foreach (var grid in new[] { entitlementGrid, pickerGrid, smsGrid })
        {
            Assert.True(SelectionRowToggle.GetIsEnabled(grid));
            Assert.NotNull(grid.RowStyle);
            Assert.Equal(40d, grid.RowStyle.Setters.OfType<Setter>()
                .Single(x => x.Property == FrameworkElement.MinHeightProperty).Value);
        }
    });

    [Fact]
    public void CheckboxVeTumSatirSecimiGercekTemaIleRenderEdilebilir() => UiThread.Run(() =>
    {
        var shotDirectory = Environment.GetEnvironmentVariable("YP_SHOT_DIR");
        if (string.IsNullOrWhiteSpace(shotDirectory)) return;

        var root = new Grid { Width = 640, Height = 340, Margin = new Thickness(18) };
        UiThread.ApplyResources(root);
        root.Background = (Brush)root.FindResource("CanvasBrush");
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var title = new TextBlock
        {
            Text = "Kolay seçim alanları", FontSize = 22, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)root.FindResource("InkBrush"), Margin = new Thickness(10, 8, 10, 10)
        };
        root.Children.Add(title);

        var choices = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 10) };
        choices.Children.Add(new CheckBox { Content = "Ücreti kasaya işle", IsChecked = true, Margin = new Thickness(0, 0, 24, 0) });
        choices.Children.Add(new CheckBox { Content = "Veliye SMS gönder", IsChecked = false });
        Grid.SetRow(choices, 1);
        root.Children.Add(choices);

        var rows = new[]
        {
            new SelectableRow { IsSelected = true, StudentNo = "5012", Name = "Ayşe Yılmaz", ClassName = "5A" },
            new SelectableRow { StudentNo = "5013", Name = "Cem Kaya", ClassName = "5A" },
            new SelectableRow { IsSelected = true, StudentNo = "5014", Name = "Ece Su", ClassName = "5B" }
        };
        var grid = new DataGrid
        {
            ItemsSource = rows, AutoGenerateColumns = false, IsReadOnly = false,
            CanUserAddRows = false, HeadersVisibility = DataGridHeadersVisibility.Column,
            Margin = new Thickness(10, 0, 10, 10),
            RowStyle = (Style)root.FindResource("SelectableDataGridRow")
        };
        SelectionRowToggle.SetIsEnabled(grid, true);
        grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "SEÇ", Width = 64,
            CellTemplate = (DataTemplate)root.FindResource("SelectionIndicatorTemplate")
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "NO", Binding = new Binding(nameof(SelectableRow.StudentNo)), Width = 90, IsReadOnly = true
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "AD SOYAD", Binding = new Binding(nameof(SelectableRow.Name)), Width = 280, IsReadOnly = true
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "SINIF", Binding = new Binding(nameof(SelectableRow.ClassName)), Width = 90, IsReadOnly = true
        });
        Grid.SetRow(grid, 2);
        root.Children.Add(grid);

        var host = new Border { Width = 676, Height = 376, Child = root, Background = root.Background };
        host.Measure(new Size(host.Width, host.Height));
        host.Arrange(new Rect(0, 0, host.Width, host.Height));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)host.Width, (int)host.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(shotDirectory);
        using var stream = File.Create(Path.Combine(shotDirectory, "checkbox-full-row-after.png"));
        encoder.Save(stream);
    });

    private sealed class SelectableRow : INotifyPropertyChanged
    {
        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value) return;
                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public string StudentNo { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string ClassName { get; init; } = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
