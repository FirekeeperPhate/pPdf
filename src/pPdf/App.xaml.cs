using System.Windows;
using System.Windows.Threading;
using pPdf.Services;

namespace pPdf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        // UI test harnesses build their own window offscreen
        if (Environment.GetEnvironmentVariable("PPDF_NO_STARTUP") == "1") return;

        var settings = AppSettings.Load();
        ThemeService.Apply(settings.Theme);
        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0) _ = window.OpenAsync(e.Args[0]);
    }

    static bool _showing;

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // an unexpected error must not take the document (and any annotations) down with it
        e.Handled = true;
        if (_showing) return;
        _showing = true;
        try
        {
            System.Diagnostics.Debug.WriteLine(e.Exception);
            MessageBox.Show(Current.MainWindow, "An unexpected error occurred:\n\n" + e.Exception.Message, "pPdf", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _showing = false; }
    }
}
