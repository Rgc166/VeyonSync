using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VeyonSync.Desktop;

/// <summary>Item da barra lateral.</summary>
public sealed class NavEntry(string glyph, string label)
{
    public string Glyph { get; } = glyph;
    public string Label { get; } = label;
}

/// <summary>Opção de lista com rótulo amigável: a tela mostra o rótulo e grava a chave interna.</summary>
public sealed class Choice(string key, string label)
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public override string ToString() => Label;
}

/// <summary>Preferências visuais deste usuário do Windows (não afetam o serviço nem outros computadores).</summary>
public sealed class UiPrefs
{
    public bool Animations { get; set; } = true;
    public bool SidebarCollapsed { get; set; }

    static string F(string app) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VeyonSync", $"ui-{app}.json");

    public static UiPrefs Load(string app)
    {
        try { return File.Exists(F(app)) ? JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(F(app))) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    public void Save(string app)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(F(app))!);
            File.WriteAllText(F(app), JsonSerializer.Serialize(this));
        }
        catch (Exception) { } // preferência visual: falhar ao gravar não pode atrapalhar o uso
    }
}

/// <summary>Estado da moldura (barra lateral recolhida, animações). Usado por bindings no tema.</summary>
public sealed class ShellState : ObservableObject
{
    public static ShellState Current { get; } = new();
    string app = "app";
    bool collapsed, animations = true;

    public bool Collapsed { get => collapsed; set { if (Set(ref collapsed, value)) Persist(); } }
    /// <summary>Preferência do usuário. As animações também respeitam a configuração de efeitos do Windows.</summary>
    public bool Animations { get => animations; set { if (Set(ref animations, value)) Persist(); } }

    public void Init(string appName)
    {
        app = appName;
        var p = UiPrefs.Load(app);
        collapsed = p.SidebarCollapsed;
        animations = p.Animations;
        Raise(nameof(Collapsed));
        Raise(nameof(Animations));
    }

    void Persist() => new UiPrefs { Animations = animations, SidebarCollapsed = collapsed }.Save(app);
}

/// <summary>Transições discretas (150–250 ms). Desligadas quando o usuário ou o Windows reduzem animações.</summary>
public static class Motion
{
    public static bool Enabled => ShellState.Current.Animations && SystemParameters.ClientAreaAnimation;
    static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    public static void EnterPage(FrameworkElement e)
    {
        if (!Enabled)
        {
            e.BeginAnimation(UIElement.OpacityProperty, null);
            e.Opacity = 1;
            e.RenderTransform = Transform.Identity;
            return;
        }
        var t = new TranslateTransform(0, 10);
        e.RenderTransform = t;
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = Ease });
        t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ease });
    }

    public static void FadeIn(UIElement e, int ms = 180)
    {
        if (!Enabled) { e.BeginAnimation(UIElement.OpacityProperty, null); e.Opacity = 1; return; }
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ease });
    }

    public static void Width(FrameworkElement e, double to)
    {
        if (!Enabled || double.IsNaN(e.Width)) { e.BeginAnimation(FrameworkElement.WidthProperty, null); e.Width = to; return; }
        e.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(e.ActualWidth, to, TimeSpan.FromMilliseconds(180)) { EasingFunction = Ease });
    }
}

public static class AdminInfo
{
    /// <summary>Somente informativo: a proteção real é a ACL do pipe do serviço (administradores e SYSTEM).</summary>
    public static bool IsElevated
    {
        get
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch (Exception) { return false; }
        }
    }
}

public static class Clip
{
    public static void Copy(string? text, string what = "Copiado")
    {
        if (string.IsNullOrEmpty(text)) { Notice.Error("Nada para copiar."); return; }
        for (int i = 0; i < 3; i++)
        {
            try { Clipboard.SetText(text); Notice.Ok(what + "."); return; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); } // área de transferência ocupada por outro programa
        }
        Notice.Error("A área de transferência está ocupada. Tente copiar de novo.");
    }
}

// ---------------------------------------------------------------- conversores

public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !(value is bool b && b);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => !(value is bool b && b);
}

/// <summary>Texto não vazio → visível (parâmetro "invert" inverte).</summary>
public sealed class TextVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value is string s ? !string.IsNullOrWhiteSpace(s) : value != null;
        if ((parameter as string) == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Número maior que zero (ou coleção com itens) → visível (parâmetro "invert" inverte).</summary>
public sealed class CountVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var n = value switch
        {
            int i => i,
            long l => (int)Math.Min(l, int.MaxValue),
            System.Collections.ICollection c => c.Count,
            _ => 0
        };
        var has = n > 0;
        if ((parameter as string) == "invert") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Tipo de estado ("ok", "warn", "err", "info", "accent", "muted") → pincel do tema.</summary>
public static class Kinds
{
    public static string BrushKey(string? kind) => kind switch
    {
        "ok" => "Ok", "warn" => "Warn", "err" => "Err", "accent" => "Accent", "primary" => "Primary", "muted" => "TextMuted", _ => "Accent"
    };
    public static string SoftKey(string? kind) => kind switch
    {
        "ok" => "OkSoft", "warn" => "WarnSoft", "err" => "ErrSoft", "muted" => "Surface2", _ => "AccentSoft"
    };
    public static string Glyph(string? kind) => kind switch
    {
        "ok" => "\uE73E", "warn" => "\uE7BA", "err" => "\uE783", "muted" => "\uE9CE", "pending" => "\uE916", _ => "\uE946"
    };
    public static Brush Find(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

public sealed class KindBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Kinds.Find(Kinds.BrushKey(value as string));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class KindSoftBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Kinds.Find(Kinds.SoftKey(value as string));
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class KindGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Kinds.Glyph(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
