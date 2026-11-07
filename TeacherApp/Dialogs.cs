using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VeyonSync.Desktop;
using VeyonSync.Shared;

namespace VeyonSync.TeacherApp;

/// <summary>Caixa de texto simples no tema do aplicativo.</summary>
public static class InputBox
{
    public static string? Ask(string title, string prompt, string initial = "") => ThemedDialog.Prompt(title, prompt, initial);
}

/// <summary>Detalhes por máquina com histórico recente real.</summary>
public static class MachineDetails
{
    public static async void Show(TeacherVm vm, MachineRow m)
    {
        var w = new Window
        {
            Title = $"{m.DisplayName} — detalhes", Width = 760, Height = 620, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow, Style = (Style)Application.Current.FindResource("AppWindow")
        };
        var root = new DockPanel { Margin = new Thickness(20) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var fields = new (string, string?)[]
        {
            ("Nome exibido (Veyon)", m.DisplayName), ("Nome do Windows", m.WindowsName), ("Número / posição", m.Position),
            ("Laboratório", m.Lab), ("Usuário", m.User ?? "Sem usuário conectado"), ("Observação da sessão", m.SessionNote),
            ("IP", m.Ip), ("MAC", m.Mac), ("Última comunicação", m.LastSeen?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")),
            ("Primeiro cadastro", m.FirstSeen?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss")),
            ("Estado do agente", m.AgentState + (m.CollectPending ? " · coleta solicitada (atende na próxima comunicação)" : "")),
            ("Integração Veyon", m.VeyonState + " · disponibilidade não verificada"), ("Versão do STD", m.Version),
            ("Envio", m.AutoMode ? "Automático" : "Manual (dados podem estar desatualizados)"),
            ("Identidade duplicada", m.Duplicate ? "Suspeita de imagem clonada — reinstale o STD e pareie novamente" : "Não"),
            ("ID persistente do agente", m.AgentId)
        };
        for (int i = 0; i < fields.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = fields[i].Item1, Style = (Style)Application.Current.FindResource("Label"), Margin = new Thickness(0, 4, 8, 4) };
            var v = new TextBox { Text = string.IsNullOrWhiteSpace(fields[i].Item2) ? "—" : fields[i].Item2, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent, Margin = new Thickness(0, 0, 0, 2), Padding = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(l, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        var collect = new Button { Content = "Solicitar nova coleta" };
        collect.Click += async (_, _) => await vm.RequestCollectAsync(new[] { m });
        var rename = new Button { Content = "Alterar nome exibido" };
        rename.Click += async (_, _) => await vm.RenameAsync(m);
        actions.Children.Add(collect); actions.Children.Add(rename);

        var hist = new DataGrid { MaxHeight = 220 };
        hist.Columns.Add(new DataGridTextColumn { Header = "Quando", Binding = new System.Windows.Data.Binding("Ts") { Converter = new LocalTimeConverter() }, Width = 150 });
        hist.Columns.Add(new DataGridTextColumn { Header = "Tipo", Binding = new System.Windows.Data.Binding("Kind"), Width = 160 });
        hist.Columns.Add(new DataGridTextColumn { Header = "Detalhe", Binding = new System.Windows.Data.Binding("Detail"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var top = new StackPanel();
        top.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 330 });
        top.Children.Add(actions);
        top.Children.Add(new TextBlock { Text = "Histórico recente", Style = (Style)Application.Current.FindResource("H2") });
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(hist);
        w.Content = root;
        w.KeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.Show();
        try { hist.ItemsSource = await vm.Api.CallAsync<List<HistoryRow>>("history.query", new HistoryQuery { Machine = m.AgentId, Limit = 200 }); }
        catch (Exception e) { Notice.Error(e.Message); }
    }
}
