using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using pPdf.Annotations;
using pPdf.Pdf;
using pPdf.Services;

namespace pPdf.Viewer;

/// <summary>
/// The document view: lays pages out (continuous / single / two pages), zooms, rotates, and only keeps the pages near the
/// viewport alive. Text selection, search highlights and annotations live in the partial class files next to this one.
/// </summary>
public sealed partial class PdfViewer : Grid
{
    /// <summary>The paper of a page while the colors are inverted: the same soft dark gray the night pixels use for white.</summary>
    static readonly Brush NightPaper = Freeze(new SolidColorBrush(Color.FromRgb(24, 24, 24)));
    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    const double PageMargin = 14;
    const double PageGap = 10;
    const double RowGap = 12;
    const long MaxFullPagePixels = 16_000_000;

    static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6, 8];

    readonly ScrollViewer _scroll;
    readonly Canvas _canvas;
    readonly Dictionary<int, PageSlot> _slots = [];
    readonly DispatcherTimer _settleTimer;
    readonly DispatcherTimer _snapTimer;

    readonly RenderService _renderer = RenderService.Shared;
    PdfFile? _pdf;

    ViewLayout _layout = ViewLayout.Continuous;
    ZoomMode _zoomMode = ZoomMode.FitWidth;
    double _zoom = 1;
    int _rotation;
    bool _coverAlone;
    bool _invert;

    List<int[]> _rows = [];
    LayoutResult? _result;
    int _currentPage;
    bool _inLayout;
    DocPosition? _pendingPosition;
    DateTime _lastFlip = DateTime.MinValue;

    public PdfViewer()
    {
        _canvas = new Canvas { Background = Brushes.Transparent, SnapsToDevicePixels = true };
        _scroll = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            CanContentScroll = false,
            PanningMode = PanningMode.Both,
        };
        Children.Add(_scroll);
        Focusable = true;
        FocusVisualStyle = null;
        AllowDrop = true;
        ClipToBounds = true;

        _scroll.ScrollChanged += OnScrollChanged;
        _scroll.PreviewMouseWheel += OnPreviewMouseWheel;
        SizeChanged += (_, _) => { if (_pdf != null) ApplyLayout(); };
        // a short pause after the last layout change: render with the final geometry, and prefetch
        _settleTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _settleTimer.Tick += (_, _) => { _settleTimer.Stop(); UpdateVisible(); };
        _snapTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _snapTimer.Tick += (_, _) => { _snapTimer.Stop(); SnapToRow(); };

        InitSelection();
        InitAnnotations();
    }

    // ------------------------------------------------------------------ state

    public PdfFile? Document => _pdf;
    public RenderService Renderer => _renderer;
    public int PageCount => _pdf?.PageCount ?? 0;
    public int CurrentPage => _currentPage;

    public ViewLayout Layout
    {
        get => _layout;
        set
        {
            if (_layout == value) return;
            int page = _currentPage;
            _layout = value;
            _rows = BuildRows();
            ApplyLayout(keepAnchor: false);
            GoToPage(page);
            RaiseStateChanged();
        }
    }

    public ZoomMode ZoomMode => _zoomMode;

    /// <summary>Zoom factor actually in use (1.0 = 100 %), also for the fit modes.</summary>
    public double EffectiveZoom => (_result?.Scale ?? PageLayoutEngine.DipsPerPoint) / PageLayoutEngine.DipsPerPoint;

    public int Rotation
    {
        get => _rotation;
        set
        {
            value = ((value % 4) + 4) % 4;
            if (value == _rotation) return;
            _rotation = value;
            foreach (var s in _slots.Values) { s.CancelRender(); s.ClearBitmap(); }
            ApplyLayout();
            RaiseStateChanged();
        }
    }

    public bool CoverAlone
    {
        get => _coverAlone;
        set
        {
            if (_coverAlone == value) return;
            int page = _currentPage;
            _coverAlone = value;
            _rows = BuildRows();
            ApplyLayout(keepAnchor: false);
            GoToPage(page);
        }
    }

    public bool InvertColors
    {
        get => _invert;
        set
        {
            if (_invert == value) return;
            _invert = value;
            foreach (var s in _slots.Values) s.CancelRender();
            ApplyPaperBrush();
            UpdateVisible();
        }
    }

    public event EventHandler? StateChanged;
    /// <summary>The view was scrolled (also within a page, which <see cref="StateChanged"/> does not report).</summary>
    public event EventHandler? ViewMoved;
    void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    List<int[]> BuildRows() => _pdf == null ? [] : PageLayoutEngine.BuildRows(_pdf.PageCount, PageLayoutEngine.PagesPerRow(_layout), _coverAlone);

    double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    void ApplyPaperBrush()
    {
        var brush = _invert ? NightPaper : Brushes.White;
        foreach (var s in _slots.Values) s.SetPaper(brush);
    }

    // ------------------------------------------------------------------ opening / closing

    /// <summary>Opens a document, at the position it was left in last time if that is known.</summary>
    public void Open(PdfFile pdf, DocPosition? start = null)
    {
        Close();
        _pdf = pdf;
        _rows = BuildRows();
        _currentPage = Math.Clamp(start?.Page ?? 0, 0, Math.Max(0, pdf.PageCount - 1));
        _rotation = (start?.Rotation ?? 0) & 3;
        if (start?.ZoomMode is { } mode)
        {
            _zoomMode = mode;
            if (mode == ZoomMode.Custom) _zoom = Math.Clamp(start.Zoom, PageLayoutEngine.MinZoom, PageLayoutEngine.MaxZoom);
        }
        _pendingPosition = start ?? new DocPosition { Page = _currentPage };
        ApplyLayout(keepAnchor: false); // when the window has no size yet this waits, and the position is applied by the first layout
        RaiseStateChanged();
        Focus();
    }

    /// <summary>Where the document is now: the page at the top of the window, how far down it, and how it is viewed.</summary>
    public DocPosition? CapturePosition()
    {
        if (_pdf == null || _result == null || _rows.Count == 0) return null;
        double v = _scroll.VerticalOffset;
        // the last row whose top is at or above the top of the window
        int lo = 0, hi = _rows.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (RowTopOffset(mid) <= v + 1) lo = mid; else hi = mid - 1;
        }
        double height = Math.Max(1, _result.RowRects[lo].Height);
        return new DocPosition
        {
            Page = _rows[lo][0],
            OffsetY = Math.Clamp((v - RowTopOffset(lo)) / height, 0, 1),
            ZoomMode = _zoomMode,
            Zoom = EffectiveZoom,
            Rotation = _rotation,
        };
    }

    /// <summary>The vertical offset that puts a row at the top of the window (the same one <see cref="GoToPage"/> scrolls to).</summary>
    double RowTopOffset(int row) => IsPaged ? _result!.RowSlots[row].Top : Math.Max(0, _result!.RowRects[row].Top - PageMargin);

    void ApplyPendingPosition()
    {
        if (_pendingPosition is not { } p || _result == null || _rows.Count == 0) return;
        _pendingPosition = null;
        MoveTo(p);
    }

    /// <summary>Scrolls to a saved position: its page, and how far down that page.</summary>
    void MoveTo(DocPosition p)
    {
        if (_pdf == null || _result == null || _rows.Count == 0) return;
        int page = Math.Clamp(p.Page, 0, _pdf.PageCount - 1);
        GoToPage(page);
        if (p.OffsetY <= 0) return;
        int row = PageLayoutEngine.RowOfPage(_rows, page);
        double target = RowTopOffset(row) + p.OffsetY * _result.RowRects[row].Height;
        if (IsPaged)
        {
            var (min, max) = RowScrollRange(row);
            target = Math.Clamp(target, min, max);
        }
        _scroll.ScrollToVerticalOffset(Math.Max(0, target));
        UpdateVisible();
    }

    // ------------------------------------------------------------------ back / forward

    readonly Stack<DocPosition> _back = new();
    readonly Stack<DocPosition> _forward = new();

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    /// <summary>The list of places to go back / forward to changed.</summary>
    public event EventHandler? HistoryChanged;

    /// <summary>
    /// A jump (a link, the outline, "go to page", a search hit...) as opposed to reading on: the place left is remembered,
    /// so <see cref="GoBack"/> returns to it. Moving to a neighbouring page is just reading, and is not remembered.
    /// </summary>
    public void JumpToPage(int page)
    {
        if (_pdf == null) return;
        page = Math.Clamp(page, 0, _pdf.PageCount - 1);
        if (Math.Abs(page - _currentPage) > 1 && CapturePosition() is { } here)
        {
            _back.Push(here);
            _forward.Clear();
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        GoToPage(page);
    }

    public void GoBack() => Travel(_back, _forward);
    public void GoForward() => Travel(_forward, _back);

    void Travel(Stack<DocPosition> from, Stack<DocPosition> to)
    {
        if (from.Count == 0 || CapturePosition() is not { } here) return;
        var target = from.Pop();
        to.Push(here);
        MoveTo(target);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    void ClearHistory()
    {
        if (_back.Count == 0 && _forward.Count == 0) return;
        _back.Clear();
        _forward.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Lets go of every rendered bitmap and of the text of pages out of view (the window is minimized: nobody is looking).
    /// <see cref="ResumeAfterTrim"/> brings the visible pages back.
    /// </summary>
    public void TrimMemory()
    {
        foreach (var s in _slots.Values) { s.CancelRender(); s.ClearBitmap(); }
        foreach (int p in _texts.Keys.Where(p => !_slots.ContainsKey(p)).ToArray()) _texts.Remove(p);
        _renderer.Clear();
    }

    public void ResumeAfterTrim() => UpdateVisible();

    public void Close()
    {
        CancelDocumentWork();
        foreach (var s in _slots.Values) s.CancelRender();
        _slots.Clear();
        _canvas.Children.Clear();
        _canvas.Width = _canvas.Height = 0;
        if (_pdf != null) _renderer.Forget(_pdf);
        _pdf = null;
        _result = null;
        _rows = [];
        _pendingPosition = null;
        SetForm(null);
        StopSmoothing();
        ClearHistory();
        _currentPage = 0;
        ClearSelection();
        ClearSearchHighlights();
        _texts.Clear();
        Annotations.Clear();
        SelectedAnnotation = null;
        RaiseStateChanged();
    }

    // ------------------------------------------------------------------ layout

    /// <summary>The document point that must stay where it is on screen while the layout changes (zoom, resize, rotate).</summary>
    readonly record struct Anchor(int Page, double FX, double FY, Point ViewportPoint);

    Anchor? CaptureAnchor(Point? viewportPoint)
    {
        if (_result == null || _slots.Count == 0) return null;
        var vp = viewportPoint ?? new Point(_scroll.ViewportWidth / 2, _scroll.ViewportHeight / 2);
        var canvasPt = new Point(_scroll.HorizontalOffset + vp.X, _scroll.VerticalOffset + vp.Y);
        int best = -1;
        double bestD = double.MaxValue;
        foreach (var (page, _) in _slots)
        {
            var r = _result.PageRects[page];
            if (r.IsEmpty) continue;
            double dx = canvasPt.X < r.Left ? r.Left - canvasPt.X : canvasPt.X > r.Right ? canvasPt.X - r.Right : 0;
            double dy = canvasPt.Y < r.Top ? r.Top - canvasPt.Y : canvasPt.Y > r.Bottom ? canvasPt.Y - r.Bottom : 0;
            double d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = page; }
        }
        if (best < 0) return null;
        var rect = _result.PageRects[best];
        return new Anchor(best, (canvasPt.X - rect.X) / rect.Width, (canvasPt.Y - rect.Y) / rect.Height, vp);
    }

    void ApplyLayout(bool keepAnchor = true, Point? anchorViewportPoint = null)
    {
        if (_pdf == null || _inLayout) return;
        if (ActualWidth < 2 || ActualHeight < 2) return;
        _inLayout = true;
        try
        {
            var anchor = keepAnchor ? CaptureAnchor(anchorViewportPoint) : null;
            var viewport = new Size(_scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth,
                                    _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : ActualHeight);
            _result = PageLayoutEngine.Compute(_pdf.Pages, _rows, _rotation, PageLayoutEngine.IsPaged(_layout),
                _zoomMode, _zoom, viewport, PageMargin, PageGap, RowGap, DpiScale);

            _canvas.Width = _result.ExtentWidth;
            _canvas.Height = _result.ExtentHeight;
            foreach (var (page, slot) in _slots.ToArray())
            {
                var r = _result.PageRects[page];
                if (r.IsEmpty) RemoveSlot(page);
                else slot.SetGeometry(r, _result.Scale, _rotation);
            }
            _scroll.UpdateLayout();

            if (anchor is { } a && !_result.PageRects[a.Page].IsEmpty)
            {
                var r = _result.PageRects[a.Page];
                double x = r.X + a.FX * r.Width - a.ViewportPoint.X;
                double y = r.Y + a.FY * r.Height - a.ViewportPoint.Y;
                _scroll.ScrollToHorizontalOffset(Math.Max(0, x));
                _scroll.ScrollToVerticalOffset(Math.Max(0, y));
            }
        }
        finally { _inLayout = false; }
        UpdateVisible();
        ApplyPendingPosition();
        RaiseStateChanged();
    }

    // ------------------------------------------------------------------ zoom

    public void SetZoom(double zoom, Point? viewportAnchor = null)
    {
        zoom = Math.Clamp(zoom, PageLayoutEngine.MinZoom, PageLayoutEngine.MaxZoom);
        _zoomMode = ZoomMode.Custom;
        _zoom = zoom;
        ApplyLayout(true, viewportAnchor);
    }

    public void SetZoomMode(ZoomMode mode)
    {
        if (mode == ZoomMode.Custom) { SetZoom(EffectiveZoom); return; }
        _zoomMode = mode;
        ApplyLayout(false);
        GoToPage(_currentPage);
    }

    public void ZoomIn(Point? anchor = null)
    {
        double z = EffectiveZoom;
        foreach (double s in ZoomSteps)
            if (s > z * 1.005) { SetZoom(s, anchor); return; }
        SetZoom(PageLayoutEngine.MaxZoom, anchor);
    }

    public void ZoomOut(Point? anchor = null)
    {
        double z = EffectiveZoom;
        for (int i = ZoomSteps.Length - 1; i >= 0; i--)
            if (ZoomSteps[i] < z / 1.005) { SetZoom(ZoomSteps[i], anchor); return; }
        SetZoom(PageLayoutEngine.MinZoom, anchor);
    }

    // ------------------------------------------------------------------ scrolling and navigation

    bool IsPaged => PageLayoutEngine.IsPaged(_layout);

    public void GoToPage(int page, bool animate = false)
    {
        if (_pdf == null || _pdf.PageCount == 0) return;
        page = Math.Clamp(page, 0, _pdf.PageCount - 1);
        if (_result == null) return;
        if (IsPaged)
        {
            // the row's slot fills the window: put its top at the top of the view
            int row = PageLayoutEngine.RowOfPage(_rows, page);
            _scroll.ScrollToVerticalOffset(_result.RowSlots[row].Top);
            _scroll.ScrollToHorizontalOffset(0);
            SetCurrentPage(_rows[row][0]);
            UpdateVisible();
            return;
        }
        var r = _result.PageRects[page];
        _scroll.ScrollToVerticalOffset(Math.Max(0, r.Top - PageMargin));
        if (_scroll.ScrollableWidth > 1) _scroll.ScrollToHorizontalOffset(Math.Max(0, r.Left - PageMargin));
        SetCurrentPage(page);
        UpdateVisible();
    }

    public void NextPage() => Step(+1);
    public void PreviousPage() => Step(-1);

    void Step(int direction)
    {
        if (_pdf == null) return;
        int row = PageLayoutEngine.RowOfPage(_rows, _currentPage) + direction;
        if (row < 0 || row >= _rows.Count) return;
        GoToPage(_rows[row][0]);
    }

    /// <summary>Scrolls so that a rectangle (canvas coordinates) is visible, with some room around it.</summary>
    void EnsureVisible(Rect canvasRect)
    {
        double vx = _scroll.HorizontalOffset, vy = _scroll.VerticalOffset;
        double vw = _scroll.ViewportWidth, vh = _scroll.ViewportHeight;
        double pad = 40;
        if (canvasRect.Top < vy + pad || canvasRect.Bottom > vy + vh - pad)
            _scroll.ScrollToVerticalOffset(Math.Max(0, canvasRect.Top - vh / 3));
        if (canvasRect.Left < vx + pad || canvasRect.Right > vx + vw - pad)
            _scroll.ScrollToHorizontalOffset(Math.Max(0, canvasRect.Left - vw / 3));
    }

    void SetCurrentPage(int page)
    {
        if (page == _currentPage) return;
        _currentPage = page;
        RaiseStateChanged();
    }

    void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_inLayout || _pdf == null) return;
        UpdateVisible();
        ViewMoved?.Invoke(this, EventArgs.Empty);
        if (IsPaged && e.VerticalChange != 0)
        {
            _snapTimer.Stop();
            _snapTimer.Start();
        }
    }

    /// <summary>True when the wheel is over something with a scrolling of its own: an open drop-down list or a long list box of a form.</summary>
    static bool WheelBelongsToControl(object? source)
    {
        var d = source as DependencyObject;
        while (d != null)
        {
            if (d is ComboBox { IsDropDownOpen: true }) return true;
            if (d is ListBox lb && FindScroller(lb) is { ScrollableHeight: > 0 }) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }

    void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_pdf == null) return;
        if (WheelBelongsToControl(e.OriginalSource)) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            var anchor = e.GetPosition(_scroll);
            if (e.Delta > 0) ZoomIn(anchor); else ZoomOut(anchor);
            return;
        }
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && _scroll.ScrollableWidth > 1)
        {
            // Shift + wheel moves sideways when the page is wider than the window
            e.Handled = true;
            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta / 120.0 * 96);
            return;
        }
        if (!IsPaged)
        {
            // continuous views: a glide, and a notch is a bit more than the default 48 px
            e.Handled = true;
            SmoothBy(-e.Delta / 120.0 * 96);
            return;
        }

        e.Handled = true;
        ScrollBy(-e.Delta / 120.0 * 48, throttleFlips: true);
    }


    // ------------------------------------------------------------------ smooth wheel scrolling

    double _smoothTarget, _smoothLast;
    bool _smoothing;

    /// <summary>Scrolls by <paramref name="delta"/> DIPs, easing towards the target over a few frames instead of jumping.</summary>
    void SmoothBy(double delta)
    {
        if (_result == null) return;
        if (!_smoothing)
        {
            _smoothTarget = _smoothLast = _scroll.VerticalOffset;
            _smoothing = true;
            CompositionTarget.Rendering += OnSmoothFrame;
        }
        _smoothTarget = Math.Clamp(_smoothTarget + delta, 0, Math.Max(0, _scroll.ScrollableHeight));
    }

    void OnSmoothFrame(object? sender, EventArgs e)
    {
        double current = _scroll.VerticalOffset;
        // something else moved the view (the scroll bar, a jump, a key): the wheel's glide gives way
        if (Math.Abs(current - _smoothLast) > 1.5) { StopSmoothing(); return; }
        double diff = _smoothTarget - current;
        if (Math.Abs(diff) < 0.5)
        {
            _scroll.ScrollToVerticalOffset(_smoothTarget);
            StopSmoothing();
            return;
        }
        _smoothLast = current + diff * 0.3;
        _scroll.ScrollToVerticalOffset(_smoothLast);
    }

    void StopSmoothing()
    {
        if (!_smoothing) return;
        _smoothing = false;
        CompositionTarget.Rendering -= OnSmoothFrame;
    }

    /// <summary>
    /// Scrolls by <paramref name="delta"/> DIPs (positive = down). In the one-page and two-page views the movement stops at the
    /// end of the current row, and pushing further turns the page (the scroll bar itself spans the whole document).
    /// </summary>
    void ScrollBy(double delta, bool throttleFlips = false)
    {
        if (_result == null) return;
        if (!IsPaged) { StopSmoothing(); _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + delta); return; }

        int row = PageLayoutEngine.RowOfPage(_rows, _currentPage);
        var (min, max) = RowScrollRange(row);
        double offset = _scroll.VerticalOffset;
        if (delta > 0 && offset >= max - 1) { FlipRow(row + 1, toBottom: false, throttleFlips); return; }
        if (delta < 0 && offset <= min + 1) { FlipRow(row - 1, toBottom: true, throttleFlips); return; }
        _scroll.ScrollToVerticalOffset(Math.Clamp(offset + delta, min, max));
    }

    void FlipRow(int row, bool toBottom, bool throttle)
    {
        if (row < 0 || row >= _rows.Count) return;
        // a touchpad's momentum sends dozens of wheel events: one turn per gesture step
        if (throttle && (DateTime.UtcNow - _lastFlip).TotalMilliseconds < 250) return;
        _lastFlip = DateTime.UtcNow;
        GoToPage(_rows[row][0]);
        if (toBottom) _scroll.ScrollToVerticalOffset(RowScrollRange(row).Max);
    }

    /// <summary>The vertical offsets at which the viewport stays inside one row's slot (paged views).</summary>
    (double Min, double Max) RowScrollRange(int row)
    {
        var slot = _result!.RowSlots[row];
        return (slot.Top, Math.Max(slot.Top, slot.Bottom - _scroll.ViewportHeight));
    }

    /// <summary>After the scroll bar was dragged the view may straddle two pages: settle on the one that shows most.</summary>
    void SnapToRow()
    {
        if (_pdf == null || _result == null || !IsPaged) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed) { _snapTimer.Start(); return; } // still dragging
        var (min, max) = RowScrollRange(PageLayoutEngine.RowOfPage(_rows, _currentPage));
        double offset = _scroll.VerticalOffset;
        double target = Math.Clamp(offset, min, max);
        if (Math.Abs(target - offset) > 0.5) _scroll.ScrollToVerticalOffset(target);
    }

    // ------------------------------------------------------------------ virtualisation

    void RemoveSlot(int page)
    {
        if (!_slots.Remove(page, out var slot)) return;
        slot.CancelRender();
        _canvas.Children.Remove(slot);
    }

    void UpdateVisible()
    {
        if (_pdf == null || _result == null || _inLayout) return;
        double dpi = DpiScale;
        var view = new Rect(_scroll.HorizontalOffset, _scroll.VerticalOffset, Math.Max(1, _scroll.ViewportWidth), Math.Max(1, _scroll.ViewportHeight));
        // a little room above and below only: pages further away are not worth a render nobody may ever look at
        var near = Rect.Inflate(view, 0, view.Height * 0.35);

        // pages to keep alive
        var wanted = new List<int>();
        double bestArea = -1;
        int bestPage = _currentPage;
        for (int p = 0; p < _pdf.PageCount; p++)
        {
            var r = _result.PageRects[p];
            if (r.IsEmpty) continue;
            if (r.Top > near.Bottom) break;
            if (r.Bottom < near.Top || r.Right < near.Left || r.Left > near.Right) continue;
            wanted.Add(p);
            var inter = Rect.Intersect(r, view);
            if (!inter.IsEmpty)
            {
                double area = inter.Width * inter.Height;
                if (area > bestArea + 0.5) { bestArea = area; bestPage = p; }
            }
        }

        foreach (int page in _slots.Keys.Except(wanted).ToArray()) RemoveSlot(page);

        foreach (int page in wanted)
        {
            if (!_slots.TryGetValue(page, out var slot))
            {
                slot = new PageSlot(page, _pdf.Pages[page]);
                slot.SetPaper(_invert ? NightPaper : Brushes.White);
                _slots[page] = slot;
                _canvas.Children.Add(slot);
                slot.SetGeometry(_result.PageRects[page], _result.Scale, _rotation);
                OnSlotCreated(slot);
            }
            bool visible = !Rect.Intersect(_result.PageRects[page], view).IsEmpty;
            RequestRender(slot, view, visible ? 0 : 1, dpi);
        }

        // the one-page views name a row by its first page
        SetCurrentPage(IsPaged && _rows.Count > 0 ? _rows[PageLayoutEngine.RowOfPage(_rows, bestPage)][0] : bestPage);
    }

    // ------------------------------------------------------------------ rendering

    void RequestRender(PageSlot slot, Rect view, int priority, double dpi)
    {
        if (_pdf == null || _result == null) return;
        var pageRect = _result.PageRects[slot.PageIndex];
        int fullW = Math.Max(1, (int)Math.Round(pageRect.Width * dpi));
        int fullH = Math.Max(1, (int)Math.Round(pageRect.Height * dpi));

        int x = 0, y = 0, w = fullW, h = fullH;
        bool partial = (long)fullW * fullH > MaxFullPagePixels;
        if (partial)
        {
            // deep zoom: rasterise just what is (nearly) on screen
            var want = Rect.Intersect(pageRect, Rect.Inflate(view, view.Width * 0.3, view.Height * 0.3));
            if (want.IsEmpty) return;
            int x0 = Math.Max(0, AlignDown((want.Left - pageRect.Left) * dpi, 64));
            int y0 = Math.Max(0, AlignDown((want.Top - pageRect.Top) * dpi, 64));
            int x1 = Math.Min(fullW, AlignUp((want.Right - pageRect.Left) * dpi, 64));
            int y1 = Math.Min(fullH, AlignUp((want.Bottom - pageRect.Top) * dpi, 64));
            x = x0; y = y0; w = Math.Max(1, x1 - x0); h = Math.Max(1, y1 - y0);

            // the bitmap on screen already covers what is visible: keep it, no re-render at every scroll step
            if (slot.ShownKey is { } shown && shown.FullW == fullW && shown.FullH == fullH && shown.Rotation == _rotation && shown.Invert == _invert)
            {
                var vis = Rect.Intersect(pageRect, view);
                int vx0 = (int)Math.Floor((vis.Left - pageRect.Left) * dpi), vy0 = (int)Math.Floor((vis.Top - pageRect.Top) * dpi);
                int vx1 = (int)Math.Ceiling((vis.Right - pageRect.Left) * dpi), vy1 = (int)Math.Ceiling((vis.Bottom - pageRect.Top) * dpi);
                if (vx0 >= shown.X && vy0 >= shown.Y && vx1 <= shown.X + shown.W && vy1 <= shown.Y + shown.H) return;
            }
        }

        var key = new RenderKey(slot.PageIndex, fullW, fullH, x, y, w, h, _rotation, _invert);
        if (slot.ShownKey == key || slot.PendingKey == key) return;

        if (_renderer.TryGetCached(_pdf, key, out var cached))
        {
            slot.CancelRender();
            slot.ShowBitmap(cached, key);
            return;
        }

        slot.CancelRender();
        var cts = new CancellationTokenSource();
        slot.Cts = cts;
        slot.PendingKey = key;
        var pdf = _pdf;
        // a page that appears empty on screen gets a quick half-size draft first (a quarter of the work), the full render follows
        if (priority == 0 && slot.ShownKey == null && !partial && (long)fullW * fullH > 600_000)
        {
            var draft = new RenderKey(slot.PageIndex, Math.Max(1, fullW / 2), Math.Max(1, fullH / 2), 0, 0, Math.Max(1, fullW / 2), Math.Max(1, fullH / 2), _rotation, _invert);
            _ = AwaitDraft(slot, pdf, draft, cts);
        }
        _ = AwaitRender(slot, pdf, key, priority, cts);
    }

    async Task AwaitDraft(PageSlot slot, PdfFile pdf, RenderKey draft, CancellationTokenSource cts)
    {
        try
        {
            var bmp = await _renderer.RenderAsync(pdf, draft, -1, cts.Token);
            if (bmp == null || cts.IsCancellationRequested || _pdf != pdf) return;
            // only while the page is still blank: the full render may already have arrived
            if (_slots.TryGetValue(slot.PageIndex, out var live) && live == slot && slot.ShownKey == null) slot.ShowBitmap(bmp, draft);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("draft failed: " + ex.Message); }
    }

    async Task AwaitRender(PageSlot slot, PdfFile pdf, RenderKey key, int priority, CancellationTokenSource cts)
    {
        try
        {
            var bmp = await _renderer.RenderAsync(pdf, key, priority, cts.Token);
            if (bmp == null || cts.IsCancellationRequested || _pdf != pdf) return;
            if (!_slots.TryGetValue(slot.PageIndex, out var live) || live != slot) return;
            slot.PendingKey = null;
            slot.ShowBitmap(bmp, key);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("render failed: " + ex.Message);
            if (slot.PendingKey == key) slot.PendingKey = null;
        }
    }

    static int AlignDown(double v, int step) => (int)Math.Floor(v / step) * step;
    static int AlignUp(double v, int step) => (int)Math.Ceiling(v / step) * step;

    /// <summary>Rect of a page in canvas coordinates (Rect.Empty when the page is not laid out).</summary>
    public Rect PageRect(int page) => _result == null || page < 0 || page >= _result.PageRects.Length ? Rect.Empty : _result.PageRects[page];
}
