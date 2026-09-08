using System.Windows;

namespace Yemekhane.Desktop.Controls;

/// <summary>
/// DataGrid sutunlari gorsel agacta degildir; <c>Visibility</c> gibi ozellikleri DataContext'e
/// dogrudan baglanamaz. Bu Freezable, kaynak sozlugune konup <c>Data="{Binding}"</c> ile
/// baglami tasir; sutun <c>{Binding Data.X, Source={StaticResource Proxy}}</c> ile okur.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy), new PropertyMetadata(null));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
