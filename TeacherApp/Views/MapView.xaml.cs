using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using VeyonSync.Desktop;
using VeyonSync.Shared;

namespace VeyonSync.TeacherApp.Views;

/// <summary>
/// Mapa físico: fileiras exibidas como colunas (estilo planilha). Posições guardam o AgentId (identidade estável).
/// </summary>
public partial class MapView : UserControl
{
    LabMap map = new();
    bool editing, dirty, loaded;
    const string DragFormat = "VeyonSync.AgentDrag";
    readonly Dictionary<string, Border> cards = new();

    sealed record DragInfo(string AgentId, int Row, int Slot); // Row = -1: veio da lista de não posicionados

    public MapView()
    {
        InitializeComponent();
        IsVisibleChanged += async (_, e) => { if ((bool)e.NewValue && !loaded) await LoadAsync(); };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TeacherVm vm)
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TeacherVm.Overview) && !editing && IsVisible) Render(); };
        };
    }

    TeacherVm Vm => (TeacherVm)DataContext;
    string Lab => Vm.Overview?.Lab ?? "";
    Dictionary<string, MachineRow> Machines => (Vm.Overview?.Machines ?? new()).ToDictionary(m => m.AgentId);

    async Task LoadAsync()
    {
        try
        {
            if (Vm.Overview == null) await Vm.RefreshAsync();
            map = await Vm.Api.CallAsync<LabMap>("map.get", new { lab = Lab }) ?? new LabMap { Lab = Lab };
            map.Lab = Lab;
            loaded = true;
            SetDirty(false);
            Render();
        }
        catch (Exception e) { Notice.Error(e.Message); }
    }

    void SetDirty(bool d) { dirty = d; DirtyText.Text = d ? "Alterações não salvas" : ""; }

    // ------------------------------------------------------------ desenho
    void Render()
    {
        cards.Clear();
        RowsPanel.Children.Clear();
        var ms = Machines;
        EmptyHint.Visibility = map.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int r = 0; r < map.Rows.Count; r++) RowsPanel.Children.Add(RowColumn(r, ms));
        RenderUnplaced(ms);
        ApplySearch();
    }

    UIElement RowColumn(int r, Dictionary<string, MachineRow> ms)
    {
        var row = map.Rows[r];
        var col = new StackPanel { Width = 176, Margin = new Thickness(0, 0, 16, 0) };
        if (editing)
        {
            var name = new TextBox { Text = row.Name, ToolTip = "Nome da fileira" };
            int ri = r;
            name.TextChanged += (_, _) => { map.Rows[ri].Name = name.Text; SetDirty(true); };
            col.Children.Add(name);
            var tools = new WrapPanel();
            tools.Children.Add(Tool("\uE76B", "Mover fileira para a esquerda", () => MoveRow(ri, -1)));
            tools.Children.Add(Tool("\uE76C", "Mover fileira para a direita", () => MoveRow(ri, +1)));
            tools.Children.Add(Tool("\uE710", "Adicionar posição", () => { map.Rows[ri].Slots.Add(null); Changed(); }));
            tools.Children.Add(Tool("\uE74D", "Remover fileira", () =>
            {
                if (map.Rows[ri].Slots.Any(s => s != null) && !Notice.Confirm("Remover a fileira? Os computadores dela voltam para 'Não posicionados'.")) return;
                map.Rows.RemoveAt(ri); Changed();
            }));
            col.Children.Add(tools);
        }
        else
        {
            col.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(row.Name) ? $"Fileira {r + 1}" : row.Name, Style = (Style)FindResource("H2"), HorizontalAlignment = HorizontalAlignment.Center });
        }
        for (int s = 0; s < row.Slots.Count; s++) col.Children.Add(Slot(r, s, row.Slots[s], ms));
        return col;
    }

    Button Tool(string glyph, string tip, Action a)
    {
        var b = new Button { Content = new TextBlock { Text = glyph, Style = (Style)FindResource("Icon"), FontSize = 12 }, ToolTip = tip, Padding = new Thickness(7, 4, 7, 4), Margin = new Thickness(0, 0, 4, 6) };
        AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => a();
        return b;
    }

    void Changed() { SetDirty(true); Render(); }

    void MoveRow(int r, int d)
    {
        var to = r + d;
        if (to < 0 || to >= map.Rows.Count) return;
        (map.Rows[r], map.Rows[to]) = (map.Rows[to], map.Rows[r]);
        Changed();
    }

    UIElement Slot(int r, int s, string? agentId, Dictionary<string, MachineRow> ms)
    {
        var holder = new Border { Margin = new Thickness(0, 0, 0, 8), MinHeight = 56, CornerRadius = new CornerRadius(8) };
        if (agentId != null && ms.TryGetValue(agentId, out var m))
        {
            holder.Child = Card(m, r, s);
        }
        else
        {
            holder.BorderBrush = (Brush)FindResource("BorderBrush");
            holder.BorderThickness = new Thickness(1);
            holder.Background = Brushes.Transparent;
            var empty = new DockPanel { Margin = new Thickness(10, 6, 6, 6) };
            if (editing)
            {
                int ri = r, si = s;
                var del = Tool("\uE711", "Remover esta posição vazia", () => { map.Rows[ri].Slots.RemoveAt(si); Changed(); });
                DockPanel.SetDock(del, Dock.Right);
                empty.Children.Add(del);
            }
            empty.Children.Add(new TextBlock
            {
                Text = agentId != null ? "Registro removido" : editing ? "Vazio / corredor — solte um PC aqui" : "Vazio",
                Foreground = (Brush)FindResource("TextMuted"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center
            });
            holder.Child = empty;
        }
        if (editing)
        {
            holder.AllowDrop = true;
            int ri = r, si = s;
            holder.DragOver += DragOverAny;
            holder.Drop += (_, e) => DropOnSlot(e, ri, si);
        }
        return holder;
    }

    Border Card(MachineRow m, int r, int s)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("Surface2"), BorderBrush = (Brush)FindResource("BorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8, 10, 8), Cursor = editing ? Cursors.SizeAll : Cursors.Hand,
            ToolTip = $"{m.WindowsName}\nIP: {m.Ip ?? "—"}\nUsuário: {m.User ?? "sem usuário conectado"}\nComunicação: {m.AgentState}\nIntegração: {m.VeyonState}\nEnvio: {(m.AutoMode ? "automático" : "manual")}",
            Focusable = true, FocusVisualStyle = (Style)FindResource("FocusRing")
        };
        AutomationProperties.SetName(card, $"{m.Position} {m.WindowsName}. {m.AgentState}. {m.VeyonState}." + (m.AutoMode ? "" : " Envio manual."));
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Monitor + nome
        var mon = new TextBlock { Text = "\uE7F4", Style = (Style)FindResource("Icon"), FontSize = 15, Foreground = (Brush)FindResource("TextMuted"), Margin = new Thickness(0, 0, 8, 0) };
        grid.Children.Add(mon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(m.Position) ? m.DisplayName : m.Position, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = m.WindowsName, Foreground = (Brush)FindResource("TextMuted"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // Indicadores independentes: comunicação (bolinha), integração (ícone) e envio manual (M).
        var flags = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, 2, 0, 0) };
        flags.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = (Brush)FindResource(m.Recent ? "Ok" : "TextMuted"), ToolTip = m.AgentState, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var (vGlyph, vKey) = m.Duplicate || m.OpenErrors > 0 || m.VeyonState is VeyonStates.Conflict or VeyonStates.Error ? ("\uE783", "Err")
            : m.VeyonState == VeyonStates.Applied ? ("\uE73E", "Ok") : ("\uE895", "Warn");
        flags.Children.Add(new TextBlock
        {
            Text = vGlyph, Style = (Style)FindResource("Icon"), FontSize = 11, Foreground = (Brush)FindResource(vKey), VerticalAlignment = VerticalAlignment.Center,
            ToolTip = m.Duplicate ? "Identidade duplicada" : m.OpenErrors > 0 ? $"{m.OpenErrors} erro(s) ativo(s)" : m.VeyonState
        });
        if (!m.AutoMode)
            flags.Children.Add(new Border
            {
                Background = (Brush)FindResource("WarnSoft"), CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Envio manual (os dados chegam quando alguém envia neste computador)",
                Child = new TextBlock { Text = "M", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Warn") }
            });
        Grid.SetColumn(flags, 2);
        grid.Children.Add(flags);
        card.Child = grid;
        cards[m.AgentId] = card;

        if (editing)
        {
            card.MouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                    DragDrop.DoDragDrop(card, new DataObject(DragFormat, new DragInfo(m.AgentId, r, s)), DragDropEffects.Move);
            };
        }
        else
        {
            card.MouseLeftButtonUp += (_, _) => OpenMachine(m);
            card.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) OpenMachine(m); };
            card.MouseEnter += (_, _) => card.BorderBrush = (Brush)FindResource("Accent");
            card.MouseLeave += (_, _) => ApplySearch();
        }
        return card;
    }

    void OpenMachine(MachineRow m)
    {
        // Abre o painel do computador na tela Computadores.
        Vm.OpenMachines("Todos", "Todos");
        Vm.SelectedMachine = m;
    }

    void RenderUnplaced(Dictionary<string, MachineRow> ms)
    {
        UnplacedList.Children.Clear();
        var placed = map.Rows.SelectMany(r => r.Slots).Where(x => x != null).ToHashSet();
        foreach (var m in ms.Values.Where(m => !placed.Contains(m.AgentId)).OrderBy(m => Naming.PositionNumber(m.Position) ?? int.MaxValue).ThenBy(m => m.WindowsName))
        {
            var item = new Border
            {
                Background = (Brush)FindResource("Surface2"), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.SizeAll,
                Child = new TextBlock { Text = $"{(string.IsNullOrEmpty(m.Position) ? "(sem nº)" : m.Position)} · {m.WindowsName}" }
            };
            item.MouseMove += (_, e) =>
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                    DragDrop.DoDragDrop(item, new DataObject(DragFormat, new DragInfo(m.AgentId, -1, -1)), DragDropEffects.Move);
            };
            UnplacedList.Children.Add(item);
        }
        if (UnplacedList.Children.Count == 0)
            UnplacedList.Children.Add(new TextBlock { Text = "Todos os computadores estão no mapa.", Foreground = (Brush)FindResource("TextMuted"), FontSize = 12 });
    }

    // ------------------------------------------------------------ arrastar e soltar
    void DragOverAny(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    void DropOnSlot(DragEventArgs e, int r, int s)
    {
        if (e.Data.GetData(DragFormat) is not DragInfo d) return;
        e.Handled = true;
        var target = map.Rows[r].Slots[s];
        if (d.Row >= 0)
        {
            if (d.Row == r && d.Slot == s) return;
            map.Rows[d.Row].Slots[d.Slot] = target; // troca de lugar
        }
        else
        {
            // Garante unicidade: a máquina não pode estar em duas posições.
            foreach (var row in map.Rows)
                for (int i = 0; i < row.Slots.Count; i++)
                    if (row.Slots[i] == d.AgentId) row.Slots[i] = null;
        }
        map.Rows[r].Slots[s] = d.AgentId;
        Changed();
    }

    void DropOnUnplaced(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not DragInfo d || d.Row < 0) return;
        map.Rows[d.Row].Slots[d.Slot] = null;
        e.Handled = true;
        Changed();
    }

    // ------------------------------------------------------------ ações
    bool switching;

    void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (switching || EditBar == null || UnplacedPanel == null || DataContext == null) return;
        var wantEdit = EditMode.IsChecked == true;
        if (wantEdit == editing) return;
        if (editing && dirty && !Notice.Confirm("Há alterações não salvas na disposição. Voltar para Visualizar e descartá-las?", "Descartar alterações"))
        {
            switching = true; EditMode.IsChecked = true; switching = false;
            return;
        }
        SetEditing(wantEdit);
        if (!wantEdit && dirty) _ = LoadAsync();
    }

    void SetEditing(bool on)
    {
        editing = on;
        switching = true;
        if (on) EditMode.IsChecked = true; else ViewMode.IsChecked = true;
        switching = false;
        EditBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UnplacedPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    // ------------------------------------------------------------ zoom
    void SetZoom(double z)
    {
        z = Math.Clamp(Math.Round(z, 2), 0.5, 1.6);
        Zoom.ScaleX = Zoom.ScaleY = z;
        ZoomText.Text = $"{z * 100:0}%";
    }

    void ZoomIn(object sender, RoutedEventArgs e) => SetZoom(Zoom.ScaleX + 0.1);
    void ZoomOut(object sender, RoutedEventArgs e) => SetZoom(Zoom.ScaleX - 0.1);

    void ZoomFit(object sender, RoutedEventArgs e)
    {
        RowsPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var need = RowsPanel.DesiredSize;
        if (need.Width <= 0 || need.Height <= 0) { SetZoom(1); return; }
        var fit = Math.Min((MapScroll.ViewportWidth - 8) / need.Width, (MapScroll.ViewportHeight - 8) / need.Height);
        SetZoom(Math.Min(1.2, fit));
    }

    async void Reload(object sender, RoutedEventArgs e)
    {
        if (dirty && !Notice.Confirm("Descartar alterações não salvas?")) return;
        await Vm.RefreshAsync();
        await LoadAsync();
    }

    void AddRow(object sender, RoutedEventArgs e)
    {
        map.Rows.Add(new MapRow { Name = $"Fileira {map.Rows.Count + 1}", Slots = { null, null, null, null, null } });
        Changed();
    }

    void Generate(object sender, RoutedEventArgs e)
    {
        var parts = CountsBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var counts = new List<int>();
        foreach (var p in parts)
        {
            if (!int.TryParse(p, out var n) || n < 1 || n > 60) { Notice.Error("Use números de 1 a 60 separados por vírgula, por exemplo 5,6,6,6."); return; }
            counts.Add(n);
        }
        if (counts.Count == 0 || counts.Count > 50) { Notice.Error("Informe de 1 a 50 fileiras."); return; }
        if (map.Rows.Count > 0 && !Notice.Confirm("Substituir as fileiras atuais? Os computadores voltam para 'Não posicionados'.")) return;
        map.Rows = counts.Select((c, i) => new MapRow { Name = $"Fileira {i + 1}", Slots = Enumerable.Repeat<string?>(null, c).ToList() }).ToList();
        Changed();
    }

    void AutoPlace(object sender, RoutedEventArgs e)
    {
        var placed = map.Rows.SelectMany(r => r.Slots).Where(x => x != null).ToHashSet();
        var pending = Machines.Values.Where(m => !placed.Contains(m.AgentId) && Naming.PositionNumber(m.Position) != null)
            .OrderBy(m => Naming.PositionNumber(m.Position)).ToList();
        // Posição N do mapa (contando fileira por fileira) recebe o PC de número N, se estiver vazia.
        var flat = map.Rows.SelectMany((row, ri) => row.Slots.Select((_, si) => (ri, si))).ToList();
        int moved = 0;
        foreach (var m in pending)
        {
            var n = Naming.PositionNumber(m.Position)!.Value - 1;
            if (n < 0 || n >= flat.Count) continue;
            var (ri, si) = flat[n];
            if (map.Rows[ri].Slots[si] != null) continue;
            map.Rows[ri].Slots[si] = m.AgentId;
            moved++;
        }
        Notice.Info($"{moved} computador(es) posicionados pelo número. Os demais continuam em 'Não posicionados'.");
        Changed();
    }

    async void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            map.Lab = Lab;
            await Vm.Api.CallAsync("map.save", map);
            SetDirty(false);
            Notice.Ok("Disposição salva.");
            SetEditing(false);
        }
        catch (Exception ex) { Notice.Error(ex.Message); }
    }

    async void Discard(object sender, RoutedEventArgs e)
    {
        if (dirty && !Notice.Confirm("Descartar as alterações da disposição?", "Cancelar alterações")) return;
        await LoadAsync();
        SetEditing(false);
    }

    void SearchChanged(object sender, TextChangedEventArgs e) => ApplySearch();

    void ApplySearch()
    {
        var q = SearchBox.Text.Trim();
        var ms = Machines;
        Border? first = null;
        foreach (var (id, card) in cards)
        {
            var hit = q.Length > 0 && ms.TryGetValue(id, out var m)
                      && new[] { m.Position, m.DisplayName, m.WindowsName, m.Ip, m.User }.Any(f => f != null && f.Contains(q, StringComparison.OrdinalIgnoreCase));
            card.BorderBrush = (Brush)FindResource(hit ? "Accent" : "BorderBrush");
            card.BorderThickness = new Thickness(hit ? 2 : 1);
            if (hit && first == null) first = card;
        }
        first?.BringIntoView();
    }
}
