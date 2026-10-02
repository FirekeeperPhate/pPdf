using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace pPdf.Services;

/// <summary>Light / dark / follow-the-system: the Fluent ThemeMode for the controls, plus the app's own brushes.</summary>
public static class ThemeService
{
    static AppTheme _theme = AppTheme.System;

    public static bool IsDark { get; private set; }
    public static event EventHandler? Changed;
    /// <summary>Just before the controls are re-themed (which re-templates them and resets things like scroll positions).</summary>
    public static event EventHandler? Changing;

    public static void Apply(AppTheme theme)
    {
        _theme = theme;
        Refresh();
    }

    static ThemeService()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_theme == AppTheme.System && e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.BeginInvoke(Refresh);
        };
    }

    static void Refresh()
    {
        var app = Application.Current;
        if (app == null) return;
        IsDark = _theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemUsesDarkApps(),
        };
        Changing?.Invoke(null, EventArgs.Empty);
        app.ThemeMode = _theme switch
        {
            AppTheme.Dark => ThemeMode.Dark,
            AppTheme.Light => ThemeMode.Light,
            _ => ThemeMode.System,
        };

        SetBrush("ViewerBackgroundBrush", IsDark ? "#26282B" : "#E4E5E8");
        SetBrush("BarBackgroundBrush", IsDark ? "#2B2D31" : "#F6F6F7");
        SetBrush("BarBorderBrush", IsDark ? "#3A3C41" : "#D9D9DD");
        SetBrush("SubtleTextBrush", IsDark ? "#A3A6AD" : "#6B6B70");

        foreach (Window w in app.Windows) ApplyTitleBar(w);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    static void SetBrush(string key, string color)
        => Application.Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));

    static bool SystemUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    // ------------------------------------------------------------------ title bar

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Makes the caption follow the theme (the Fluent ThemeMode does not touch the non-client area on every build).</summary>
    public static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }
}
