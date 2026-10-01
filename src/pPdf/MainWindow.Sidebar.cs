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
    }

    void LoadSidebar(PdfFile pdf)
    {
        var items = new List<ThumbItem>(pdf.PageCount);
        for (int i = 0; i < pdf.PageCount; i++) items.Add(new ThumbItem(i, pdf.Pages[i]));
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
        OutlineEmpty.Visibility = nodes.Count == 0 && OutlineTab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void ClearSidebar()
    {
        ThumbList.ItemsSource = null;
        OutlineTree.ItemsSource = null;
        OutlineEmpty.Visibility = Visibility.Collapsed;
    }

    void OnThumbLoaded(object sender, RoutedEventArgs e)
    {
        if (_pdf != null && sender is FrameworkElement { DataContext: ThumbItem item })
            item.RequestLoad(Viewer.Renderer, _pdf, VisualTreeHelper_Dpi());
    }

    double VisualTreeHelper_Dpi() => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

    void OnThumbSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || ThumbList.SelectedIndex < 0) return;
        Viewer.GoToPage(ThumbList.SelectedIndex);
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
        if (e.NewValue is OutlineNode { PageIndex: >= 0 } node) Viewer.GoToPage(node.PageIndex);
    }

    void OnSideTabChanged(object sender, RoutedEventArgs e)
    {
        if (ThumbList == null) return; // while the XAML is still being built
        bool thumbs = ThumbTab.IsChecked == true;
        ThumbList.Visibility = thumbs ? Visibility.Visible : Visibility.Collapsed;
        OutlineTree.Visibility = thumbs ? Visibility.Collapsed : Visibility.Visible;
        OutlineEmpty.Visibility = !thumbs && _pdf != null && OutlineTree.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _settings.SidebarOutline = !thumbs;
        if (thumbs) SyncSidebarSelection();
    }

    void OnSidebarToggle(object sender, RoutedEventArgs e) => SetSidebar(SidebarToggle.IsChecked == true);

    void ToggleSidebar() => SetSidebar(!_settings.SidebarVisible);

    void SetSidebar(bool visible)
    {
        _settings.SidebarVisible = visible;
        SidebarToggle.IsChecked = visible;
        SidebarColumn.Width = new GridLength(visible ? _settings.SidebarWidth : 0);
        SidebarSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) SyncSidebarSelection();
    }

    void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (SidebarColumn.ActualWidth > 40) _settings.SidebarWidth = SidebarColumn.ActualWidth;
    }
}
