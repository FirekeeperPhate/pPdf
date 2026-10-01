using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pPdf.Pdf;

namespace pPdf.Viewer;

/// <summary>Draws a set of rectangles (selection, search hits) in page space.</summary>
sealed class RectsLayer : FrameworkElement
{
    IReadOnlyList<RectF> _rects = [];
    IReadOnlyList<RectF> _accent = [];
    Brush _brush = Brushes.Transparent;
    Brush _accentBrush = Brushes.Transparent;

    public RectsLayer() { IsHitTestVisible = false; }

    public void Set(IReadOnlyList<RectF> rects, Brush brush, IReadOnlyList<RectF>? accent = null, Brush? accentBrush = null)
    {
        _rects = rects; _brush = brush;
        _accent = accent ?? []; _accentBrush = accentBrush ?? brush;
        InvalidateVisual();
    }

    public bool IsEmptyLayer => _rects.Count == 0 && _accent.Count == 0;

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var r in _rects) dc.DrawRectangle(_brush, null, r.ToRect());
        foreach (var r in _accent) dc.DrawRectangle(_accentBrush, null, r.ToRect());
    }
}

/// <summary>One page on the canvas: paper, rendered bitmap and the page-space overlay (selection, hits, annotations).</summary>
sealed class PageSlot : Canvas
{
    public int PageIndex { get; }
    public PageSize PointSize { get; }

    readonly Image _bitmap = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    readonly Border _paper = new() { Background = Brushes.White, IsHitTestVisible = true };

    /// <summary>Page space: points from the top-left of the page, before the viewer rotation. Scaled and rotated by a transform.</summary>
    public Canvas Overlay { get; } = new() { ClipToBounds = true, Background = Brushes.Transparent };
    public RectsLayer SearchLayer { get; } = new();
    public RectsLayer SelectionLayer { get; } = new();
    /// <summary>The controls of the form fields on this page (page space), under the annotations.</summary>
    public Canvas FormLayer { get; } = new();
    public Canvas AnnotationLayer { get; } = new();

    public double Scale { get; private set; } = 1;
    public int Rotation { get; private set; }

    // rendering state, owned by the viewer
    public RenderKey? ShownKey { get; private set; }
    public RenderKey? PendingKey { get; set; }
    public CancellationTokenSource? Cts { get; set; }
    public bool TextRequested { get; set; }

    public PageSlot(int pageIndex, PageSize size)
    {
        PageIndex = pageIndex;
        PointSize = size;
        SnapsToDevicePixels = true;
        Children.Add(_paper);
        Children.Add(_bitmap);
        Overlay.Width = size.Width;
        Overlay.Height = size.Height;
        Overlay.Children.Add(SearchLayer);
        Overlay.Children.Add(SelectionLayer);
        Overlay.Children.Add(FormLayer);
        Overlay.Children.Add(AnnotationLayer);
        Children.Add(Overlay);
        RenderOptions.SetBitmapScalingMode(_bitmap, BitmapScalingMode.HighQuality);
    }

    public void SetPaper(Brush brush) => _paper.Background = brush;

    public void SetGeometry(Rect rect, double scale, int rotation)
    {
        Canvas.SetLeft(this, rect.X);
        Canvas.SetTop(this, rect.Y);
        Width = rect.Width;
        Height = rect.Height;
        _paper.Width = rect.Width;
        _paper.Height = rect.Height;
        bool changed = scale != Scale || rotation != Rotation;
        Scale = scale;
        Rotation = rotation;
        if (changed || Overlay.RenderTransform is not MatrixTransform) Overlay.RenderTransform = new MatrixTransform(OverlayMatrix(PointSize, scale, rotation));
        foreach (var child in AnnotationLayer.Children)
            if (child is Annotations.AnnotationControl ac) ac.ViewScale = scale;
        PlaceBitmap();
    }

    /// <summary>Maps page space (points) to this slot's DIP space for a given scale and rotation (quarter turns clockwise).</summary>
    public static Matrix OverlayMatrix(PageSize size, double s, int rotation)
    {
        double sw = size.Width * s, sh = size.Height * s;
        return (rotation & 3) switch
        {
            1 => new Matrix(0, s, -s, 0, sh, 0),
            2 => new Matrix(-s, 0, 0, -s, sw, sh),
            3 => new Matrix(0, -s, s, 0, 0, sw),
            _ => new Matrix(s, 0, 0, s, 0, 0),
        };
    }

    public Point ToPagePoint(Point inSlot)
    {
        var m = ((MatrixTransform)Overlay.RenderTransform).Matrix;
        m.Invert();
        return m.Transform(inSlot);
    }

    public Rect ToSlotRect(Rect pageRect)
    {
        var m = ((MatrixTransform)Overlay.RenderTransform).Matrix;
        return Rect.Transform(pageRect, m);
    }

    public void ShowBitmap(BitmapSource bitmap, RenderKey key)
    {
        _bitmap.Source = bitmap;
        ShownKey = key;
        PlaceBitmap();
    }

    public void ClearBitmap()
    {
        _bitmap.Source = null;
        ShownKey = null;
    }

    /// <summary>Stretches the bitmap over the area it was rendered for, at the current size (it may be from another zoom).</summary>
    public void PlaceBitmap()
    {
        if (ShownKey is not { } k || _bitmap.Source == null) return;
        if (k.Rotation != Rotation) { _bitmap.Visibility = Visibility.Hidden; return; }
        _bitmap.Visibility = Visibility.Visible;
        double w = Width, h = Height;
        Canvas.SetLeft(_bitmap, k.X / (double)k.FullW * w);
        Canvas.SetTop(_bitmap, k.Y / (double)k.FullH * h);
        _bitmap.Width = k.W / (double)k.FullW * w;
        _bitmap.Height = k.H / (double)k.FullH * h;
    }

    public void CancelRender()
    {
        Cts?.Cancel();
        Cts = null;
        PendingKey = null;
    }
}
