using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VeyonSync.Desktop;
using VeyonSync.StudentApp.Views;

namespace VeyonSync.StudentApp;

public partial class MainWindow : Window
{
    readonly StudentVm vm = new();
    readonly FrameworkElement[] pages;
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };
    bool syncingNav;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = vm;
        // Ordem da barra lateral (StudentVm.NavEntries) + primeiro acesso/credenciamento (fora da barra).
        pages = new FrameworkElement[] { new OverviewView(), new SendView(), new ErrorsView(), new UpdatesView(), new SettingsView(), new PairingView() };
        foreach (var p in pages) p.DataContext = vm;
        vm.PropertyChanged += OnVm;
        Notice.Sink = ShowNotice;
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        FitToScreen();
        TryLogo();
        ApplySidebar(false);
        SyncNav();
        ShowPage(vm.Page);
    }

    void FitToScreen()
    {
        var wa = SystemParameters.WorkArea;
        if (Width > wa.Width - 24) Width = Math.Max(MinWidth, wa.Width - 24);
        if (Height > wa.Height - 24) Height = Math.Max(MinHeight, wa.Height - 24);
        if (wa.Width < 1100) ShellState.Current.Collapsed = true;
    }

    void TryLogo()
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "icon.ico");
            if (!File.Exists(p)) { Logo.Visibility = Visibility.Collapsed; return; }
            Logo.Source = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(p));
            LogoGlyph.Visibility = Visibility.Collapsed;
        }
        catch (Exception) { Logo.Visibility = Visibility.Collapsed; }
    }

    void OnVm(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StudentVm.Page)) { SyncNav(); ShowPage(vm.Page); }
    }

    void NavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingNav || Nav.SelectedIndex < 0) return;
        vm.Page = Nav.SelectedIndex;
    }

    void SyncNav()
    {
        syncingNav = true;
        try { Nav.SelectedIndex = vm.Page >= 0 && vm.Page < Nav.Items.Count ? vm.Page : -1; }
        finally { syncingNav = false; }
    }

    void ShowPage(int i)
    {
        if (i < 0 || i >= pages.Length) return;
        Host.Content = pages[i];
        Motion.EnterPage(pages[i]);
    }

    void ToggleSidebar(object sender, RoutedEventArgs e)
    {
        ShellState.Current.Collapsed = !ShellState.Current.Collapsed;
        ApplySidebar(true);
    }

    void ApplySidebar(bool animate)
    {
        var c = ShellState.Current.Collapsed;
        double w = c ? 70 : 236;
        if (animate) Motion.Width(Side, w); else Side.Width = w;
        Brand.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
        ToggleText.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
        VersionLabel.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
        ToggleGlyph.Text = c ? "" : "";
        ToggleSide.ToolTip = c ? "Expandir a barra lateral" : "Recolher a barra lateral";
    }

    void ShowNotice(string kind, string msg)
    {
        if (kind == "err")
        {
            ErrorText.Text = msg;
            ErrorBar.Visibility = Visibility.Visible;
            Motion.FadeIn(ErrorBar);
            return;
        }
        var key = kind == "ok" ? "Ok" : "Accent";
        ToastIcon.Foreground = (Brush)FindResource(key);
        ToastIcon.Text = kind == "ok" ? "" : "";
        ToastText.Text = msg;
        Toast.BorderBrush = (Brush)FindResource(key);
        Toast.Visibility = Visibility.Visible;
        Motion.FadeIn(Toast);
        toastTimer.Stop();
        toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(msg.Length / 18.0, 4.5, 12));
        toastTimer.Start();
    }

    void CloseError(object sender, RoutedEventArgs e) => ErrorBar.Visibility = Visibility.Collapsed;
}
