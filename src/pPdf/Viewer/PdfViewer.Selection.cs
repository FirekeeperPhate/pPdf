using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using pPdf.Pdf;

namespace pPdf.Viewer;

public enum ViewerTool { Select, Hand, Text }

/// <summary>Text selection, copy, search highlights, links and the mouse / keyboard handling of the page area.</summary>
public sealed partial class PdfViewer
{
    sealed record PageData(PageText Text, List<PageLink> Links);

    readonly record struct TextPos(int Page, int Caret) : IComparable<TextPos>
    {
        public int CompareTo(TextPos o) => Page != o.Page ? Page.CompareTo(o.Page) : Caret.CompareTo(o.Caret);
    }

    readonly Dictionary<int, PageData> _texts = [];
    readonly HashSet<int> _textLoading = [];
    CancellationTokenSource _workCts = new();

    TextPos? _selAnchor, _selFocus;
    bool _selecting;
    bool _panning;
    Point _panStart;
    Vector _panOffset;
    PageLink? _pendingLink;
    Point _downPoint;
    DispatcherTimer _autoScroll = null!;

    ViewerTool _tool = ViewerTool.Select;

    static readonly Brush SelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x70, 0x2F, 0x80, 0xED)));
    static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xD5, 0x00)));
    static readonly Brush CurrentHitBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB4, 0xFF, 0x8C, 0x00)));

    static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    public event EventHandler? SelectionChanged;
    public event EventHandler? ToolChanged;

    public ViewerTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            _tool = value;
            UpdateCursor(null);
            ToolChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    void InitSelection()
    {
        _canvas.MouseLeftButtonDown += OnCanvasLeftDown;
        _canvas.MouseMove += OnCanvasMouseMove;
        _canvas.MouseLeftButtonUp += OnCanvasLeftUp;
        _canvas.MouseDown += OnCanvasOtherDown;
        _canvas.MouseUp += OnCanvasOtherUp;
        _canvas.LostMouseCapture += (_, _) => { _selecting = false; _panning = false; _autoScroll?.Stop(); };
        _autoScroll = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(35) };
        _autoScroll.Tick += OnAutoScroll;
    }

    void CancelDocumentWork()
    {
        _workCts.Cancel();
        _workCts = new CancellationTokenSource();
        _textLoading.Clear();
    }

    // ------------------------------------------------------------------ page text

    PageData? DataFor(int page) => _texts.TryGetValue(page, out var d) ? d : null;

    void OnSlotCreated(PageSlot slot)
    {
        PopulateAnnotations(slot);
        if (_texts.ContainsKey(slot.PageIndex)) RefreshOverlays(slot);
        else EnsureText(slot.PageIndex);
    }

    void EnsureText(int page)
    {
        if (_pdf == null || _texts.ContainsKey(page) || !_textLoading.Add(page)) return;
        _ = LoadTextAsync(_pdf, page, _workCts.Token);
    }

    async Task LoadTextAsync(PdfFile pdf, int page, CancellationToken ct)
    {
        try
        {
            var data = await Task.Run(() =>
            {
                if (ct.IsCancellationRequested) return null;
                var text = pdf.LoadText(page, true);
                return text == null ? null : new PageData(text, pdf.LoadLinks(page));
            }, ct);
            if (data == null || ct.IsCancellationRequested || _pdf != pdf) return;
            _textLoading.Remove(page);
            _texts[page] = data;
            TrimTextCache();
            if (_slots.TryGetValue(page, out var slot)) RefreshOverlays(slot);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine("text load failed: " + ex.Message);
        }
    }

    void TrimTextCache()
    {
        const int Max = 80;
        if (_texts.Count <= Max) return;
        foreach (int p in _texts.Keys.Where(p => !_slots.ContainsKey(p)).OrderByDescending(p => Math.Abs(p - _currentPage)).Take(_texts.Count - Max).ToArray())
            _texts.Remove(p);
    }

    /// <summary>Fetches the text of a page and waits for it (used for jumping to a search hit).</summary>
    public async Task<PageText?> GetPageTextAsync(int page)
    {
        if (_pdf == null) return null;
        if (_texts.TryGetValue(page, out var d)) return d.Text;
        EnsureText(page);
        for (int i = 0; i < 400 && _pdf != null; i++)
        {
            if (_texts.TryGetValue(page, out d)) return d.Text;
            await Task.Delay(15);
        }
        return null;
    }

    // ------------------------------------------------------------------ overlays

    void RefreshOverlays(PageSlot slot)
    {
        RefreshSelection(slot);
        RefreshSearch(slot);
    }

    (TextPos Lo, TextPos Hi)? SelectionRange()
    {
        if (_selAnchor is not { } a || _selFocus is not { } f) return null;
        return a.CompareTo(f) <= 0 ? (a, f) : (f, a);
    }

    public bool HasSelection => SelectionRange() is { } r && (r.Lo.Page != r.Hi.Page || r.Lo.Caret != r.Hi.Caret);

    void RefreshSelection(PageSlot slot)
    {
        var data = DataFor(slot.PageIndex);
        if (data == null || SelectionRange() is not { } range || slot.PageIndex < range.Lo.Page || slot.PageIndex > range.Hi.Page)
        {
            if (!slot.SelectionLayer.IsEmptyLayer) slot.SelectionLayer.Set([], SelectionBrush);
            return;
        }
        int len = data.Text.Text.Length;
        int s = slot.PageIndex == range.Lo.Page ? Math.Min(range.Lo.Caret, len) : 0;
        int e = slot.PageIndex == range.Hi.Page ? Math.Min(range.Hi.Caret, len) : len;
        slot.SelectionLayer.Set(e > s ? data.Text.RectsForRange(s, e) : [], SelectionBrush);
    }

    void RefreshSelectionAll()
    {
        foreach (var slot in _slots.Values) RefreshSelection(slot);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection()
    {
        if (_selAnchor == null && _selFocus == null) return;
        _selAnchor = _selFocus = null;
        RefreshSelectionAll();
    }

    public void SelectAll()
    {
        if (_pdf == null || _pdf.PageCount == 0) return;
        _selAnchor = new TextPos(0, 0);
        _selFocus = new TextPos(_pdf.PageCount - 1, int.MaxValue);
        foreach (var slot in _slots.Values) EnsureText(slot.PageIndex);
        RefreshSelectionAll();
    }

    /// <summary>The selected text, pages separated by blank lines.</summary>
    public string GetSelectedText()
    {
        if (_pdf == null || SelectionRange() is not { } range) return "";
        var sb = new StringBuilder();
        for (int p = range.Lo.Page; p <= range.Hi.Page; p++)
        {
            var text = DataFor(p)?.Text ?? _pdf.LoadText(p, false);
            if (text == null) continue;
            int len = text.Text.Length;
            int s = p == range.Lo.Page ? Math.Min(range.Lo.Caret, len) : 0;
            int e = p == range.Hi.Page ? Math.Min(range.Hi.Caret, len) : len;
            if (e <= s) continue;
            if (sb.Length > 0) sb.Append("\r\n\r\n");
            sb.Append(text.Slice(s, e));
        }
        return sb.ToString();
    }

    public bool CopySelection()
    {
        string text = GetSelectedText();
        if (text.Length == 0) return false;
        try { Clipboard.SetDataObject(text, true); return true; }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    // ------------------------------------------------------------------ search highlights

    Dictionary<int, List<TextMatch>> _hits = [];
    TextMatch? _currentHit;

    public void SetSearchHighlights(IEnumerable<TextMatch> matches, TextMatch? current)
    {
        _hits = matches.GroupBy(m => m.Page).ToDictionary(g => g.Key, g => g.ToList());
        _currentHit = current;
        foreach (var slot in _slots.Values) RefreshSearch(slot);
    }

    public void ClearSearchHighlights()
    {
        if (_hits.Count == 0 && _currentHit == null) return;
        _hits = [];
        _currentHit = null;
        foreach (var slot in _slots.Values) RefreshSearch(slot);
    }

    void RefreshSearch(PageSlot slot)
    {
        if (!_hits.TryGetValue(slot.PageIndex, out var hits) || DataFor(slot.PageIndex) is not { } data)
        {
            if (!slot.SearchLayer.IsEmptyLayer) slot.SearchLayer.Set([], HitBrush);
            return;
        }
        var all = new List<RectF>();
        var current = new List<RectF>();
        foreach (var h in hits)
        {
            var rects = data.Text.RectsForRange(h.Start, h.End);
            if (_currentHit is { } c && c == h) current.AddRange(rects); else all.AddRange(rects);
        }
        slot.SearchLayer.Set(all, HitBrush, current, CurrentHitBrush);
    }

    /// <summary>Brings a search hit into view (switching page in the one-page layouts).</summary>
    public async Task ShowMatchAsync(TextMatch match)
    {
        if (_pdf == null) return;
        var data = await GetPageTextAsync(match.Page);
        GoToPageIfNotVisible(match.Page);
        if (data == null || _result == null || _pdf == null) return;
        var rects = data.RectsForRange(match.Start, match.End);
        if (rects.Count == 0) return;
        // the slot may have been created by GoToPage just now
        UpdateVisible();
        if (!_slots.TryGetValue(match.Page, out var slot)) return;
        var r = rects[0].ToRect();
        var inSlot = slot.ToSlotRect(r);
        var pageRect = _result.PageRects[match.Page];
        EnsureVisible(new Rect(pageRect.X + inSlot.X, pageRect.Y + inSlot.Y, inSlot.Width, inSlot.Height));
    }

    void GoToPageIfNotVisible(int page)
    {
        if (_result == null) return;
        var view = new Rect(_scroll.HorizontalOffset, _scroll.VerticalOffset, _scroll.ViewportWidth, _scroll.ViewportHeight);
        if (Rect.Intersect(view, _result.PageRects[page]).IsEmpty) GoToPage(page);
    }

    // ------------------------------------------------------------------ hit testing

    bool TryHit(Point canvasPoint, out PageSlot slot, out Point pagePoint)
    {
        foreach (var s in _slots.Values)
        {
            var r = _result?.PageRects[s.PageIndex] ?? Rect.Empty;
            if (!r.IsEmpty && r.Contains(canvasPoint))
            {
                slot = s;
                pagePoint = s.ToPagePoint(new Point(canvasPoint.X - r.X, canvasPoint.Y - r.Y));
                return true;
            }
        }
        slot = null!;
        pagePoint = default;
        return false;
    }

    bool TryNearest(Point canvasPoint, out PageSlot slot, out Point pagePoint)
    {
        slot = null!;
        pagePoint = default;
        double best = double.MaxValue;
        foreach (var s in _slots.Values)
        {
            var r = _result?.PageRects[s.PageIndex] ?? Rect.Empty;
            if (r.IsEmpty) continue;
            double dx = canvasPoint.X < r.Left ? r.Left - canvasPoint.X : canvasPoint.X > r.Right ? canvasPoint.X - r.Right : 0;
            double dy = canvasPoint.Y < r.Top ? r.Top - canvasPoint.Y : canvasPoint.Y > r.Bottom ? canvasPoint.Y - r.Bottom : 0;
            // reading order matters more vertically
            double d = dx * dx + 4 * dy * dy;
            if (d < best) { best = d; slot = s; }
        }
        if (slot == null) return false;
        var rr = _result!.PageRects[slot.PageIndex];
        pagePoint = slot.ToPagePoint(new Point(canvasPoint.X - rr.X, canvasPoint.Y - rr.Y));
        return true;
    }

    PageLink? LinkAt(PageSlot slot, Point pagePoint)
    {
        if (DataFor(slot.PageIndex) is not { } d) return null;
        foreach (var l in d.Links)
            if (l.Box.Contains(pagePoint.X, pagePoint.Y)) return l;
        return null;
    }

    // ------------------------------------------------------------------ mouse

    void OnCanvasLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (_pdf == null) return;
        // a click anywhere else ends an annotation text edit (the editor loses keyboard focus)
        Focus();
        Keyboard.Focus(this);
        SelectedAnnotation = null;

        var pt = e.GetPosition(_canvas);
        if (Tool == ViewerTool.Hand) { StartPan(e); return; }
        if (Tool == ViewerTool.Text)
        {
            if (TryHit(pt, out var tslot, out var tp)) BeginNewText(tslot, tp);
            e.Handled = true;
            return;
        }

        _downPoint = pt;
        _pendingLink = null;
        if (!TryHit(pt, out var slot, out var pp)) { ClearSelection(); return; }

        if (LinkAt(slot, pp) is { } link) { _pendingLink = link; e.Handled = true; _canvas.CaptureMouse(); return; }

        var data = DataFor(slot.PageIndex);
        if (data == null) { EnsureText(slot.PageIndex); ClearSelection(); return; }
        int caret = data.Text.CaretAt(pp.X, pp.Y);
        if (caret < 0) { ClearSelection(); return; }

        if (e.ClickCount >= 3)
        {
            var (s, en) = data.Text.LineAt(caret);
            _selAnchor = new TextPos(slot.PageIndex, s);
            _selFocus = new TextPos(slot.PageIndex, en);
        }
        else if (e.ClickCount == 2)
        {
            var (s, en) = data.Text.WordAt(Math.Min(caret, Math.Max(0, data.Text.Text.Length - 1)));
            _selAnchor = new TextPos(slot.PageIndex, s);
            _selFocus = new TextPos(slot.PageIndex, en);
        }
        else
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0 || _selAnchor == null) _selAnchor = new TextPos(slot.PageIndex, caret);
            _selFocus = new TextPos(slot.PageIndex, caret);
        }
        _selecting = true;
        _canvas.CaptureMouse();
        _autoScroll.Start();
        RefreshSelectionAll();
        e.Handled = true;
    }

    void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_pdf == null) return;
        if (_panning)
        {
            var now = e.GetPosition(_scroll);
            var delta = now - _panStart;
            _scroll.ScrollToHorizontalOffset(_panOffset.X - delta.X);
            _scroll.ScrollToVerticalOffset(_panOffset.Y - delta.Y);
            return;
        }
        if (_pendingLink != null)
        {
            // a drag that started on a link turns into a plain text selection
            if ((e.GetPosition(_canvas) - _downPoint).Length > 4) { _pendingLink = null; StartSelectionAt(_downPoint); }
            return;
        }
        if (_selecting) { UpdateSelectionFocus(e.GetPosition(_canvas)); return; }
        UpdateCursor(e.GetPosition(_canvas));
    }

    void StartSelectionAt(Point canvasPt)
    {
        if (!TryHit(canvasPt, out var slot, out var pp) || DataFor(slot.PageIndex) is not { } data) return;
        int caret = data.Text.CaretAt(pp.X, pp.Y);
        if (caret < 0) return;
        _selAnchor = new TextPos(slot.PageIndex, caret);
        _selFocus = _selAnchor;
        _selecting = true;
        _autoScroll.Start();
    }

    void UpdateSelectionFocus(Point canvasPt)
    {
        if (!TryNearest(canvasPt, out var slot, out var pp)) return;
        var data = DataFor(slot.PageIndex);
        if (data == null) { EnsureText(slot.PageIndex); return; }
        int caret = data.Text.CaretAt(pp.X, pp.Y);
        if (caret < 0)
        {
            // a page without text: extend to its start or end depending on the side of the pointer
            caret = (_selAnchor is { } a && slot.PageIndex < a.Page) ? 0 : data.Text.Text.Length;
        }
        var focus = new TextPos(slot.PageIndex, caret);
        if (_selFocus == focus) return;
        _selFocus = focus;
        RefreshSelectionAll();
    }

    void OnCanvasLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning) { EndPan(); return; }
        if (_pendingLink is { } link)
        {
            _pendingLink = null;
            _canvas.ReleaseMouseCapture();
            FollowLink(link);
            return;
        }
        if (_selecting)
        {
            _selecting = false;
            _autoScroll.Stop();
            _canvas.ReleaseMouseCapture();
            // a plain click leaves no selection behind
            if (!HasSelection) ClearSelection();
        }
    }

    void OnCanvasOtherDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _pdf != null) { StartPan(e); e.Handled = true; }
    }

    void OnCanvasOtherUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _panning) EndPan();
    }

    void StartPan(MouseButtonEventArgs e)
    {
        _panning = true;
        _panStart = e.GetPosition(_scroll);
        _panOffset = new Vector(_scroll.HorizontalOffset, _scroll.VerticalOffset);
        _canvas.CaptureMouse();
        Cursor = Cursors.ScrollAll;
        _canvas.Cursor = Cursors.ScrollAll;
        e.Handled = true;
    }

    void EndPan()
    {
        _panning = false;
        _canvas.ReleaseMouseCapture();
        UpdateCursor(null);
    }

    void OnAutoScroll(object? sender, EventArgs e)
    {
        if (!_selecting) { _autoScroll.Stop(); return; }
        var p = Mouse.GetPosition(_scroll);
        double dy = p.Y < 0 ? p.Y : p.Y > _scroll.ViewportHeight ? p.Y - _scroll.ViewportHeight : 0;
        double dx = p.X < 0 ? p.X : p.X > _scroll.ViewportWidth ? p.X - _scroll.ViewportWidth : 0;
        if (dx == 0 && dy == 0) return;
        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + Math.Clamp(dy / 3, -60, 60));
        _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + Math.Clamp(dx / 3, -60, 60));
        UpdateSelectionFocus(Mouse.GetPosition(_canvas));
    }

    void UpdateCursor(Point? canvasPt)
    {
        if (_tool == ViewerTool.Hand) { _canvas.Cursor = Cursors.Hand; return; }
        if (_tool == ViewerTool.Text) { _canvas.Cursor = Cursors.IBeam; return; }
        if (canvasPt is { } p && TryHit(p, out var slot, out var pp))
        {
            if (LinkAt(slot, pp) != null) { _canvas.Cursor = Cursors.Hand; return; }
            if (DataFor(slot.PageIndex)?.Text.IsOverText(pp.X, pp.Y) == true) { _canvas.Cursor = Cursors.IBeam; return; }
        }
        _canvas.Cursor = Cursors.Arrow;
    }

    void FollowLink(PageLink link)
    {
        if (link.PageIndex >= 0) { GoToPage(link.PageIndex); return; }
        if (link.Uri is not { } uri) return;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return;
        // only ever hand the shell something that cannot run code
        if (u.Scheme is not ("http" or "https" or "mailto")) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine("link failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_pdf == null || e.Handled) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.C when ctrl:
                if (CopySelection()) e.Handled = true;
                return;
            case Key.A when ctrl:
                SelectAll();
                e.Handled = true;
                return;
            case Key.Escape:
                if (SelectedAnnotation != null) SelectedAnnotation = null;
                else if (Tool != ViewerTool.Select) Tool = ViewerTool.Select;
                else ClearSelection();
                e.Handled = true;
                return;
        }

        if (SelectedAnnotation != null && HandleAnnotationKey(e, shift)) { e.Handled = true; return; }

        double line = 48;
        switch (e.Key)
        {
            case Key.Down:
                ScrollBy(line); break;
            case Key.Up:
                ScrollBy(-line); break;
            case Key.Left:
                if (_scroll.ScrollableWidth > 1) _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - line); else PreviousPage();
                break;
            case Key.Right:
                if (_scroll.ScrollableWidth > 1) _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + line); else NextPage();
                break;
            case Key.PageDown:
            case Key.Space when !shift:
                ScrollScreen(+1); break;
            case Key.PageUp:
            case Key.Space when shift:
                ScrollScreen(-1); break;
            case Key.Home:
                GoToPage(0); break;
            case Key.End:
                GoToPage(PageCount - 1); break;
            default:
                return;
        }
        e.Handled = true;
    }

    void ScrollScreen(int direction)
    {
        ScrollBy(direction * _scroll.ViewportHeight * 0.9);
    }
}
