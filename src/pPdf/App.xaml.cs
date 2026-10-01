using System.Windows;
using System.Windows.Threading;
using pPdf.Services;

namespace pPdf;

public partial class App : Application
{
    /// <summary>Tells the installer and the uninstaller that pPdf is running (AppMutex in pPdf.iss).</summary>
    static Mutex? _runningMutex;

    AppSettings _settings = new();

    public App()
    {
        _runningMutex = new Mutex(false, "pPdf.Running");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        // UI test harnesses build their own window offscreen
        if (Environment.GetEnvironmentVariable("PPDF_NO_STARTUP") == "1") return;

        // already running: hand the files to it and leave (no second process, no second runtime in memory)
        if (!SingleInstance.TryBecomePrimary())
        {
            if (SingleInstance.Forward(e.Args)) { Shutdown(); return; }
        }
        else
        {
            SingleInstance.StartServer(files => Dispatcher.BeginInvoke(() => OpenDocuments(files)));
        }

        _settings = AppSettings.Load();
        ThemeService.Apply(_settings.Theme);
        OpenDocuments(e.Args);
    }

    /// <summary>
    /// Opens each file in a window: the empty window there already is if any, otherwise a new one.
    /// With no files it brings a window forward (or opens an empty one).
    /// </summary>
    public void OpenDocuments(IReadOnlyList<string> files)
    {
        var windows = Windows.OfType<MainWindow>().ToList();
        if (files.Count == 0)
        {
            var existing = windows.LastOrDefault();
            Bring(existing ?? NewWindow());
            return;
        }
        foreach (string file in files)
        {
            var target = Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsEmpty) ?? NewWindow();
            Bring(target);
            _ = target.OpenAsync(file);
        }
    }

    public MainWindow NewWindow()
    {
        var previous = Windows.OfType<MainWindow>().LastOrDefault();
        var window = new MainWindow(_settings);
        if (MainWindow == null) MainWindow = window;
        else if (previous is { WindowState: WindowState.Normal })
        {
            // a new window opens a little down and to the right of the one before, not exactly on top of it
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = previous.Left + 32;
            window.Top = previous.Top + 32;
        }
        window.Show();
        return window;
    }

    static void Bring(Window w)
    {
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
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
            // the first window may be gone while others are open
            var owner = Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible);
            string text = "An unexpected error occurred:\n\n" + e.Exception.Message;
            if (owner != null) MessageBox.Show(owner, text, "pPdf", MessageBoxButton.OK, MessageBoxImage.Error);
            else MessageBox.Show(text, "pPdf", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _showing = false; }
    }
}
