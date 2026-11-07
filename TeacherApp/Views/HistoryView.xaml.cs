using System.Windows.Controls;
using System.Windows.Input;
using VeyonSync.Shared;

namespace VeyonSync.TeacherApp.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    TeacherVm Vm => (TeacherVm)DataContext;

    void OpenDetails(object sender, MouseButtonEventArgs e)
    {
        if (HistGrid.SelectedItem is HistoryRow h) Vm.HistoryDetailsCmd.Execute(h);
    }

    void GridKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && HistGrid.SelectedItem is HistoryRow h) { Vm.HistoryDetailsCmd.Execute(h); e.Handled = true; }
    }
}
