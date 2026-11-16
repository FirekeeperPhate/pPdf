using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using pPdf.Annotations;
using pPdf.Views;

namespace pPdf;

/// <summary>The Marks tab of the side panel: the user's bookmarks, and every annotation of the document, to jump to.</summary>
public partial class MainWindow
{
    /// <summary>The words under each highlight / underline / strike-through, read once from the page (by annotation id).</summary>
    readonly Dictionary<Guid, string> _markupTexts = [];

    bool MarksActive => _settings.SidebarVisible && MarksTab.IsChecked == true;

    void InitMarks()
    {
        Viewer.Annotations.HistoryChanged += () => { if (MarksActive) RefreshMarks(); };
    }

    void RefreshMarks()
    {
        if (BookmarkItems == null) return;
        BookmarkItems.Children.Clear();
        AnnotationItems.Children.Clear();
        int pages = _pdf?.PageCount ?? 0;
        AddBookmarkButton.IsEnabled = _pdf != null;

        foreach (var b in _bookmarks.Where(b => b.Page < pages).OrderBy(b => b.Page))
        {
            var bookmark = b;
            var row = Row("", string.IsNullOrWhiteSpace(b.Title) ? $"Page {b.Page + 1}" : b.Title, b.Page, () => Viewer.JumpToPage(bookmark.Page));
            row.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { RenameBookmark(bookmark); e.Handled = true; } };
            var menu = new ContextMenu();
            menu.Items.Add(MenuEntry("Rename...", () => RenameBookmark(bookmark)));
            menu.Items.Add(MenuEntry("Delete", () => DeleteBookmark(bookmark)));
            row.ContextMenu = menu;
            row.ToolTip = "Click to go there; double-click to rename; right-click for more";
            BookmarkItems.Children.Add(row);
        }
        BookmarksEmpty.Visibility = BookmarkItems.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var items = Viewer.Annotations.Items.Where(a => a.Page < pages).OrderBy(a => a.Page).ThenBy(a => a.Y).ThenBy(a => a.X).ToList();
        foreach (var a in items)
        {
            var annotation = a;
            var (glyph, text) = Describe(a);
            var row = Row(glyph, text, a.Page, () => Viewer.RevealAnnotation(annotation));
            var menu = new ContextMenu();
            menu.Items.Add(MenuEntry("Go there", () => Viewer.RevealAnnotation(annotation)));
            menu.Items.Add(MenuEntry("Delete", () => Viewer.Annotations.Remove(annotation)));
            row.ContextMenu = menu;
            AnnotationItems.Children.Add(row);
        }
        AnnotationsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = ReadMarkupTextsAsync(items.OfType<MarkupAnnotation>().Where(m => !_markupTexts.ContainsKey(m.Id)).ToList());
    }

    (string Glyph, string Text) Describe(Annotation a) => a switch
    {
        TextAnnotation t => ("", FirstLine(t.Text) is { Length: > 0 } line ? line : "(empty text box)"),
        ImageAnnotation => ("", "Image"),
        MarkupAnnotation m => (m.Kind switch { MarkupKind.Underline => "", MarkupKind.Strikeout => "", _ => "" },
            _markupTexts.TryGetValue(m.Id, out var words) && words.Length > 0 ? "“" + words + "”"
            : m.Kind switch { MarkupKind.Underline => "Underline", MarkupKind.Strikeout => "Strike-through", _ => "Highlight" }),
        _ => ("", "Annotation"),
    };

    static string FirstLine(string text)
    {
        var line = text.Replace("\r", "").Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 80 ? line[..80] + "…" : line;
    }

    /// <summary>Reads the words under the markups (one page at a time, in the background) and shows them instead of "Highlight".</summary>
    async Task ReadMarkupTextsAsync(List<MarkupAnnotation> markups)
    {
        if (markups.Count == 0 || _pdf is not { } pdf) return;
        bool any = false;
        foreach (var group in markups.GroupBy(m => m.Page))
        {
            var text = await Viewer.GetPageTextAsync(group.Key);
            if (_pdf != pdf) return;
            if (text?.Boxes is not { } boxes) continue;
            foreach (var m in group)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < boxes.Length && i < text.Text.Length; i++)
                {
                    var box = boxes[i];
                    if (box.IsEmpty) { if (sb.Length > 0 && char.IsWhiteSpace(text.Text[i])) sb.Append(' '); continue; }
                    if (m.Rects.Any(r => r.Contains(box.CenterX, box.CenterY))) sb.Append(char.IsControl(text.Text[i]) ? ' ' : text.Text[i]);
                }
                string words = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
                if (words.Length > 80) words = words[..80] + "…";
                _markupTexts[m.Id] = words;
                any = true;
            }
        }
        if (any && MarksActive && _pdf == pdf) RefreshMarks();
    }

    /// <summary>One line of the list: an icon, the text (trimmed), the page number on the right; a click runs <paramref name="open"/>.</summary>
    Border Row(string glyph, string text, int page, Action open)
    {
        var dock = new DockPanel();
        var number = new TextBlock { Text = (page + 1).ToString(CultureInfo.CurrentCulture), Foreground = (Brush)FindResource("SubtleTextBrush"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(number, Dock.Right);
        dock.Children.Add(number);
        var icon = new TextBlock { Text = glyph, FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("SubtleTextBrush") };
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

        var row = new Border { Child = dock, Padding = new Thickness(6, 5, 6, 5), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        row.MouseEnter += (_, _) => row.Background = (Brush)FindResource("BarBorderBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) => { if (e.ClickCount == 1) { open(); Viewer.Focus(); } };
        return row;
    }

    MenuItem MenuEntry(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    void OnAddBookmark(object sender, RoutedEventArgs e) => AddBookmark();

    /// <summary>Bookmarks the page being read (a page already marked is just shown in the list).</summary>
    void AddBookmark()
    {
        if (_pdf == null) return;
        int page = Viewer.CurrentPage;
        if (!_bookmarks.Any(b => b.Page == page))
        {
            _bookmarks.Add(new Bookmark(page, $"Page {page + 1}"));
            MarkKeepChanged();
            StatusLeft.Text = $"Page {page + 1} bookmarked: rename it in the Marks tab of the side panel (F4).";
        }
        if (!_settings.SidebarVisible) SetSidebar(true);
        MarksTab.IsChecked = true;
        RefreshMarks();
    }

    void RenameBookmark(Bookmark b)
    {
        string? name = InputDialog.Ask(this, "Rename bookmark", $"Name of the bookmark on page {b.Page + 1}:", b.Title, "Rename");
        if (name == null) return;
        int i = _bookmarks.IndexOf(b);
        if (i < 0) return;
        _bookmarks[i] = b with { Title = name.Length == 0 ? $"Page {b.Page + 1}" : name };
        MarkKeepChanged();
        RefreshMarks();
    }

    void DeleteBookmark(Bookmark b)
    {
        if (!_bookmarks.Remove(b)) return;
        MarkKeepChanged();
        RefreshMarks();
    }
}
