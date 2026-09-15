using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Yemekhane.Desktop;

/// <summary>
/// Onay kutulu seçim tablolarında yalnız küçük kutuyu hedefleme zorunluluğunu kaldırır:
/// satırın herhangi bir yeri tıklanınca ya da Space'e basılınca satırın
/// <c>IsSelected</c> özelliği değiştirilir.
/// </summary>
public static class SelectionRowToggle
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SelectionRowToggle),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    /// <summary>Davranışın test edilebilir, veri türünden bağımsız seçim çekirdeği.</summary>
    public static bool TryToggle(object? item)
    {
        if (item is null) return false;
        var property = TypeDescriptor.GetProperties(item)["IsSelected"];
        if (property is null || property.IsReadOnly || property.PropertyType != typeof(bool)) return false;
        property.SetValue(item, !(bool)(property.GetValue(item) ?? false));
        return true;
    }

    private static void OnIsEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not DataGrid grid) return;
        if ((bool)args.NewValue)
        {
            grid.PreviewMouseLeftButtonDown += ToggleFromMouse;
            grid.PreviewKeyDown += ToggleFromKeyboard;
        }
        else
        {
            grid.PreviewMouseLeftButtonDown -= ToggleFromMouse;
            grid.PreviewKeyDown -= ToggleFromKeyboard;
        }
    }

    private static void ToggleFromMouse(object sender, MouseButtonEventArgs args)
    {
        if (sender is not DataGrid grid || FindAncestor<DataGridRow>(args.OriginalSource as DependencyObject) is not { } row)
            return;
        if (!TryToggle(row.DataContext)) return;
        grid.CurrentItem = row.DataContext;
        row.Focus();
        args.Handled = true;
    }

    private static void ToggleFromKeyboard(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Space || sender is not DataGrid grid || grid.CurrentItem is null) return;
        if (TryToggle(grid.CurrentItem)) args.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
