using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace VeyonSync.Desktop;

/// <summary>Janelas modais no tema do aplicativo (confirmação e detalhes), com entrada suave.</summary>
public static class ThemedDialog
{
    static T Res<T>(string key) where T : class => (Application.Current.TryFindResource(key) as T)!;

    static (Window w, StackPanel body, StackPanel buttons) Make(string title, string? glyph, string glyphKey, double width)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current?.MainWindow;
        var w = new Window
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false,
            Style = Res<Style>("AppWindow"),
            WindowStartupLocation = owner != null && owner.IsLoaded ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
        };
        w.Background = Brushes.Transparent;
        if (owner != null && owner.IsLoaded) w.Owner = owner;
        w.MaxHeight = SystemParameters.WorkArea.Height - 40;
        if (width > SystemParameters.WorkArea.Width - 40) w.Width = SystemParameters.WorkArea.Width - 40;

        var frame = new Border
        {
            Background = Res<Brush>("Surface"), BorderBrush = Res<Brush>("BorderBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Margin = new Thickness(14), Padding = new Thickness(24, 20, 24, 18),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black }
        };
        var root = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12), Background = Brushes.Transparent };
        if (glyph != null)
        {
            var icon = new TextBlock { Text = glyph, Style = Res<Style>("Icon"), FontSize = 20, Foreground = Res<Brush>(glyphKey), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(icon, Dock.Left);
            head.Children.Add(icon);
        }
        head.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { w.DragMove(); } catch (InvalidOperationException) { } };
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        var body = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = Math.Max(200, SystemParameters.WorkArea.Height - 260) });
        frame.Child = root;
        w.Content = frame;
        w.Loaded += (_, _) => Motion.FadeIn(frame, 160);
        return (w, body, buttons);
    }

    static TextBlock Text(string s, string? style = null)
    {
        var t = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        if (style != null) t.Style = Res<Style>(style);
        return t;
    }

    /// <summary>Confirmação. Retorna true somente se o botão principal for escolhido.</summary>
    public static bool Confirm(string title, string message, IEnumerable<string>? bullets = null, string? warning = null,
        string primary = "Confirmar", string cancel = "Cancelar", bool danger = false)
    {
        var (w, body, buttons) = Make(title, danger ? "\uE7BA" : "\uE9CE", danger ? "Warn" : "Accent", 540);
        body.Children.Add(Text(message));
        var list = bullets?.Where(b => !string.IsNullOrWhiteSpace(b)).ToList() ?? new List<string>();
        if (list.Count > 0)
        {
            var box = new Border { Style = Res<Style>("Inset"), Margin = new Thickness(0, 4, 0, 8) };
            var sp = new StackPanel();
            foreach (var b in list)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var dot = new TextBlock { Text = "•", Foreground = Res<Brush>("Accent"), Margin = new Thickness(0, 0, 8, 0) };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                row.Children.Add(new TextBlock { Text = b, TextWrapping = TextWrapping.Wrap });
                sp.Children.Add(row);
            }
            box.Child = sp;
            body.Children.Add(box);
        }
        if (!string.IsNullOrWhiteSpace(warning))
        {
            var warn = new Border { Background = Res<Brush>("WarnSoft"), BorderBrush = Res<Brush>("Warn"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 4, 0, 4) };
            var dp = new DockPanel();
            var ic = new TextBlock { Text = "\uE7BA", Style = Res<Style>("Icon"), Foreground = Res<Brush>("Warn"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(ic, Dock.Left);
            dp.Children.Add(ic);
            dp.Children.Add(new TextBlock { Text = warning, TextWrapping = TextWrapping.Wrap });
            warn.Child = dp;
            body.Children.Add(warn);
        }
        bool result = false;
        var no = new Button { Content = cancel, IsCancel = true, MinWidth = 110 };
        var yes = new Button { Content = primary, IsDefault = true, MinWidth = 130, Style = Res<Style>(danger ? "DangerButton" : "PrimaryButton") };
        yes.Click += (_, _) => { result = true; w.DialogResult = true; };
        no.Click += (_, _) => { w.DialogResult = false; };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        w.Loaded += (_, _) => (danger ? no : yes).Focus();
        w.ShowDialog();
        return result;
    }

    /// <summary>Pergunta um texto. Retorna null se cancelado.</summary>
    public static string? Prompt(string title, string message, string initial = "", string primary = "OK")
    {
        var (w, body, buttons) = Make(title, "\uE70F", "Accent", 500);
        body.Children.Add(Text(message));
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 4) };
        System.Windows.Automation.AutomationProperties.SetName(box, message);
        body.Children.Add(box);
        string? result = null;
        var no = new Button { Content = "Cancelar", IsCancel = true, MinWidth = 110 };
        var yes = new Button { Content = primary, IsDefault = true, MinWidth = 110, Style = Res<Style>("PrimaryButton") };
        yes.Click += (_, _) => { result = box.Text; w.DialogResult = true; };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.ShowDialog();
        return result;
    }

    /// <summary>Detalhes somente leitura (texto selecionável) com botão para copiar.</summary>
    public static void Details(string title, IEnumerable<(string Label, string? Value)> fields, string? body = null)
    {
        var (w, panel, buttons) = Make(title, "\uE946", "Accent", 620);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var copy = new System.Text.StringBuilder();
        int r = 0;
        foreach (var (label, value) in fields)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Style = Res<Style>("Label"), Margin = new Thickness(0, 4, 12, 4) };
            var v = new TextBox { Text = string.IsNullOrWhiteSpace(value) ? "—" : value, Style = Res<Style>("ReadOnlyValue"), Margin = new Thickness(0, 3, 0, 3) };
            Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
            copy.AppendLine($"{label}: {(string.IsNullOrWhiteSpace(value) ? "—" : value)}");
            r++;
        }
        panel.Children.Add(grid);
        if (!string.IsNullOrWhiteSpace(body))
        {
            panel.Children.Add(new TextBlock { Text = "Detalhes técnicos", Style = Res<Style>("Label"), Margin = new Thickness(0, 12, 0, 4) });
            var box = new Border { Style = Res<Style>("Inset") };
            box.Child = new TextBox { Text = body, Style = Res<Style>("ReadOnlyValue"), FontFamily = new FontFamily("Consolas"), FontSize = 12.5 };
            panel.Children.Add(box);
            copy.AppendLine().AppendLine(body);
        }
        var c = new Button { Content = "Copiar detalhes", MinWidth = 130 };
        c.Click += (_, _) => Clip.Copy(copy.ToString(), "Detalhes copiados");
        var close = new Button { Content = "Fechar", IsCancel = true, IsDefault = true, MinWidth = 110, Style = Res<Style>("PrimaryButton") };
        close.Click += (_, _) => w.Close();
        buttons.Children.Add(c);
        buttons.Children.Add(close);
        w.ShowDialog();
    }
}
