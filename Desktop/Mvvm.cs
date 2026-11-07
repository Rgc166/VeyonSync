using System.IO;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace VeyonSync.Desktop;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;
    public void Execute(object? p) => run(p);
}

/// <summary>Comando assíncrono: desabilita enquanto executa e mostra erros sem travar a interface.</summary>
public sealed class AsyncCommand(Func<object?, Task> run, Func<object?, bool>? can = null) : ICommand
{
    bool running;
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => !running && (can?.Invoke(p) ?? true);
    public async void Execute(object? p)
    {
        running = true;
        CommandManager.InvalidateRequerySuggested();
        try { await run(p); }
        catch (Exception e) { Notice.Error(e.Message); }
        finally { running = false; CommandManager.InvalidateRequerySuggested(); }
    }
}

/// <summary>Barra de aviso da janela principal (sucesso/erro) — o texto sempre acompanha a cor.</summary>
public static class Notice
{
    public static Action<string, string>? Sink;
    public static void Info(string msg) => Show("info", msg);
    public static void Ok(string msg) => Show("ok", msg);
    public static void Error(string msg) => Show("err", msg);
    static void Show(string kind, string msg)
    {
        if (Sink != null) Application.Current.Dispatcher.Invoke(() => Sink(kind, msg));
        else MessageBox.Show(msg);
    }

    /// <summary>Confirmação no tema do aplicativo (substitui a caixa padrão do Windows).</summary>
    public static bool Confirm(string text, string title = "Confirmar") =>
        ThemedDialog.Confirm(title, text);
}

public sealed record Palette(string Bg, string Surface, string Surface2, string Surface3, string Border, string Text, string TextMuted,
    string Primary, string PrimaryHover, string OnPrimary, string Accent)
{
    // Professor: azul/ciano. Aluno: laranja/amarelo (texto escuro sobre o laranja para manter o contraste).
    public static readonly Palette Thc = new("#0B1220", "#111C2E", "#17243A", "#1E2E48", "#25344D", "#E6EDF7", "#9AA8BD", "#2563EB", "#3B76F0", "#FFFFFF", "#22D3EE");
    public static readonly Palette Std = new("#15120F", "#211B16", "#2B231C", "#362C23", "#3A3027", "#F3ECE4", "#B5A797", "#F97316", "#FB8A3C", "#1A0F05", "#FACC15");

    public void Apply(ResourceDictionary r)
    {
        static SolidColorBrush B(string hex, byte? alpha = null)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            if (alpha != null) c.A = alpha.Value;
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
        r["Bg"] = B(Bg); r["Surface"] = B(Surface); r["Surface2"] = B(Surface2); r["Surface3"] = B(Surface3); r["BorderBrush"] = B(Border);
        r["Text"] = B(Text); r["TextMuted"] = B(TextMuted); r["Primary"] = B(Primary); r["PrimaryHover"] = B(PrimaryHover);
        r["PrimarySoft"] = B(Primary, 0x33); r["OnPrimary"] = B(OnPrimary); r["Accent"] = B(Accent); r["AccentSoft"] = B(Accent, 0x26);
    }
}

/// <summary>Converte um texto de estado em ícone + pincel. Erro/aviso usam cores próprias, distintas da marca.</summary>
public sealed class StateGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = (value as string ?? "").ToLowerInvariant();
        var kind = Classify(s);
        if ((parameter as string) == "brush")
            return Application.Current.TryFindResource(kind switch { "ok" => "Ok", "err" => "Err", "warn" => "Warn", _ => "TextMuted" }) ?? Brushes.Gray;
        return kind switch { "ok" => "\uE73E", "err" => "\uE783", "warn" => "\uE7BA", _ => "\uE946" };
    }

    public static string Classify(string s)
    {
        if (s.StartsWith("nenhum") || s == "ok") return "ok";
        if (s == "aviso") return "warn";
        if (s.Contains("falh") || s.Contains("erro") || s.Contains("recus") || s.Contains("inválid")) return "err";
        if (s.Contains("conflito") || s.Contains("sem resposta") || s.Contains("parado") || s.Contains("pendente") || s.Contains("sem comunicação") || s.Contains("manual") || s.Contains("aguardando") || s.Contains("não verificada") || s.Contains("ainda não")) return "warn";
        if (s.Contains("confirmad") || s.Contains("respondeu") || s.Contains("em execução") || s.Contains("aplicado") || s.Contains("recente") || s.Contains("integrado") || s.Contains("atualizado") || s.Contains("concluída")) return "ok";
        return "info";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class LocalTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset d when d != default => d.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
        DateTime t when t != default => t.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"),
        _ => "—"
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class EmptyDashConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? s : value is null or string ? "—" : value;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is bool v && v;
        if ((parameter as string) == "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public static class AppIcon
{
    /// <summary>Usa icon.ico ao lado do executável quando o ícone definitivo for fornecido.</summary>
    public static void TryApply(Window w)
    {
        try
        {
            var p = System.IO.Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (File.Exists(p)) w.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(p));
        }
        catch (Exception) { }
    }
}
