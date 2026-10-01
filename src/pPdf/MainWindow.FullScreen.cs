using System.Windows;

namespace pPdf;

/// <summary>F11: the document alone, without toolbars, panel or status bar.</summary>
public partial class MainWindow
{
    bool _fullScreen;
    WindowState _beforeFullScreen;
    WindowStyle _beforeStyle;
    ResizeMode _beforeResize;
    bool _beforeSidebar;

    void ToggleFullScreen()
    {
        if (!_fullScreen)
        {
            _beforeFullScreen = WindowState;
            _beforeStyle = WindowStyle;
            _beforeResize = ResizeMode;
            _beforeSidebar = _settings.SidebarVisible;
            _fullScreen = true;
            MainToolbar.Visibility = Visibility.Collapsed;
            StatusBar.Visibility = Visibility.Collapsed;
            AnnotationBar.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitter.Visibility = Visibility.Collapsed;
            // a borderless maximized window covers the taskbar too
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            _fullScreen = false;
            WindowStyle = _beforeStyle;
            ResizeMode = _beforeResize;
            WindowState = _beforeFullScreen;
            MainToolbar.Visibility = Visibility.Visible;
            StatusBar.Visibility = Visibility.Visible;
            SetSidebar(_beforeSidebar); // also when F4 opened or closed the panel in the meantime
            UpdateAnnotationBar();
        }
        Viewer.Focus();
    }
}
