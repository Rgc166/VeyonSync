using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VeyonSync.Desktop;
using VeyonSync.Shared;

namespace VeyonSync.TeacherApp.Views;

public partial class MachinesView : UserControl
{
    HashSet<string> selected = new();
    bool restoring;

    public MachinesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { if (DataContext is TeacherVm vm) vm.MachinesRebuilt += Restore; };
        MachineGrid.PreviewMouseLeftButtonDown += GridPreviewDown;
    }

    TeacherVm Vm => (TeacherVm)DataContext;
    List<MachineRow> Selected => MachineGrid.SelectedItems.OfType<MachineRow>().ToList();

    void GridSelection(object sender, SelectionChangedEventArgs e)
    {
        if (restoring) return;
        selected = Selected.Select(m => m.AgentId).ToHashSet();
        UpdateAllCheck();
        // O último computador marcado/clicado abre o painel lateral.
        if (e.AddedItems.Count > 0 && e.AddedItems[e.AddedItems.Count - 1] is MachineRow m) Vm.SelectedMachine = m;
    }

    void UpdateAllCheck()
    {
        var total = Vm.Machines.Count;
        bool? state = null;
        if (total > 0 && selected.Count >= total) state = true;
        else if (selected.Count == 0) state = false;
        AllCheck.IsChecked = state;
    }

    // A lista é recarregada a cada 5 s; a seleção do administrador é preservada.
    void Restore()
    {
        restoring = true;
        try
        {
            MachineGrid.SelectedItems.Clear();
            foreach (var m in Vm.Machines.Where(m => selected.Contains(m.AgentId))) MachineGrid.SelectedItems.Add(m);
            UpdateAllCheck();
        }
        finally { restoring = false; }
    }

    /// <summary>A caixa de cada linha marca/desmarca sem desfazer a seleção das outras linhas.</summary>
    void GridPreviewDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        var box = Find<CheckBox>(src);
        var row = Find<DataGridRow>(src);
        if (box == null || row == null) return;
        row.IsSelected = !row.IsSelected;
        e.Handled = true;
    }

    static T? Find<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T)
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    void ToggleAll(object sender, RoutedEventArgs e)
    {
        if (AllCheck.IsChecked == true) MachineGrid.SelectAll();
        else MachineGrid.UnselectAll();
    }

    async void Collect(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Selected.Count == 0) { Notice.Error("Marque os computadores na lista (ou use Solicitar coleta na Visão geral para todos)."); return; }
            await Vm.RequestCollectAsync(Selected);
        }
        catch (Exception ex) { Notice.Error(ex.Message); }
    }

    async void IntegrateSelected(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Selected.Count == 0) { Notice.Error("Marque os computadores na lista para integrar."); return; }
            await Vm.IntegrateSelectedAsync(Selected);
        }
        catch (Exception ex) { Notice.Error(ex.Message); }
    }
}
