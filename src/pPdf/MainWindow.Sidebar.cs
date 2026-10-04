using System.Windows;
using System.Windows.Controls;
using pPdf.Pdf;
using pPdf.Services;

namespace pPdf;

/// <summary>The page thumbnails and the document outline.</summary>
public partial class MainWindow
{
    void InitSidebar()
    {
        ThumbList.ItemsSource = null;
        // after a click on a page the keyboard belongs to the document again (arrows, PgUp / PgDn...)
        ThumbList.PreviewMouseLeftButtonUp += (_, _) => Viewer.Focus();
    }

    List<ThumbItem>? _thumbItems;

    /// <summary>Thumbnails are only rendered while the panel is open on the Pages tab.</summary>
    bool ThumbsActive => _pdf != null && _settings.SidebarVisible && ThumbTab.IsChecked == true;

    /// <summary>The rows that were built while the panel was hidden never asked for their bitmap: build them again now.</summary>
    void RefreshThumbs()
    {
        if (_thumbItems == null || !ThumbsActive) return;
        ThumbList.ItemsSource = null;
        ThumbList.ItemsSource = _thumbItems;
        SyncSidebarSelection();
    }

    void LoadSidebar(PdfFile pdf)
    {
        var items = new List<ThumbItem>(pdf.PageCount);
        for (int i = 0; i < pdf.PageCount; i++) items.Add(new ThumbItem(i, pdf.Pages[i]));
        _thumbItems = items;
        ThumbList.ItemsSource = items;

        OutlineTree.ItemsSource = null;
        OutlineEmpty.Visibility = Visibility.Collapsed;
        _ = LoadOutlineAsync(pdf);
    }

    async Task LoadOutlineAsync(PdfFile pdf)
    {
        var nodes = await Task.Run(pdf.LoadOutline);
        if (_pdf != pdf) return;
        OutlineTree.ItemsSource = nodes;

        // the Outline is the tab people want first; a document without one shows its pages instead (not remembered as a choice)
        if (!_settings.SidebarPages)
        {
            if (nodes.Count == 0 && OutlineTab.IsChecked == true) ShowSideTab(thumbs: true, automatic: true);
            else if (nodes.Count > 0 && _autoPages) ShowSideTab(thumbs: false, automatic: true);
        }
        OutlineEmpty.Visibility = nodes.Count == 0 && OutlineTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    bool _autoSwitch;
    /// <summary>The Pages tab is showing only because this document has no outline.</summary>
    bool _autoPages;

    /// <summary>Switches tab; an automatic switch is not the user's choice, so it is not saved and is undone for the next document that has an outline.</summary>
    void ShowSideTab(bool thumbs, bool automatic)
    {
        _autoSwitch = automatic;
        try { (thumbs ? ThumbTab : OutlineTab).IsChecked = true; }
        finally { _autoSwitch = false; }
        _autoPages = automatic && thumbs;
    }

    void ClearSidebar()
    {
        _thumbItems = null;
        ThumbList.ItemsSource = null;
        OutlineTree.ItemsSource = null;
        OutlineEmpty.Visibility = Visibility.Collapsed;
    }

    void OnThumbLoaded(object sender, RoutedEventArgs e)
    {
        if (ThumbsActive && sender is FrameworkElement { DataContext: ThumbItem item })
            item.RequestLoad(Viewer.Renderer, _pdf!, VisualTreeHelper_Dpi());
    }

    double VisualTreeHelper_Dpi() => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

    void OnThumbSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ThumbList.SelectedIndex < 0) return;
        Viewer.JumpToPage(ThumbList.SelectedIndex);
    }

    void SyncSidebarSelection()
    {
        if (_pdf == null || !_settings.SidebarVisible || ThumbTab.IsChecked != true) return;
        int page = Viewer.CurrentPage;
        if (ThumbList.SelectedIndex != page && page >= 0 && page < ThumbList.Items.Count)
        {
            ThumbList.SelectedIndex = page;
            ThumbList.ScrollIntoView(ThumbList.Items[page]);
        }
    }

    void OnOutlineSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OutlineNode { PageIndex: >= 0 } node) Viewer.JumpToPage(node.PageIndex);
    }

    void OnSideTabChanged(object sender, RoutedEventArgs e)
    {
        if (ThumbList == null) return; // while the XAML is still being built
        bool thumbs = ThumbTab.IsChecked == true;
        ThumbList.Visibility = thumbs ? Visibility.Visible : Visibility.Collapsed;
        OutlineTree.Visibility = thumbs ? Visibility.Collapsed : Visibility.Visible;
        OutlineEmpty.Visibility = !thumbs && _pdf != null && OutlineTree.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_autoSwitch) { _settings.SidebarPages = thumbs; _autoPages = false; }
        if (thumbs) RefreshThumbs();
    }

    void OnSidebarToggle(object sender, RoutedEventArgs e) => SetSidebar(SidebarToggle.IsChecked == true);

    void ToggleSidebar() => SetSidebar(!_settings.SidebarVisible);

    void SetSidebar(bool visible)
    {
        _settings.SidebarVisible = visible;
        SidebarToggle.IsChecked = visible;
        SidebarColumn.Width = new GridLength(visible ? _settings.SidebarWidth : 0);
        SidebarSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) RefreshThumbs();
    }

    void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (SidebarColumn.ActualWidth > 40) _settings.SidebarWidth = SidebarColumn.ActualWidth;
    }
}
