using System.Windows.Controls;

namespace Yemekhane.Desktop.Views;

public partial class MealEntitlementsView : UserControl
{
    // Secim artik satirin kendi IsSelected ozelliginde; tablonun SelectionChanged'ini
    // dinlemeye gerek yok (once oradan geliyordu ve tik ile catisiyordu).
    public MealEntitlementsView() => InitializeComponent();
}
