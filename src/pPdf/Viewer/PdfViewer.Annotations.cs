using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using pPdf.Annotations;

namespace pPdf.Viewer;

/// <summary>Adding, selecting and editing annotations on the pages.</summary>
public sealed partial class PdfViewer
{
    Annotation? _selectedAnnotation;

    public AnnotationStore Annotations { get; } = new();

    /// <summary>Style given to new text annotations (updated when the user restyles one).</summary>
    public TextAnnotation TextDefaults { get; } = new() { FontFamily = "Arial", FontSize = 14 };

    public event EventHandler? SelectedAnnotationChanged;

    public Annotation? SelectedAnnotation
    {
        get => _selectedAnnotation;
        set
        {
            if (_selectedAnnotation == value) return;
            // leaving a text box that is being edited ends the edit
            if (_selectedAnnotation != null && ControlFor(_selectedAnnotation) is { IsEditing: true } editing) editing.EndEdit();
            _selectedAnnotation = value;
            foreach (var slot in _slots.Values)
                foreach (var c in slot.AnnotationLayer.Children.OfType<AnnotationControl>())
                    c.IsSelected = c.Model == value;
            if (value != null) ClearSelection();
            SelectedAnnotationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    void InitAnnotations()
    {
        Annotations.Added += OnAnnotationAdded;
        Annotations.Removed += OnAnnotationRemoved;
        Drop += OnViewerDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
    }

    AnnotationControl? ControlFor(Annotation a)
        => _slots.TryGetValue(a.Page, out var slot) ? slot.AnnotationLayer.Children.OfType<AnnotationControl>().FirstOrDefault(c => c.Model == a) : null;

    void PopulateAnnotations(PageSlot slot)
    {
        foreach (var a in Annotations.OnPage(slot.PageIndex))
            if (!slot.AnnotationLayer.Children.OfType<AnnotationControl>().Any(c => c.Model == a))
                AddControl(slot, a, pending: false);
    }

    AnnotationControl AddControl(PageSlot slot, Annotation a, bool pending)
    {
        var ac = new AnnotationControl(a, Annotations) { ViewScale = slot.Scale, IsPending = pending, IsSelected = a == _selectedAnnotation };
        ac.SelectRequested += OnAnnotationSelectRequested;
        ac.EditEnded += OnAnnotationEditEnded;
        // highlights and lines sit under text boxes and images
        if (a is MarkupAnnotation) slot.AnnotationLayer.Children.Insert(0, ac); else slot.AnnotationLayer.Children.Add(ac);
        return ac;
    }

    void OnAnnotationSelectRequested(AnnotationControl ac)
    {
        Focus();
        SelectedAnnotation = ac.Model;
        // the selected one goes on top of its neighbours
        var layer = (Canvas)ac.Parent;
        layer.Children.Remove(ac);
        layer.Children.Add(ac);
        Annotations.BringToFront(ac.Model);
    }

    void OnAnnotationAdded(Annotation a)
    {
        if (_slots.TryGetValue(a.Page, out var slot) && ControlFor(a) == null) AddControl(slot, a, pending: false);
    }

    void OnAnnotationRemoved(Annotation a)
    {
        if (ControlFor(a) is { } ac)
        {
            ((Canvas)ac.Parent).Children.Remove(ac);
        }
        if (_selectedAnnotation == a) SelectedAnnotation = null;
    }

    void OnAnnotationEditEnded(AnnotationControl ac, bool empty)
    {
        if (ac.IsPending)
        {
            if (empty)
            {
                ((Canvas)ac.Parent).Children.Remove(ac);
                if (_selectedAnnotation == ac.Model) SelectedAnnotation = null;
                return;
            }
            ac.IsPending = false;
            Annotations.Add(ac.Model);
            return;
        }
        if (empty) Annotations.Remove(ac.Model);
    }

    // ------------------------------------------------------------------ text

    void BeginNewText(PageSlot slot, Point pagePoint)
    {
        var t = new TextAnnotation
        {
            Page = slot.PageIndex,
            X = pagePoint.X,
            Y = pagePoint.Y,
            FontFamily = TextDefaults.FontFamily,
            FontSize = TextDefaults.FontSize,
            Bold = TextDefaults.Bold,
            Italic = TextDefaults.Italic,
            Foreground = TextDefaults.Foreground,
            Background = TextDefaults.Background,
        };
        // the caret should sit where the user clicked
        t.X = Math.Max(0, t.X - TextAnnotation.Padding);
        t.Y = Math.Max(0, t.Y - t.FontSize * 0.6);
        var ac = AddControl(slot, t, pending: true);
        SelectedAnnotation = t;
        Tool = ViewerTool.Select;
        ac.BeginEdit();
    }

    /// <summary>Adds a text box in the middle of the visible part of the current page (menu / shortcut use).</summary>
    public void AddTextAtCenter()
    {
        if (!TryCenterPoint(out var slot, out var pp)) return;
        BeginNewText(slot, pp);
    }

    bool TryCenterPoint(out PageSlot slot, out Point pagePoint)
    {
        slot = null!;
        pagePoint = default;
        if (_pdf == null || _result == null) return false;
        var center = new Point(_scroll.HorizontalOffset + _scroll.ViewportWidth / 2, _scroll.VerticalOffset + _scroll.ViewportHeight / 2);
        return TryHit(center, out slot, out pagePoint) || TryNearest(center, out slot, out pagePoint);
    }

    /// <summary>Applies a change to the selected annotation as one undoable step; also remembers text styles as the new defaults.</summary>
    public void ModifySelected(Action<Annotation> change)
    {
        if (_selectedAnnotation is not { } a) return;
        var before = a.Clone();
        change(a);
        Annotations.Commit(a, before);
        if (a is TextAnnotation t)
        {
            TextDefaults.FontFamily = t.FontFamily;
            TextDefaults.FontSize = t.FontSize;
            TextDefaults.Bold = t.Bold;
            TextDefaults.Italic = t.Italic;
            TextDefaults.Foreground = t.Foreground;
            TextDefaults.Background = t.Background;
        }
    }

    /// <summary>Ends any text box being typed in, so its text is part of the annotations (before saving, printing, closing).</summary>
    public void CommitEdits()
    {
        foreach (var slot in _slots.Values.ToArray())
            foreach (var c in slot.AnnotationLayer.Children.OfType<AnnotationControl>().ToArray())
                if (c.IsEditing) c.EndEdit(restoreFocus: false);
    }

    public void DeleteSelected()
    {
        if (_selectedAnnotation is { } a)
        {
            SelectedAnnotation = null;
            Annotations.Remove(a);
        }
    }

    public void EditSelectedText()
    {
        if (_selectedAnnotation is TextAnnotation && ControlFor(_selectedAnnotation) is { } c) c.BeginEdit();
    }

    // ------------------------------------------------------------------ images

    static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".jxr", ".ico"];

    public static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public bool AddImageFile(string path, Point? viewerPoint = null)
    {
        try { return AddImage(ImageAnnotation.FromBytes(File.ReadAllBytes(path)), viewerPoint); }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Places an image on the page under <paramref name="viewerPoint"/> (or in the middle of the view) and selects it.</summary>
    public bool AddImage(ImageAnnotation img, Point? viewerPoint = null)
    {
        if (_pdf == null || _result == null) return false;
        PageSlot slot;
        Point pp;
        if (viewerPoint is { } vp)
        {
            var cp = _canvas.TranslatePoint(new Point(), this); // canvas origin inside the viewer
            var canvasPt = new Point(vp.X - cp.X, vp.Y - cp.Y);
            if (!TryHit(canvasPt, out slot, out pp) && !TryNearest(canvasPt, out slot, out pp)) return false;
        }
        else if (!TryCenterPoint(out slot, out pp)) return false;

        var page = slot.PointSize;
        double w = img.NaturalSize.Width * 0.75, h = img.NaturalSize.Height * 0.75; // 96 dpi pixels -> points
        double fit = Math.Min(1, Math.Min(page.Width * 0.45 / w, page.Height * 0.45 / h));
        w *= fit; h *= fit;
        img.Page = slot.PageIndex;
        img.Width = w;
        img.Height = h;
        img.X = Math.Clamp(pp.X - w / 2, 0, Math.Max(0, page.Width - w));
        img.Y = Math.Clamp(pp.Y - h / 2, 0, Math.Max(0, page.Height - h));
        Annotations.Add(img);
        SelectedAnnotation = img;
        Focus();
        return true;
    }

    /// <summary>Pastes an image, or a piece of text, from the clipboard as an annotation. Returns false if there is nothing to paste.</summary>
    public bool PasteFromClipboard()
    {
        if (_pdf == null) return false;
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } bmp)
            {
                bmp.Freeze();
                return AddImage(ImageAnnotation.FromBitmap(bmp));
            }
            if (Clipboard.ContainsFileDropList())
            {
                foreach (string? f in Clipboard.GetFileDropList())
                    if (f != null && IsImageFile(f)) return AddImageFile(f);
            }
            if (Clipboard.ContainsText() && Clipboard.GetText() is { Length: > 0 } text)
            {
                if (!TryCenterPoint(out var slot, out var pp)) return false;
                var t = new TextAnnotation
                {
                    Page = slot.PageIndex, X = pp.X, Y = pp.Y, Text = text.TrimEnd(),
                    FontFamily = TextDefaults.FontFamily, FontSize = TextDefaults.FontSize, Bold = TextDefaults.Bold,
                    Italic = TextDefaults.Italic, Foreground = TextDefaults.Foreground, Background = TextDefaults.Background,
                };
                Annotations.Add(t);
                SelectedAnnotation = t;
                return true;
            }
        }
        catch (System.Runtime.InteropServices.COMException) { }
        return false;
    }

    void OnViewerDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var at = e.GetPosition(this);
        bool any = false;
        foreach (var f in files.Where(IsImageFile)) any |= AddImageFile(f, at);
        if (any) e.Handled = true;
    }

    // ------------------------------------------------------------------ keys

    bool HandleAnnotationKey(KeyEventArgs e, bool shift)
    {
        if (_selectedAnnotation is not { } a) return false;
        if (ControlFor(a) is { IsEditing: true }) return false;
        double step = shift ? 10 : 1;
        switch (e.Key)
        {
            case Key.Delete:
            case Key.Back:
                DeleteSelected();
                return true;
            case Key.Enter:
            case Key.F2:
                if (a is TextAnnotation) { EditSelectedText(); return true; }
                return false;
            case Key.Left when a is not MarkupAnnotation: Nudge(a, -step, 0); return true;
            case Key.Right when a is not MarkupAnnotation: Nudge(a, step, 0); return true;
            case Key.Up when a is not MarkupAnnotation: Nudge(a, 0, -step); return true;
            case Key.Down when a is not MarkupAnnotation: Nudge(a, 0, step); return true;
        }
        return false;
    }

    void Nudge(Annotation a, double dx, double dy)
    {
        // the arrows move on screen, so turn the step into page space for the current rotation
        (dx, dy) = (_rotation & 3) switch
        {
            1 => (dy, -dx),
            2 => (-dx, -dy),
            3 => (-dy, dx),
            _ => (dx, dy),
        };
        var before = a.Clone();
        a.X += dx;
        a.Y += dy;
        Annotations.Commit(a, before);
    }
}
