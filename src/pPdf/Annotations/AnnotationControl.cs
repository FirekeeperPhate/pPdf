using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace pPdf.Annotations;

public enum Handle { None, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>
/// The on-screen element of one annotation, living in the page-space canvas of a page (so zoom and rotation come for free).
/// It handles its own moving, resizing and (for text) in-place editing.
/// </summary>
public sealed class AnnotationControl : Grid
{
    const double MinSize = 10;
    const double HandlePx = 9;

    readonly AnnotationStore _store;
    readonly SelectionChrome _chrome;
    readonly FrameworkElement _content;
    TextBox? _editor;
    Annotation? _editBefore;

    // drag state
    enum Drag { None, Move, Resize }
    Drag _drag;
    Handle _handle;
    Point _dragStart;
    Rect _startBounds;
    double _startFontSize;
    Annotation? _dragBefore;

    public Annotation Model { get; }
    /// <summary>True until the first non-empty text is committed (a new text box that is not yet part of the document).</summary>
    public bool IsPending { get; set; }

    /// <summary>Raised on a click: the viewer makes this the selected annotation.</summary>
    public event Action<AnnotationControl>? SelectRequested;
    /// <summary>Raised when editing ends; the argument is true if the text box was empty and should go away.</summary>
    public event Action<AnnotationControl, bool>? EditEnded;

    double _scale = 1;
    bool _selected;

    public double ViewScale
    {
        get => _scale;
        set { _scale = value <= 0 ? 1 : value; _chrome.Scale = _scale; if (_content is MarkupVisual mv) mv.Scale = _scale; }
    }

    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            // a markup shows its own dashed outline instead of a box with handles
            if (_content is MarkupVisual mv) mv.IsSelected = value; else _chrome.IsSelected = value;
        }
    }

    public bool IsEditing => _editor != null;

    public AnnotationControl(Annotation model, AnnotationStore store)
    {
        Model = model;
        _store = store;
        Background = Brushes.Transparent;
        Cursor = Cursors.SizeAll;
        SnapsToDevicePixels = false;
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

        _content = model switch
        {
            TextAnnotation => new TextBlock { TextWrapping = TextWrapping.NoWrap, Padding = new Thickness(TextAnnotation.Padding) },
            ImageAnnotation => new Image { Stretch = Stretch.Fill },
            MarkupAnnotation m => new MarkupVisual(m),
            _ => throw new NotSupportedException(),
        };
        if (_content is Image) RenderOptions.SetBitmapScalingMode(_content, BitmapScalingMode.HighQuality);
        // a markup is only a picture over the text: the viewer handles the clicks on it
        if (model is MarkupAnnotation) { IsHitTestVisible = false; Background = null; }
        Children.Add(_content);
        _chrome = new SelectionChrome(this);
        Children.Add(_chrome);

        model.PropertyChanged += OnModelChanged;
        Unloaded += (_, _) => model.PropertyChanged -= OnModelChanged;
        Loaded += (_, _) => { model.PropertyChanged -= OnModelChanged; model.PropertyChanged += OnModelChanged; };
        Refresh();
        SizeChanged += (_, e) =>
        {
            if (Model is TextAnnotation && e.NewSize.Width > 0)
            {
                Model.Width = e.NewSize.Width;
                Model.Height = e.NewSize.Height;
            }
        };
    }

    void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    void Refresh()
    {
        Canvas.SetLeft(this, Model.X);
        Canvas.SetTop(this, Model.Y);
        switch (Model)
        {
            case TextAnnotation t:
                var tb = (TextBlock)_content;
                tb.Text = t.Text;
                tb.FontFamily = new FontFamily(t.FontFamily);
                tb.FontSize = t.FontSize;
                tb.FontWeight = t.Bold ? FontWeights.Bold : FontWeights.Normal;
                tb.FontStyle = t.Italic ? FontStyles.Italic : FontStyles.Normal;
                tb.Foreground = new SolidColorBrush(t.Foreground);
                tb.Background = t.Background is { } bg ? new SolidColorBrush(bg) : null;
                if (_editor != null) ApplyEditorStyle(_editor, t);
                break;
            case ImageAnnotation i:
                ((Image)_content).Source = i.Source;
                Width = Math.Max(1, i.Width);
                Height = Math.Max(1, i.Height);
                break;
            case MarkupAnnotation mk:
                Width = Math.Max(1, mk.Width);
                Height = Math.Max(1, mk.Height);
                ((MarkupVisual)_content).InvalidateVisual();
                break;
        }
        _chrome.InvalidateVisual();
    }

    // ------------------------------------------------------------------ text editing

    static void ApplyEditorStyle(TextBox box, TextAnnotation t)
    {
        box.FontFamily = new FontFamily(t.FontFamily);
        box.FontSize = t.FontSize;
        box.FontWeight = t.Bold ? FontWeights.Bold : FontWeights.Normal;
        box.FontStyle = t.Italic ? FontStyles.Italic : FontStyles.Normal;
        box.Foreground = new SolidColorBrush(t.Foreground);
        box.Background = t.Background is { } bg ? new SolidColorBrush(bg) : Brushes.Transparent;
        box.CaretBrush = box.Foreground;
    }

    public void BeginEdit()
    {
        if (Model is not TextAnnotation t || _editor != null) return;
        _editBefore = t.Clone();
        var box = new TextBox
        {
            Text = t.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(TextAnnotation.Padding),
            MinWidth = 24,
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Cursor = Cursors.IBeam,
            SelectionBrush = SystemColors.HighlightBrush,
        };
        if (TryFindResource("AnnotationEditBox") is Style style) box.Style = style;
        ApplyEditorStyle(box, t);
        box.LostKeyboardFocus += OnEditorLostFocus;
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
            {
                e.Handled = true;
                EndEdit();
            }
        };
        _editor = box;
        _content.Visibility = Visibility.Hidden;
        Children.Insert(1, box);
        Cursor = Cursors.IBeam;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
            if (!IsPending) box.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // focus moving inside the box (context menu...) is not the end of editing
        if (_editor != null && !_editor.IsKeyboardFocusWithin && e.NewFocus is not System.Windows.Controls.ContextMenu) EndEdit(restoreFocus: false);
    }

    public void EndEdit(bool restoreFocus = true)
    {
        if (_editor is not { } box || Model is not TextAnnotation t) return;
        _editor = null;
        box.LostKeyboardFocus -= OnEditorLostFocus;
        string text = box.Text;
        Children.Remove(box);
        _content.Visibility = Visibility.Visible;
        Cursor = Cursors.SizeAll;
        t.Text = text;

        bool empty = string.IsNullOrWhiteSpace(text);
        if (!IsPending && !empty && _editBefore != null) _store.Commit(t, _editBefore);
        _editBefore = null;
        EditEnded?.Invoke(this, empty);
        // give the keyboard back to whoever hosts the page (so Delete, arrows... keep working)
        if (restoreFocus) Keyboard.Focus(FindHost());
    }

    IInputElement? FindHost()
    {
        DependencyObject? p = this;
        while (p != null)
        {
            p = VisualTreeHelper.GetParent(p);
            if (p is UIElement { Focusable: true } ui) return ui;
        }
        return null;
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_editor != null) return; // the text box takes it
        e.Handled = true;
        SelectRequested?.Invoke(this);

        if (e.ClickCount == 2 && Model is TextAnnotation)
        {
            BeginEdit();
            return;
        }

        var local = e.GetPosition(this);
        _handle = _selected ? _chrome.HandleAt(local) : Handle.None;
        _dragStart = e.GetPosition((IInputElement)Parent);
        _startBounds = new Rect(Model.X, Model.Y, Math.Max(1, Model.Width), Math.Max(1, Model.Height));
        _startFontSize = (Model as TextAnnotation)?.FontSize ?? 0;
        _dragBefore = Model.Clone();
        _drag = _handle == Handle.None ? Drag.Move : Drag.Resize;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_editor != null) return;
        if (_drag == Drag.None)
        {
            if (_selected) Cursor = CursorFor(_chrome.HandleAt(e.GetPosition(this)));
            return;
        }
        var p = e.GetPosition((IInputElement)Parent);
        var delta = p - _dragStart;
        if (_drag == Drag.Move)
        {
            Model.X = _startBounds.X + delta.X;
            Model.Y = _startBounds.Y + delta.Y;
        }
        else
        {
            Resize(delta);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => EndDrag();

    // the capture can be lost without a button release (Alt+Tab, a dialog): the drag ends there, it must not follow the mouse afterwards
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }

    void EndDrag()
    {
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (_dragBefore != null) _store.Commit(Model, _dragBefore);
        _dragBefore = null;
    }

    static Cursor CursorFor(Handle h) => h switch
    {
        Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
        Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
        Handle.Top or Handle.Bottom => Cursors.SizeNS,
        Handle.Left or Handle.Right => Cursors.SizeWE,
        _ => Cursors.SizeAll,
    };

    void Resize(Vector delta)
    {
        var r = _startBounds;
        bool left = _handle is Handle.Left or Handle.TopLeft or Handle.BottomLeft;
        bool right = _handle is Handle.Right or Handle.TopRight or Handle.BottomRight;
        bool top = _handle is Handle.Top or Handle.TopLeft or Handle.TopRight;
        bool bottom = _handle is Handle.Bottom or Handle.BottomLeft or Handle.BottomRight;
        bool corner = (left || right) && (top || bottom);

        double x = r.X, y = r.Y, w = r.Width, h = r.Height;
        if (left) { x = r.X + delta.X; w = r.Width - delta.X; }
        if (right) w = r.Width + delta.X;
        if (top) { y = r.Y + delta.Y; h = r.Height - delta.Y; }
        if (bottom) h = r.Height + delta.Y;

        if (Model is TextAnnotation t)
        {
            // text scales as a whole: dragging a corner changes the font size
            double s = corner ? Math.Max(w / r.Width, h / r.Height)
                     : (left || right) ? w / r.Width : h / r.Height;
            s = Math.Clamp(s, 4 / Math.Max(1, _startFontSize), 400 / Math.Max(1, _startFontSize));
            t.FontSize = Math.Round(_startFontSize * s, 1);
            double nw = r.Width * s, nh = r.Height * s;
            t.X = left ? r.Right - nw : r.X;
            t.Y = top ? r.Bottom - nh : r.Y;
            return;
        }

        if (corner)
        {
            // images keep their proportions on corners; hold Ctrl to stretch freely
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                double s = Math.Max(w / r.Width, h / r.Height);
                s = Math.Max(s, MinSize / Math.Min(r.Width, r.Height));
                w = r.Width * s; h = r.Height * s;
            }
        }
        w = Math.Max(MinSize, w);
        h = Math.Max(MinSize, h);
        if (left) x = r.Right - w;
        if (top) y = r.Bottom - h;
        Model.X = x; Model.Y = y; Model.Width = w; Model.Height = h;
    }
}

/// <summary>Draws the selection outline and the eight resize handles of an annotation, at a constant size on screen.</summary>
sealed class SelectionChrome : FrameworkElement
{
    readonly AnnotationControl _owner;
    bool _selected;
    double _scale = 1;

    public SelectionChrome(AnnotationControl owner)
    {
        _owner = owner;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        IsHitTestVisible = true;
    }

    public bool IsSelected { get => _selected; set { _selected = value; InvalidateVisual(); } }
    public double Scale { get => _scale; set { _scale = value; InvalidateVisual(); } }

    double HandleSize => 9 / _scale;
    bool CornersOnly => _owner.Model is TextAnnotation;

    IEnumerable<(Handle H, Point P)> Handles()
    {
        double w = ActualWidth, h = ActualHeight;
        yield return (Handle.TopLeft, new Point(0, 0));
        yield return (Handle.TopRight, new Point(w, 0));
        yield return (Handle.BottomRight, new Point(w, h));
        yield return (Handle.BottomLeft, new Point(0, h));
        if (CornersOnly) yield break;
        yield return (Handle.Top, new Point(w / 2, 0));
        yield return (Handle.Right, new Point(w, h / 2));
        yield return (Handle.Bottom, new Point(w / 2, h));
        yield return (Handle.Left, new Point(0, h / 2));
    }

    /// <summary>The handle under a point given in this element's coordinates.</summary>
    public Handle HandleAt(Point p)
    {
        if (!_selected) return Handle.None;
        double r = HandleSize * 0.75;
        foreach (var (hd, pt) in Handles())
            if (Math.Abs(p.X - pt.X) <= r && Math.Abs(p.Y - pt.Y) <= r) return hd;
        return Handle.None;
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
        => _selected && HandleAt(hitTestParameters.HitPoint) != Handle.None ? new PointHitTestResult(this, hitTestParameters.HitPoint) : null;

    protected override void OnRender(DrawingContext dc)
    {
        if (!_selected) return;
        var accent = Color.FromRgb(0x2F, 0x80, 0xED);
        var pen = new Pen(new SolidColorBrush(accent), 1.5 / _scale);
        pen.Freeze();
        dc.DrawRectangle(null, pen, new Rect(0, 0, ActualWidth, ActualHeight));
        var fill = Brushes.White;
        double s = HandleSize;
        foreach (var (_, pt) in Handles())
            dc.DrawRectangle(fill, pen, new Rect(pt.X - s / 2, pt.Y - s / 2, s, s));
    }
}
