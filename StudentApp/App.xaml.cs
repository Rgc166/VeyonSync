using System.Windows;
using VeyonSync.Desktop;

namespace VeyonSync.StudentApp;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Palette.Std.Apply(Resources);
        ShellState.Current.Init("std");
        DispatcherUnhandledException += (_, ex) =>
        {
            Notice.Error("Erro inesperado: " + ex.Exception.Message);
            ex.Handled = true;
        };
        var w = new MainWindow();
        AppIcon.TryApply(w);
        MainWindow = w;
        w.Show();
    }
}
