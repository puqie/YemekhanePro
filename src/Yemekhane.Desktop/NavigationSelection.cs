using System.Windows;

namespace Yemekhane.Desktop;

/// <summary>
/// Kenar cubugu menu ogesinin secili olup olmadigini tasir.
///
/// Tag ozelligi zaten rota kimligini tasiyor (orn. "dashboard", "students");
/// secim durumunu da Tag uzerinden isaretlemek rota eslestirmesini kirar.
/// Bu yuzden secim ayri bir eklenti (attached) ozellik olarak tutulur ve
/// NavItem stilindeki tetikleyici (DesignSystem.xaml) buna gore calisir.
/// </summary>
public static class NavigationSelection
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.RegisterAttached(
        "IsSelected", typeof(bool), typeof(NavigationSelection), new PropertyMetadata(false));

    public static bool GetIsSelected(DependencyObject element) => (bool)element.GetValue(IsSelectedProperty);

    public static void SetIsSelected(DependencyObject element, bool value) => element.SetValue(IsSelectedProperty, value);
}
