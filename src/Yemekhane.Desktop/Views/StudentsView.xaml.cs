using System.Windows.Controls;

namespace Yemekhane.Desktop.Views;

public partial class StudentsView : UserControl
{
    public StudentsView() => InitializeComponent();
    public void FocusSearch()
    {
        StudentSearchBox.Focus();
        StudentSearchBox.SelectAll();
    }
}
